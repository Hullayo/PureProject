using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PureProject.Core;
using PureProject.Infrastructure;

var tests = new List<(string Name, Func<Task> Run)>();
var metrics = new Dictionary<string, object>();
var scratch = Path.Combine(Path.GetTempPath(), "PureProject-storage-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var service = new ProjectService();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Test(string name, Func<Task> run) => tests.Add((name, run));
async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
string NewDirectory() => Path.Combine(scratch, Guid.NewGuid().ToString("N"));
Project Fresh() => service.CreateProject("持久化测试", "Preserve 中文, emoji 🧪 and \"quotes\".");
async Task<IReadOnlyList<Project>> ReadText(string text)
{
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
    return await PmSerializer.ReadInternalAsync(stream);
}
string Fingerprint(Project project) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(project)));
async Task<string> FileHash(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream));
}

Test("Native compact storage preserves every known field and extension namespace", async () =>
{
    var project = BuildProjects(1, 1, 4, 2).Single();
    using var stream = new MemoryStream();
    await PmSerializer.WriteInternalAsync(stream, new[] { project });
    stream.Position = 0;
    var loaded = (await PmSerializer.ReadInternalAsync(stream)).Single();
    Check(Fingerprint(loaded) == Fingerprint(project), "Native payload changed");
    stream.Position = 0;
    await PmSerializer.ValidateInternalAsync(stream);
});
Test("Canonical exchange parser preserves storage extensions while native storage strips device-only fields", async () =>
{
    var project = Fresh();
    project.Extra["storage"] = JsonSerializer.SerializeToElement(new { path = "C:/device-only" });
    project.Extra["plugin"] = JsonSerializer.SerializeToElement(new { values = new[] { 1, 2, 3 } });
    using var document = JsonDocument.Parse(JsonSerializer.Serialize(project));
    Check(PmSerializer.ParseCanonicalProject(document.RootElement).Extra.ContainsKey("storage"), "Exchange parser changed payload");
    using var stream = new MemoryStream();
    await PmSerializer.WriteInternalAsync(stream, new[] { project });
    stream.Position = 0;
    var saved = (await PmSerializer.ReadInternalAsync(stream)).Single();
    Check(!saved.Extra.ContainsKey("storage") && saved.Extra.ContainsKey("plugin"), "Device/unknown field handling changed");
    Check(project.Extra.ContainsKey("storage"), "Save mutated the live model");
});
Test("Schema 1–3 arrays, PM files and both legacy backup kinds retain migration behavior", async () =>
{
    const string legacy = """{"version":"1.0","project":{"name":"Legacy","created_at":"2020-01-01"},"tasks":[{"id":"t1","title":"历史任务","status":"done","updated_at":"2020-01-02"}]}""";
    for (var schema = 1; schema <= 3; schema++)
    {
        var node = JsonNode.Parse(legacy)!; node["schema_version"] = schema;
        var text = node.ToJsonString();
        Check(Fingerprint((await ReadText(text)).Single()) == Fingerprint(PmSerializer.ParsePm(text)), "Legacy PM migration changed");
        foreach (var kind in new[] { "pureproject-backup", "projectmanager-backup" })
        {
            var backup = new JsonObject { ["schema_version"] = schema, ["kind"] = kind, ["projects"] = new JsonArray(node.DeepClone()) }.ToJsonString();
            Check(Fingerprint((await ReadText(backup)).Single()) == Fingerprint(PmSerializer.ParseProjects(backup).Single()), "Legacy backup migration changed");
        }
    }
    var current = PmSerializer.ExportInternal(new[] { Fresh() });
    var array = JsonNode.Parse(current)!["projects"]!.ToJsonString();
    Check((await ReadText(array)).Count == 1, "Internal array no longer imports");
    using var bomStream = new MemoryStream(new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(current)).ToArray());
    Check((await PmSerializer.ReadInternalAsync(bomStream)).Count == 1, "UTF-8 BOM no longer accepted");
});
Test("Future envelope and item versions, invalid required fields and null projects reject", async () =>
{
    var project = Fresh(); service.CreateTask(project, "task");
    var text = PmSerializer.ExportInternal(new[] { project });
    foreach (var mutate in new Action<JsonNode>[]
    {
        n => n["schema_version"] = 99,
        n => n["projects"]![0]!["schema_version"] = 99,
        n => n["projects"]![0]!["task_groups"]![0]!["statuses"]![0]!.AsObject().Remove("category"),
        n => n["projects"]![0]!["tasks"]![0]!.AsObject().Remove("status_id"),
        n => n["projects"]![0]!["tasks"]![0]!["dependencies"] = new JsonObject(),
        n => n["projects"]![0] = null,
        n => n["projects"]!.AsArray().Add(n["projects"]![0]!.DeepClone()),
        n => n["projects"]![0]!["plugin"] = new string('x', 200001)
    })
    {
        var node = JsonNode.Parse(text)!; mutate(node);
        await Reject<PmValidationException>(async () => _ = await ReadText(node.ToJsonString()));
    }
    await Reject<PmValidationException>(async () => _ = await ReadText(text + " trailing"));
});
Test("Failed structural validation cannot replace either primary or previous backup", async () =>
{
    var directory = NewDirectory();
    using var repository = new JsonProjectRepository(directory);
    await repository.LoadAsync();
    var project = Fresh(); await repository.SaveAsync(new[] { project });
    project.Name = "Second revision"; await repository.SaveAsync(new[] { project });
    var primary = await FileHash(repository.DataFilePath); var backup = await FileHash(repository.BackupFilePath);
    project.Extra["too_long"] = JsonSerializer.SerializeToElement(new string('x', 200001));
    await Reject<PmValidationException>(() => repository.SaveAsync(new[] { project }));
    Check(await FileHash(repository.DataFilePath) == primary && await FileHash(repository.BackupFilePath) == backup, "Invalid save changed a durable file");
    Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Failed validation left a temp file");
});
Test("Duplicate JSON keys reject rather than silently discarding payload values", async () =>
{
    var project = Fresh();
    var flat = JsonSerializer.Serialize(project);
    var duplicates = new[]
    {
        flat.Insert(1, "\"name\":\"Overwritten\","),
        flat.Insert(1, "\"plugin_duplicate\":{\"a\":1,\"a\":2},"),
        flat.Insert(1, "\"plugin_duplicate\":[{\"a\":1,\"a\":2}],")
    };
    foreach (var json in duplicates)
    {
        using var document = JsonDocument.Parse(json);
        await Reject<PmValidationException>(() => { PmSerializer.ParseCanonicalProject(document.RootElement); return Task.CompletedTask; });
        await Reject<PmValidationException>(async () => _ = await ReadText("{\"schema_version\":4,\"projects\":[" + json + "]}"));
    }
    await Reject<PmValidationException>(async () => _ = await ReadText("{\"schema_version\":4,\"schema_version\":4,\"projects\":[]}"));
});
Test("Locked write and cancellation leave primary and backup unchanged", async () =>
{
    var directory = NewDirectory();
    using var repository = new JsonProjectRepository(directory);
    await repository.LoadAsync();
    var project = Fresh(); await repository.SaveAsync(new[] { project });
    project.Name = "Second revision"; await repository.SaveAsync(new[] { project });
    var primary = await FileHash(repository.DataFilePath); var backup = await FileHash(repository.BackupFilePath);
    project.Name = "Unsaved third revision";
    await using (var locked = new FileStream(repository.DataFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await Reject<IOException>(() => repository.SaveAsync(new[] { project }));
    using var cancellation = new CancellationTokenSource();
    IEnumerable<Project> CancelWhileEnumerating()
    {
        yield return project;
        cancellation.Cancel();
        yield return Fresh();
    }
    await Reject<OperationCanceledException>(() => repository.SaveAsync(CancelWhileEnumerating(), cancellation.Token));
    Check(await FileHash(repository.DataFilePath) == primary && await FileHash(repository.BackupFilePath) == backup, "Failed save changed durable files");
    Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Failed save left a temp file");
});
Test("Corrupt load blocks save; explicit recovery retains corrupt source and the good backup", async () =>
{
    var directory = NewDirectory(); var original = Fresh();
    using (var first = new JsonProjectRepository(directory))
    {
        await first.LoadAsync(); await first.SaveAsync(new[] { original });
        var second = PmSerializer.Clone(original); second.Name = "Second revision";
        await first.SaveAsync(new[] { second });
    }
    var file = Path.Combine(directory, "projects.json"); await File.WriteAllTextAsync(file, "{corrupted");
    using (var repository = new JsonProjectRepository(directory))
    {
        await Reject<RepositoryDataException>(async () => _ = await repository.LoadAsync());
        await Reject<InvalidOperationException>(() => repository.SaveAsync(Array.Empty<Project>()));
        Check(await File.ReadAllTextAsync(file) == "{corrupted", "Bad load overwrote original");
        var backup = await FileHash(repository.BackupFilePath);
        var recovered = await repository.RestoreBackupAsync();
        Check(Fingerprint(recovered.Single()) == Fingerprint(original), "Recovery content changed");
        Check(await FileHash(repository.BackupFilePath) == backup, "Recovery changed good backup");
        Check(await File.ReadAllTextAsync(Directory.GetFiles(directory, "projects.before-restore.*.json").Single()) == "{corrupted", "Corrupt source was not retained");
    }
    using var reopened = new JsonProjectRepository(directory);
    Check(Fingerprint((await reopened.LoadAsync()).Single()) == Fingerprint(original), "Recovered data not durable after close");
});
Test("Missing primary with existing backup requires explicit recovery", async () =>
{
    var directory = NewDirectory();
    using var repository = new JsonProjectRepository(directory);
    await repository.LoadAsync(); var project = Fresh();
    await repository.SaveAsync(new[] { project }); await repository.SaveAsync(new[] { project });
    File.Delete(repository.DataFilePath);
    await Reject<RepositoryDataException>(async () => _ = await repository.LoadAsync());
    await Reject<InvalidOperationException>(() => repository.SaveAsync(Array.Empty<Project>()));
    Check((await repository.RestoreBackupAsync()).Single().Id == project.Id, "Explicit recovery failed");
});
Test("Invalid backup never replaces a valid primary", async () =>
{
    var directory = NewDirectory();
    using var repository = new JsonProjectRepository(directory);
    await repository.LoadAsync(); await repository.SaveAsync(new[] { Fresh() });
    var hash = await FileHash(repository.DataFilePath);
    await File.WriteAllTextAsync(repository.BackupFilePath, "{\"schema_version\":99,\"projects\":[]}");
    await Reject<PmValidationException>(async () => _ = await repository.RestoreBackupAsync());
    Check(await FileHash(repository.DataFilePath) == hash, "Bad backup overwrote valid primary");
});
if (!args.Contains("--skip-scale", StringComparer.Ordinal)) Test("100,000 tasks save, close and reload with exact per-project fingerprints", async () =>
{
    var projects = BuildProjects(10, 10, 10, 100);
    Check(projects.Count == 10 && projects.Sum(p => p.TaskGroups.Count) == 100 && projects.Sum(p => p.TaskGroups.Sum(g => g.Statuses.Count)) == 1000 && projects.Sum(p => p.Tasks.Count) == 100000, "Wrong pressure fixture shape");
    var expected = projects.Select(Fingerprint).ToArray();
    var directory = NewDirectory();
    var allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
    using (var repository = new JsonProjectRepository(directory))
    {
        await repository.LoadAsync(); await repository.SaveAsync(projects);
    }
    clock.Stop();
    metrics["save_elapsed_ms"] = clock.Elapsed.TotalMilliseconds;
    metrics["save_allocated_bytes"] = GC.GetTotalAllocatedBytes(true) - allocated;
    metrics["file_bytes"] = new FileInfo(Path.Combine(directory, "projects.json")).Length;
    projects = null!;
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    allocated = GC.GetTotalAllocatedBytes(true); clock.Restart();
    List<Project> loaded;
    using (var repository = new JsonProjectRepository(directory)) loaded = await repository.LoadAsync();
    clock.Stop();
    metrics["reload_elapsed_ms"] = clock.Elapsed.TotalMilliseconds;
    metrics["reload_allocated_bytes"] = GC.GetTotalAllocatedBytes(true) - allocated;
    Check(loaded.Sum(p => p.Tasks.Count) == 100000, "Task count changed after close/reopen");
    Check(loaded.Select(Fingerprint).SequenceEqual(expected), "Known fields, order, or unknown extensions changed");
    metrics["shape"] = new { projects = 10, groups = 100, statuses = 1000, tasks = 100000 };
    metrics["exact_project_fingerprints_match"] = true;
    metrics["process_peak_working_set_bytes"] = Process.GetCurrentProcess().PeakWorkingSet64;
});

TemporaryRecoveryTests.Register(tests, scratch);
var failures = 0;
var skipped = 0;
try
{
    foreach (var (name, run) in tests)
    {
        try { await run(); Console.WriteLine("PASS " + name); }
        catch (StorageTestSkippedException reason) { skipped++; Console.WriteLine($"SKIP {name}: {reason.Message}"); }
        catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}\n{error}"); }
    }
    Console.WriteLine($"Storage: {tests.Count - failures - skipped}/{tests.Count} tests passed; {skipped} skipped.");
    metrics["tests_passed"] = tests.Count - failures - skipped; metrics["tests_total"] = tests.Count; metrics["tests_skipped"] = skipped;
    Console.WriteLine(JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    var outputArgument = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
    if (outputArgument is not null)
    {
        var output = Path.GetFullPath(outputArgument); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    }
}
finally { Directory.Delete(scratch, recursive: true); }
return failures == 0 ? 0 : 1;

static List<Project> BuildProjects(int projectCount, int groupCount, int statusCount, int tasksPerStatus)
{
    const string timestamp = "2026-10-06T08:00:00.000Z";
    var projects = new List<Project>();
    for (var p = 0; p < projectCount; p++)
    {
        var id = $"storage-p{p}";
        var project = new Project
        {
            Id = id, Name = $"项目 {p} 🧪", Description = "中文与 unknown extensions", CreatedAt = timestamp, UpdatedAt = timestamp,
            Archived = p == 3, StartDate = "2026-10-01", EndDate = "2027-01-31", DefaultTaskGroupId = id + "-g0", Readme = "# Edited README\n中文内容", SyncEnabled = p % 2 == 0,
            SortOrder = p * 1024, LegacyReadmeFile = "README.md", LegacyTemplate = new() { Files = ["README.md"], Dirs = ["docs"], FileContents = new() { ["README.md"] = "Original README" } },
            FileTree = JsonSerializer.SerializeToElement(new { files = new[] { "中文.md", "README.md" } }), Flowchart = JsonSerializer.SerializeToElement(new { nodes = new[] { new { id = "node", x = 4.5, y = 8 } } }),
            Tags = [new() { Id = id + "-tag", Name = "回归测试" }], Changelog = [new() { Version = "1.0", Date = "2026-10-06", Info = "保存与重开", Auto = false }],
            Milestones = [new() { Id = id + "-milestone", Title = "验收", Date = "2027-01-31", Description = "完整重载" }]
        };
        project.Extra["plugin_project"] = JsonSerializer.SerializeToElement(new { n = p, nested = new[] { "α", "β" } });
        project.PmExtensions["plugin_envelope"] = JsonSerializer.SerializeToElement(new { keep = true });
        project.LegacyTemplate.Extra["plugin_template"] = JsonSerializer.SerializeToElement(42);
        for (var g = 0; g < groupCount; g++)
        {
            var groupId = id + $"-g{g}";
            var group = new TaskGroup { Id = groupId, Name = $"工作组 {g}", InitialStatusId = groupId + "-s0", CompletionStatusId = groupId + $"-s{statusCount - 1}", SortOrder = g * 1024, Archived = g == 8 };
            group.Extra["plugin_group"] = JsonSerializer.SerializeToElement(new { keep = g });
            for (var s = 0; s < statusCount; s++)
            {
                var statusId = groupId + $"-s{s}";
                var category = s == statusCount - 1 ? "done" : s % 3 == 0 ? "todo" : s % 3 == 1 ? "active" : "cancelled";
                var status = new TaskStatusDefinition { Id = statusId, Name = $"状态 {s}", Category = category, Color = PmSerializer.CategoryColor(category), SortOrder = s * 1024 };
                status.Extra["plugin_status"] = JsonSerializer.SerializeToElement(s);
                group.Statuses.Add(status);
                for (var t = 0; t < tasksPerStatus; t++)
                {
                    var task = new ProjectTask
                    {
                        Id = statusId + $"-t{t}", Title = $"压力任务 {p}/{g}/{s}/{t}", Description = "保存、重开、顺序、扩展数据与中文检查", TaskGroupId = groupId, StatusId = statusId,
                        CompletedAt = category == "done" ? timestamp : null, Priority = t % 2 == 0 ? "high" : "low", CreatedAt = timestamp, UpdatedAt = timestamp,
                        DueDate = "2026-12-31", DueTime = "18:30", StartOffset = 2.5, Tags = [id + "-tag"],
                        Subtasks = [new() { Id = statusId + $"-t{t}-sub", Title = "检查步骤", Done = t % 2 == 0 }],
                        Comments = [new() { Id = statusId + $"-t{t}-comment", Content = "原始评论 \"精确保留\"", CreatedAt = timestamp }],
                        Recurrence = t == 0 ? new() { Freq = "weekly", Interval = 2, ByWeekday = [1, 3], End = "count", Count = 5 } : null
                    };
                    if (project.Tasks.Count > 0) task.Dependencies.Add(new() { TaskId = project.Tasks[^1].Id, DayOffset = 0.5 });
                    task.Extra["plugin_task"] = JsonSerializer.SerializeToElement(new { position = t, enabled = true });
                    project.Tasks.Add(task);
                }
            }
            project.TaskGroups.Add(group);
        }
        projects.Add(project);
    }
    return projects;
}
