using System.Collections;
using System.Text.Json;
using PureProject.Core;
using PureProject.Infrastructure;

internal static class SettingsBackupTests
{
    public static async Task<int> RunAsync(Project original, string testRoot)
    {
        var passed = 0;
        var settingsDirectory = Path.Combine(testRoot, "settings-backup");
        Directory.CreateDirectory(settingsDirectory);
        var repository = new SettingsRepository(settingsDirectory);
        var defaults = await repository.LoadAsync();
        Check(defaults.Theme == "System" && !defaults.AutoBackupEnabled && defaults.AutoBackupIntervalMinutes == 5 && defaults.Shortcuts.Count == 0,
            "new settings default to system theme, disabled backups and a five-minute interval");

        await File.WriteAllTextAsync(repository.FilePath, """
            {"Version":1,"Theme":"Dark","SyncServerUrl":"https://sync.example.test","SyncStateServerUrl":"https://sync.example.test","SyncRevisions":{"project-test":9}}
            """);
        var migrated = await repository.LoadAsync();
        Check(migrated.Theme == "Dark" && !migrated.AutoBackupEnabled && migrated.AutoBackupIntervalMinutes == 5 && migrated.Shortcuts.Count == 0
            && migrated.SyncRevisions["project-test"] == 9,
            "older settings load new defaults without losing existing theme or sync state");
        foreach (var interval in new[] { 1, 5, 10, 15, 30, 60, 120 })
        {
            await repository.SaveAsync(migrated with { AutoBackupEnabled = true, AutoBackupIntervalMinutes = interval });
            var loaded = await repository.LoadAsync();
            Check(loaded.AutoBackupEnabled && loaded.AutoBackupIntervalMinutes == interval && loaded.Theme == "Dark",
                $"automatic backup interval {interval} minutes persists");
        }
        await repository.SaveAsync(migrated with { Shortcuts = new() { ["New"] = "alt+shift+n" } });
        Check((await repository.LoadAsync()).Shortcuts["New"] == "Alt+Shift+N", "custom shortcuts persist as canonical key gestures");
        var validSettings = await File.ReadAllTextAsync(repository.FilePath);
        foreach (var interval in new[] { 0, -1, 2, 60 * 24 })
            await ExpectAsync<ArgumentException>(() => repository.SaveAsync(defaults with { AutoBackupIntervalMinutes = interval }),
                $"invalid automatic backup interval {interval} is rejected before saving");
        await ExpectAsync<ArgumentException>(() => repository.SaveAsync(defaults with { Theme = "custom" }), "unsupported themes are rejected before saving");
        await ExpectAsync<ArgumentException>(() => repository.SaveAsync(defaults with { Shortcuts = new() { ["unknown-action"] = "Ctrl+P" } }),
            "unknown shortcut actions are rejected before saving");
        Check(await File.ReadAllTextAsync(repository.FilePath) == validSettings, "invalid settings never replace the last valid settings file");
        foreach (var json in new[] { "{\"Version\":1,\"AutoBackupIntervalMinutes\":2}", "{\"Version\":1,\"Theme\":\"custom\"}", "{\"Version\":1,\"Shortcuts\":null}" })
        {
            await File.WriteAllTextAsync(repository.FilePath, json);
            await ExpectAsync<InvalidDataException>(() => repository.LoadAsync(), "invalid stored settings are reported without normalization");
            Check(await File.ReadAllTextAsync(repository.FilePath) == json, "failed settings loads preserve their source for recovery");
        }

        var backupRoot = Path.Combine(testRoot, "automatic-backups");
        var backup = new AutoBackupService(backupRoot);
        Check(!Directory.Exists(backupRoot), "constructing the backup service does not create files while backups are disabled");
        var empty = await backup.BackupAsync([]);
        Check(!empty.Skipped && empty.Files.Count == 0 && !Directory.Exists(backupRoot), "an empty project list does not create a backup directory");
        Check(AutoBackupService.DefaultBackupDirectory == Path.Combine(AppContext.BaseDirectory, "backup"),
            "the default automatic backup directory is next to the application");
        backup.EnsureBackupDirectoryWritable();
        Check(Directory.Exists(backupRoot) && !Directory.EnumerateFileSystemEntries(backupRoot).Any(),
            "backup writability is checked without leaving probe files");

        var first = PmSerializer.Clone(original);
        first.Name = "../../CON:<报告>|?*.";
        first.Id = "../../project-one";
        first.Extra["future-field"] = JsonSerializer.SerializeToElement(new { text = "未知字段也完整保留", nested = new[] { 1, 2, 3 } });
        var second = PmSerializer.Clone(first);
        second.Id = "../../project-two";
        var result = await backup.BackupAsync([first, second]);
        Check(!result.Skipped && result.Files.Count == 2 && result.Failures.Count == 0, "automatic backup writes one snapshot for each project");
        var folders = result.Files.Select(path => Path.GetDirectoryName(path)!).ToArray();
        Check(folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2
            && folders.All(path => Path.GetDirectoryName(path) == Path.GetFullPath(backupRoot)),
            "same-name projects and hostile names remain in distinct child directories");
        var restored = new ProjectExchangeService().ImportFile(result.Files[0]).Single();
        Check(PmSerializer.ExportPm(restored) == PmSerializer.ExportPm(first),
            "automatic snapshots restore all project content and unknown fields through the standard importer");
        var oldContents = await File.ReadAllBytesAsync(result.Files[0]);
        first.Name = "重命名后";
        first.Tasks[0].Title = "备份后修改";
        var later = await backup.BackupAsync([first]);
        Check(later.Files.Count == 1 && Path.GetDirectoryName(later.Files[0]) == folders[0],
            "renaming a project retains its existing backup directory");
        Check(later.Files[0] != result.Files[0] && Directory.GetFiles(folders[0], "*.pureproject").Length == 2
            && (await File.ReadAllBytesAsync(result.Files[0])).SequenceEqual(oldContents),
            "subsequent backups preserve previous snapshots byte for byte");
        Check(new ProjectExchangeService().ImportFile(later.Files[0]).Single().Tasks[0].Title == "备份后修改",
            "new backup snapshots contain the latest project version");

        var reservedName = PmSerializer.Clone(original);
        reservedName.Name = "CON.txt";
        var reservedResult = await backup.BackupAsync([reservedName]);
        Check(reservedResult.Files.Count == 1 && reservedResult.Failures.Count == 0, "Windows device names are safe in backup folder names");
        var invalid = PmSerializer.Clone(first);
        invalid.Tasks[0].TaskGroupId = "missing-group";
        var partial = await backup.BackupAsync([invalid, second]);
        Check(partial.Failures.Count == 1 && partial.Failures[0].ProjectId == first.Id && partial.Files.Count == 1,
            "one invalid project is reported while other projects still receive backups");
        Check((await File.ReadAllBytesAsync(result.Files[0])).SequenceEqual(oldContents), "a failed snapshot cannot damage an existing backup");

        var blockedRoot = Path.Combine(testRoot, "backup-path-is-a-file");
        await File.WriteAllTextAsync(blockedRoot, "retained");
        var blockedService = new AutoBackupService(blockedRoot);
        await ExpectAsync<IOException>(() => Task.Run(blockedService.EnsureBackupDirectoryWritable), "a non-writable backup location is reported before enabling backups");
        var blockedResult = await blockedService.BackupAsync([first]);
        Check(blockedResult.Failures.Count == 1 && blockedResult.Files.Count == 0 && await File.ReadAllTextAsync(blockedRoot) == "retained",
            "filesystem errors leave existing destination content untouched");

        var cancelledRoot = Path.Combine(testRoot, "cancelled-automatic-backups");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => new AutoBackupService(cancelledRoot).BackupAsync([first], cancellation.Token),
            "cancelled automatic backups stop before creating output");
        Check(!Directory.Exists(cancelledRoot), "pre-cancelled backups leave no directory or files");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blockedSnapshots = new BlockingSnapshots(second, entered, release);
        var running = Task.Run(() => backup.BackupAsync(blockedSnapshots));
        try
        {
            Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))), "a controlled backup round has started");
            var overlap = await backup.BackupAsync([first]);
            Check(overlap.Skipped && overlap.Files.Count == 0, "overlapping automatic backup rounds are skipped without queuing duplicate writes");
        }
        finally { release.Set(); }
        Check((await running).Files.Count == 1, "the accepted automatic backup round completes normally");
        Check(!Directory.EnumerateFiles(backupRoot, "*.tmp", SearchOption.AllDirectories).Any(), "automatic backups leave no staging files after completion or failure");
        return passed;

        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAILED: " + message);
            passed++;
            Console.WriteLine("PASS " + message);
        }

        async Task ExpectAsync<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { Check(true, message); return; }
            throw new Exception("FAILED: " + message);
        }
    }

    private sealed class BlockingSnapshots(Project project, ManualResetEventSlim entered, ManualResetEventSlim release) : IReadOnlyList<Project>
    {
        public int Count => 1;
        public Project this[int index] => index == 0 ? project : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<Project> GetEnumerator()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test snapshot source was not released.");
            yield return project;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
