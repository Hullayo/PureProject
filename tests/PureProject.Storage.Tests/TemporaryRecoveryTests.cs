using System.Security.Cryptography;
using PureProject.Core;
using PureProject.Infrastructure;

internal static class TemporaryRecoveryTests
{
    public static void Register(List<(string Name, Func<Task> Run)> tests, string scratch)
    {
        string DirectoryName() => Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        tests.Add(("Orphan recovery keeps committed data and quarantines unique or unsupported candidates", async () =>
        {
            var directory = DirectoryName(); var baseline = await Seed(directory);
            var main = Path.Combine(directory, "projects.json"); var backup = main + ".bak";
            var before = (Hash(main), Hash(backup));
            var unique = PmSerializer.Clone(baseline); unique.Name = "uncommitted";
            var valid = await Candidate(directory, PmSerializer.ExportInternal([unique]));
            var unsupported = await Candidate(directory, "{\"schema_version\":99,\"projects\":[]}");
            var partial = await Candidate(directory, "{\"schema_version\":4,\"projects\":[");
            var empty = await Candidate(directory, "");
            var redundant = await Candidate(directory, await File.ReadAllTextAsync(main));
            using var repository = new JsonProjectRepository(directory);
            Check((await repository.LoadAsync()).Single().Name == baseline.Name, "Uncommitted value replaced primary");
            Check((Hash(main), Hash(backup)) == before, "Orphan scan altered primary or backup");
            Check(File.Exists(Path.ChangeExtension(valid, ".recovery")), "Valid unique candidate not isolated");
            Check(File.Exists(Path.ChangeExtension(unsupported, ".recovery")), "Future format candidate deleted");
            Check(File.Exists(Path.ChangeExtension(partial, ".recovery")), "Nonempty partial evidence deleted");
            Check(!File.Exists(empty) && !File.Exists(redundant), "Safe empty/redundant candidates were not cleaned");
            Check(repository.TemporaryRecovery.Files.Count(f => f.Action == "QuarantinedValid") == 1, "Valid candidate not identified");
            Check(repository.TemporaryRecovery.HasUncommittedFiles, "Recovery notice suppressed");
        }));
        tests.Add(("An exclusive repository lock prevents recovery from touching another owner's candidate", async () =>
        {
            var directory = DirectoryName(); await Seed(directory);
            using var owner = new JsonProjectRepository(directory);
            var path = await Candidate(directory, "owner-staged-data");
            var before = Hash(path);
            try { using var other = new JsonProjectRepository(directory); throw new Exception("Second owner acquired lock"); }
            catch (RepositoryInUseException) { }
            Check(File.Exists(path) && Hash(path) == before, "Failed second constructor altered orphan");
        }));
        tests.Add(("Fresh, read-only, locked and foreign temporary paths remain untouched", async () =>
        {
            var directory = DirectoryName(); await Seed(directory);
            var fresh = await Candidate(directory, "fresh", old: false);
            var readOnly = await Candidate(directory, "read-only"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            var locked = await Candidate(directory, "locked");
            var unknown = Path.Combine(directory, ".projects.json.not-a-guid.tmp"); await File.WriteAllTextAsync(unknown, "foreign");
            var foreign = Path.Combine(directory, $".settings.json.{Guid.NewGuid():N}.tmp"); await File.WriteAllTextAsync(foreign, "settings");
            var subdirectory = Path.Combine(directory, $".projects.json.{Guid.NewGuid():N}.tmp"); Directory.CreateDirectory(subdirectory);
            try
            {
                await using var writer = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using var repository = new JsonProjectRepository(directory); await repository.LoadAsync();
                Check(new[] { fresh, readOnly, locked, unknown, foreign }.All(File.Exists) && Directory.Exists(subdirectory), "Unsafe or foreign candidate changed");
                Check(repository.TemporaryRecovery.Files.Any(f => f.Action == "RetainedUnavailable"), "Open writer not preserved/reported");
                Check(repository.TemporaryRecovery.Files.Any(f => f.Action == "RetainedRecent"), "Fresh candidate not preserved");
                Check(!repository.TemporaryRecovery.Files.Any(f => f.FilePath == unknown || f.FilePath == foreign), "Foreign names entered candidate set");
            }
            finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
        }));
        tests.Add(("A missing primary with an uncommitted candidate cannot silently become an empty database", async () =>
        {
            var directory = DirectoryName(); Directory.CreateDirectory(directory);
            var project = new ProjectService().CreateProject("uncommitted only");
            var candidate = await Candidate(directory, PmSerializer.ExportInternal([project]));
            using var repository = new JsonProjectRepository(directory);
            await Reject<RepositoryDataException>(async () => _ = await repository.LoadAsync());
            await Reject<InvalidOperationException>(() => repository.SaveAsync([]));
            Check(!File.Exists(repository.DataFilePath) && File.Exists(Path.ChangeExtension(candidate, ".recovery")), "Missing primary was silently populated or evidence lost");
        }));
        tests.Add(("Recovery count and validation byte budgets preserve over-budget data without parsing it", async () =>
        {
            var directory = DirectoryName(); var baseline = await Seed(directory);
            var paths = new List<string>();
            for (var i = 0; i < ProjectTemporaryRecoveryReport.MaximumRetainedCandidates + 1; i++)
            { var copy = PmSerializer.Clone(baseline); copy.Name = "candidate-" + i; paths.Add(await Candidate(directory, PmSerializer.ExportInternal([copy]))); }
            using (var repository = new JsonProjectRepository(directory))
            {
                await repository.LoadAsync();
                Check(repository.TemporaryRecovery.Files.Count(f => f.Action == "QuarantinedValid") == 8, "Retention count not bounded");
                Check(repository.TemporaryRecovery.Files.Count(f => f.Action == "RetainedOverBudget") == 1, "Over-budget unique data not reported");
                Check(paths.All(p => File.Exists(p) || File.Exists(Path.ChangeExtension(p, ".recovery"))), "Retention quota discarded unique data");
            }
            var largeDirectory = DirectoryName(); await Seed(largeDirectory);
            var large = await Candidate(largeDirectory, "");
            await using (var file = File.OpenWrite(large)) file.SetLength(ProjectTemporaryRecoveryReport.MaximumSingleValidationBytes + 1);
            File.SetLastWriteTimeUtc(large, DateTime.UtcNow.AddMinutes(-5));
            using var second = new JsonProjectRepository(largeDirectory); await second.LoadAsync();
            Check(second.TemporaryRecovery.ValidatedBytes == 0 && File.Exists(Path.ChangeExtension(large, ".recovery")), "Oversize data was parsed or lost");
        }));
        tests.Add(("Bounded orphan enumeration leaves remaining candidates intact for the next process", async () =>
        {
            var directory = DirectoryName(); await Seed(directory);
            for (var i = 0; i < 70; i++) await Candidate(directory, "");
            using (var first = new JsonProjectRepository(directory))
            {
                await first.LoadAsync();
                Check(first.TemporaryRecovery.ScanLimitReached && first.TemporaryRecovery.Files.Count == 64, "Candidate scan not bounded");
                Check(Directory.GetFiles(directory, ".projects.json.*.tmp").Length == 6, "Unscanned candidates changed");
            }
            using var next = new JsonProjectRepository(directory); await next.LoadAsync();
            Check(!Directory.GetFiles(directory, ".projects.json.*.tmp").Any(), "Next owner did not finish safe empty cleanup");
        }));
        tests.Add(("Recovery never follows a candidate symbolic link outside its directory", async () =>
        {
            var directory = DirectoryName(); await Seed(directory);
            var outside = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(outside, "external data must survive");
            var link = Path.Combine(directory, $".projects.json.{Guid.NewGuid():N}.tmp");
            try { File.CreateSymbolicLink(link, outside); }
            catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            { throw new StorageTestSkippedException("candidate symlink setup: " + error.Message); }
            var hash = Hash(outside);
            using var repository = new JsonProjectRepository(directory); await repository.LoadAsync();
            Check(Hash(outside) == hash && new FileInfo(link).LinkTarget is not null, "Reparse target changed");
            Check(repository.TemporaryRecovery.Files.Any(f => f.Action == "SkippedUnsafePath"), "Reparse point not excluded");
        }));
    }

    private static async Task<Project> Seed(string directory)
    {
        using var repository = new JsonProjectRepository(directory); await repository.LoadAsync();
        var project = new ProjectService().CreateProject("committed");
        await repository.SaveAsync([project]); await repository.SaveAsync([project]); return project;
    }
    private static async Task<string> Candidate(string directory, string text, bool old = true)
    {
        var path = Path.Combine(directory, $".projects.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(path, text);
        if (old) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }
    private static string Hash(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}

internal sealed class StorageTestSkippedException(string message) : Exception(message);
