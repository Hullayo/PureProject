using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PureProject.Core;
using PureProject.Infrastructure;

internal static class PersistenceCheck
{
    // All mutations, cancellation, and deliberate corruption stay in a new copy.
    // Reopening the repository releases/reacquires the session lock; it is not
    // presented as a full GUI process restart or a power-loss simulation.
    public static async Task RunAsync(string source, string artifactRoot, IReadOnlyList<Project> projects)
    {
        var before = await SourceHashesAsync(source);
        var directory = Path.Combine(artifactRoot, "persistence-check", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var data = Path.Combine(directory, "data");
        var steps = new List<object>();
        var total = Stopwatch.StartNew();
        string? failure = null;
        var expectedTasks = projects.Sum(p => p.Tasks.Count);
        var projectId = projects[^1].Id;
        var taskId = projects[^1].Tasks[^1].Id;
        var originalTitle = projects[^1].Tasks[^1].Title;
        var originalDescription = projects[^1].Tasks[^1].Description;
        var title = originalTitle + "｜持久化验证";
        var description = originalDescription + "\n独立副本的第二次保存。";
        string? backupHash = null;

        async Task Step(string name, Func<Task> action)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                await action();
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                steps.Add(new { name, success = true, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                    privateBytes = process.PrivateMemorySize64, managedBytes = GC.GetTotalMemory(false) });
                Console.WriteLine($"Persistence {name}: {timer.Elapsed.TotalMilliseconds:F0} ms");
            }
            catch (Exception error)
            {
                steps.Add(new { name, success = false, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, error = error.ToString() });
                throw;
            }
        }

        ProjectTask Target(IReadOnlyList<Project> loaded)
        {
            if (loaded.Count != projects.Count || loaded.Sum(p => p.Tasks.Count) != expectedTasks)
                throw new InvalidDataException("The persisted library count changed.");
            return loaded.Single(p => p.Id == projectId).Tasks.Single(t => t.Id == taskId);
        }

        try
        {
            await Step("initial-copy-save", async () =>
            {
                using var repository = new JsonProjectRepository(data);
                _ = await repository.LoadAsync();
                await repository.SaveAsync(projects);
            });
            await Step("reopen-edit-last-task-save", async () =>
            {
                using var repository = new JsonProjectRepository(data);
                var loaded = await repository.LoadAsync();
                var task = Target(loaded);
                if (task.Title != originalTitle) throw new InvalidDataException("Initial copy lost the final task.");
                task.Title = title;
                await repository.SaveAsync(loaded);
            });
            await Step("reopen-check-edit-save-next-revision", async () =>
            {
                using var repository = new JsonProjectRepository(data);
                var loaded = await repository.LoadAsync();
                var task = Target(loaded);
                if (task.Title != title) throw new InvalidDataException("The title edit did not survive repository reopening.");
                task.Description = description;
                await repository.SaveAsync(loaded);
                backupHash = await HashAsync(repository.BackupFilePath);
            });
            await Step("reopen-and-cancel-save-preserves-primary", async () =>
            {
                using var repository = new JsonProjectRepository(data);
                var loaded = await repository.LoadAsync();
                if (Target(loaded).Description != description) throw new InvalidDataException("The second revision did not survive reopening.");
                var hash = await HashAsync(repository.DataFilePath);
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                try { await repository.SaveAsync(loaded, cancel.Token); throw new InvalidDataException("A cancelled save unexpectedly succeeded."); }
                catch (OperationCanceledException) { }
                if (await HashAsync(repository.DataFilePath) != hash) throw new InvalidDataException("Cancelled save changed the primary file.");
            });
            await Step("corrupt-copy-reject-load-and-guard-save", async () =>
            {
                await File.WriteAllTextAsync(Path.Combine(data, "projects.json"), "{ deliberate invalid isolated stress copy");
                using var repository = new JsonProjectRepository(data);
                try { _ = await repository.LoadAsync(); throw new InvalidDataException("Corrupt copy unexpectedly loaded."); }
                catch (RepositoryDataException) { }
                try { await repository.SaveAsync(projects); throw new InvalidDataException("Save after failed load unexpectedly succeeded."); }
                catch (InvalidOperationException) { }
            });
            await Step("explicit-backup-recovery-and-retained-corrupt-copy", async () =>
            {
                using var repository = new JsonProjectRepository(data);
                var restored = await repository.RestoreBackupAsync();
                var task = Target(restored);
                if (task.Title != title || task.Description != originalDescription)
                    throw new InvalidDataException("Recovery did not return exactly the preceding saved revision.");
                if (await HashAsync(repository.DataFilePath) != backupHash || await HashAsync(repository.BackupFilePath) != backupHash)
                    throw new InvalidDataException("Recovery changed the known-good backup or restored unexpected bytes.");
                var retained = Directory.GetFiles(data, "projects.before-restore.*.json");
                if (retained.Length != 1 || await File.ReadAllTextAsync(retained[0]) != "{ deliberate invalid isolated stress copy")
                    throw new InvalidDataException("The corrupt input was not retained for recovery inspection.");
            });
            await Step("final-reopen-recovery-check", async () =>
            {
                using var repository = new JsonProjectRepository(data);
                var task = Target(await repository.LoadAsync());
                if (task.Title != title || task.Description != originalDescription)
                    throw new InvalidDataException("The recovery did not survive repository reopening.");
            });
        }
        catch (Exception error) { failure = error.ToString(); }
        var after = await SourceHashesAsync(source);
        var sourceUnchanged = before.OrderBy(p => p.Key).SequenceEqual(after.OrderBy(p => p.Key));
        if (!sourceUnchanged) failure = (failure ?? "") + "\nSource fixture files changed during the isolated persistence check.";
        var reportPath = Path.Combine(directory, "persistence-result.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            success = failure is null, sourceDirectory = source, mutationDirectory = data, projects = projects.Count, tasks = expectedTasks,
            elapsedMilliseconds = total.Elapsed.TotalMilliseconds, processId = Environment.ProcessId,
            verification = "Repository disposal/reopen, native storage save, cancelled save, explicit backup recovery. This does not simulate an OS crash or GUI process restart.",
            sourceUnchanged, before, after, steps, exception = failure
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Persistence report: " + reportPath);
        if (failure is not null) throw new InvalidDataException(failure);
    }

    private static async Task<Dictionary<string, string>> SourceHashesAsync(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "projects.json", "settings.json", "scale-fixture.json" })
            result[name] = await HashAsync(Path.Combine(directory, name));
        return result;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
