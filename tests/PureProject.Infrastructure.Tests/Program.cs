using System.Net;
using System.Text;
using System.Text.Json;
using PureProject.Core;
using PureProject.Infrastructure;

var passed = 0;
var testRoot = Path.Combine(Path.GetTempPath(), "PureProject.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
try
{
    var original = SampleProject();
    using (var repository = new JsonProjectRepository(Path.Combine(testRoot, "data")))
    {
        Check((await repository.LoadAsync()).Count == 0, "new repository starts empty");
        await repository.SaveAsync([original]);
        Check(File.Exists(repository.DataFilePath), "first save creates primary data");
        Expect<RepositoryInUseException>(() => { using var second = new JsonProjectRepository(repository.DataDirectory); }, "second instance cannot acquire same data directory");
        var updated = PmSerializer.Clone(original);
        updated.Name = "已更新";
        await repository.SaveAsync([updated]);
        Check(PmSerializer.ParseProjects(await File.ReadAllTextAsync(repository.BackupFilePath))[0].Name == "迁移验收", "atomic replacement retains previous valid backup");

        if (OperatingSystem.IsWindows())
        {
            using (var blocker = new FileStream(repository.DataFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await ExpectAsync<IOException>(() => repository.SaveAsync([original]), "locked destination reports write failure");
            Check((await repository.LoadAsync())[0].Name == "已更新", "failed write preserves primary data");
        }

        await File.WriteAllTextAsync(repository.DataFilePath, "{ damaged");
        await ExpectAsync<RepositoryDataException>(() => repository.LoadAsync(), "corrupt main data is reported");
        await ExpectAsync<InvalidOperationException>(() => repository.SaveAsync([]), "failed load cannot overwrite main data with empty list");
        Check(await File.ReadAllTextAsync(repository.DataFilePath) == "{ damaged", "corrupt original retained until explicit recovery");
        var restored = await repository.RestoreBackupAsync();
        Check(restored[0].Name == "迁移验收", "explicit restore uses known good backup");
        Check(Directory.GetFiles(repository.DataDirectory, "projects.before-restore.*.json").Length == 1, "recovery retains corrupt source for inspection");
        Check(!Directory.GetFiles(repository.DataDirectory, "*.tmp").Any(), "temporary files are cleaned after writes");
    }

    var exportPath = Path.Combine(testRoot, "export.pm");
    await JsonProjectRepository.ExportProjectAsync(exportPath, original);
    Check((await JsonProjectRepository.ImportAsync(exportPath))[0].Id == original.Id, "pm export and import round trip");
    var backupPath = Path.Combine(testRoot, "backup.json");
    await JsonProjectRepository.ExportBackupAsync(backupPath, [original]);
    Check((await JsonProjectRepository.ImportAsync(backupPath)).Count == 1, "full backup import round trip");

    if (OperatingSystem.IsWindows())
    {
        var settingsRepository = new SettingsRepository(Path.Combine(testRoot, "settings"));
        var settings = new AppSettings { Theme = "Dark", SyncServerUrl = "https://sync.example.test", SyncToken = "secret-that-must-never-appear-in-json" };
        await settingsRepository.SaveAsync(settings);
        Check(!File.ReadAllText(settingsRepository.FilePath).Contains(settings.SyncToken, StringComparison.Ordinal), "credentials do not appear in settings JSON");
        var loaded = await settingsRepository.LoadAsync();
        Check(loaded.SyncToken == settings.SyncToken && loaded.Theme == "Dark", "current-user DPAPI credentials round trip");
    }

    var syncSettings = new AppSettings { SyncServerUrl = "https://sync.example.test", SyncToken = "bearer-test" };
    var conflictRequests = 0;
    using (var http = new HttpClient(new Handler(request =>
    {
        conflictRequests++;
        Check(request.Headers.GetValues("X-Base-Rev").Single() == "7", "upload carries expected base revision");
        Check(request.Headers.Authorization?.Parameter == "bearer-test", "sync authenticates with bearer header");
        return Json(new { conflict = true, reason = "revision_conflict", serverRev = 8, serverData = "remote", deleted = false }, HttpStatusCode.Conflict);
    })))
    using (var client = new RestSyncClient(http))
    {
        var result = await client.PushAsync(syncSettings, original, 7);
        Check(!result.Succeeded && result.Conflict?.ServerRev == 8 && conflictRequests == 1, "409 remains conflict without retry or overwrite");
    }

    using (var http = new HttpClient(new Handler(_ => Json(new { rev = 8, schema_version = 5, data = PmSerializer.ExportPm(original) }))))
    using (var client = new RestSyncClient(http))
        await ExpectAsync<InvalidDataException>(() => client.PullAsync(syncSettings, original.Id), "future server schema is rejected before application");

    using (var http = new HttpClient(new Handler(_ => Json(new { rev = 8, schema_version = 4, data = PmSerializer.ExportPm(original) }))))
    using (var client = new RestSyncClient(http))
        await ExpectAsync<InvalidDataException>(() => client.PullAsync(syncSettings, "another-id"), "remote ID mismatch is rejected");

    var remote = PmSerializer.Clone(original);
    remote.Name = "远程更新";
    var putRequests = 0;
    using (var http = new HttpClient(new Handler(request =>
    {
        if (request.Method == HttpMethod.Put) { putRequests++; return Json(new { rev = 9, schema_version = 4 }); }
        return request.RequestUri!.AbsolutePath.EndsWith("/api/projects", StringComparison.Ordinal)
            ? Json(new { rev = 8, projects = new[] { new { id = original.Id, name = remote.Name, rev = 8, schema_version = 4 } } })
            : Json(new { rev = 8, schema_version = 4, data = PmSerializer.ExportPm(remote) });
    })))
    using (var client = new RestSyncClient(http))
    {
        var service = new ManualSyncService(client);
        var baselineSettings = syncSettings with
        {
            SyncStateServerUrl = "https://sync.example.test/",
            SyncRevisions = new() { [original.Id] = 7 },
            SyncedProjectHashes = new() { [original.Id] = ManualSyncService.Hash(original) }
        };
        var pulled = await service.SyncAsync([original], baselineSettings);
        Check(pulled.Downloaded == 1 && pulled.Projects[0].Name == remote.Name && putRequests == 0, "unchanged local project safely receives remote update");
        Check(original.Name == "迁移验收" && baselineSettings.SyncRevisions[original.Id] == 7, "sync result does not mutate caller objects or baseline");
        var changed = PmSerializer.Clone(original);
        changed.Name = "本地修改";
        var conflicted = await service.SyncAsync([changed], baselineSettings);
        Check(conflicted.Conflicts.Count == 1 && conflicted.Projects[0].Name == "本地修改" && putRequests == 0, "concurrent changes retain local data and surface conflict");
        var changedServer = baselineSettings with { SyncStateServerUrl = "https://different.example.test/" };
        var newServerResult = await service.SyncAsync([original], changedServer);
        Check(newServerResult.Conflicts.Single().Reason == "unknown_baseline" && putRequests == 0, "changing server cannot reuse another server's revision baseline");
        var rewindSettings = baselineSettings with { SyncRevisions = new() { [original.Id] = 9 } };
        var rewound = await service.SyncAsync([original], rewindSettings);
        Check(rewound.Conflicts.Single().Reason == "server_revision_rewound" && rewound.Projects[0].Name == original.Name,
            "a restored older server cannot silently roll back local data");
    }

    Console.WriteLine($"Infrastructure: {passed} checks passed.");
    if (args.Contains("--integration", StringComparer.Ordinal))
        await ServerIntegration.RunAsync();
}
finally
{
    // The generated directory is proven to be a direct child of the dedicated test root.
    var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PureProject.Infrastructure.Tests"));
    if (Path.GetDirectoryName(Path.GetFullPath(testRoot)) == parent && Directory.Exists(testRoot))
        Directory.Delete(testRoot, recursive: true);
}

void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
    passed++;
    Console.WriteLine("PASS " + message);
}
void Expect<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, message); return; }
    throw new Exception("FAILED: " + message);
}
async Task ExpectAsync<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, message); return; }
    throw new Exception("FAILED: " + message);
}
static HttpResponseMessage Json(object payload, HttpStatusCode code = HttpStatusCode.OK)
    => new(code) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };

static Project SampleProject() => new()
{
    Id = "project-test", Name = "迁移验收", CreatedAt = "2026-10-01T08:00:00.000Z", UpdatedAt = "2026-10-01T08:00:00.000Z",
    DefaultTaskGroupId = "group-test",
    TaskGroups = [new TaskGroup
    {
        Id = "group-test", Name = "开发", InitialStatusId = "todo-test", CompletionStatusId = "done-test",
        Statuses = [new TaskStatusDefinition { Id = "todo-test", Name = "待办", Category = "todo", SortOrder = 1024 },
            new TaskStatusDefinition { Id = "done-test", Name = "已完成", Category = "done", SortOrder = 2048 }]
    }],
    Tasks = [new ProjectTask
    {
        Id = "task-test", Title = "本机数据保护", TaskGroupId = "group-test", StatusId = "todo-test",
        CreatedAt = "2026-10-01T08:00:00.000Z", UpdatedAt = "2026-10-01T08:00:00.000Z"
    }]
};

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}
