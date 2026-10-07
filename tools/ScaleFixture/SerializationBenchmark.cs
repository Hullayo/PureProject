using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PureProject.Core;

internal static class SerializationBenchmark
{
    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task RunAsync(string fixtureDirectory, string reportPath, int iterations)
    {
        var paths = new[] { "projects.json", "settings.json", "scale-fixture.json" }
            .Select(name => Path.Combine(fixtureDirectory, name)).ToArray();
        var initialHashes = paths.ToDictionary(path => Path.GetFileName(path)!, HashFile);
        var source = await File.ReadAllTextAsync(paths[0]);
        var projects = PmSerializer.ParseProjects(source);
        if (projects.Count != 10 || projects.Sum(p => p.Tasks.Count) != 20000
            || projects.Any(p => !p.Id.StartsWith("scale-", StringComparison.Ordinal)))
            throw new InvalidDataException("Benchmark requires the marked 10-project / 20,000-task fixture.");

        var samples = new List<Sample>();
        using var process = Process.GetCurrentProcess();
        long undoCharacters = 0;
        long encodedBytes = 0;

        Sample Measure(int iteration, string stage, Action action)
        {
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var cpu = process.TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            return new Sample(iteration, stage, stopwatch.Elapsed.TotalMilliseconds,
                (process.TotalProcessorTime - cpu).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocated,
                GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
        }

        void CollectBetweenRounds()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        void CommitRound(int iteration, bool record)
        {
            string before = "";
            List<Project> next = [];
            string serialized = "";
            IReadOnlyList<Project> reparsed = [];
            byte[] encoded = [];
            var replacement = PmSerializer.CloneTask(projects[0].Tasks[0]);
            replacement.Title += "｜只读基准内存草稿";
            var service = new ProjectService();
            var round = new List<Sample>(6);
            round.Add(Measure(iteration, "commit.undo.ExportBackup.all20000", () => before = PmSerializer.ExportBackup(projects)));
            round.Add(Measure(iteration, "commit.snapshot.Clone.all20000", () => next = projects.Select(PmSerializer.Clone).ToList()));
            round.Add(Measure(iteration, "commit.mutation.SaveTask.oneProject2000", () => service.SaveTask(next[0], replacement, false)));
            round.Add(Measure(iteration, "commit.repository.ExportInternal.all20000", () => serialized = PmSerializer.ExportInternal(next)));
            round.Add(Measure(iteration, "commit.repository.ParseProjects.all20000", () => reparsed = PmSerializer.ParseProjects(serialized)));
            round.Add(Measure(iteration, "commit.atomicWrite.EncodeUtf8.all20000", () => encoded = Encoding.UTF8.GetBytes(serialized)));
            if (record)
            {
                samples.AddRange(round);
                samples.Add(new Sample(iteration, "commit.total.dataCpuBeforeIo", round.Sum(s => s.ElapsedMs),
                    round.Sum(s => s.ProcessCpuMs), round.Sum(s => s.AllocatedBytes),
                    round.Sum(s => s.Gen0), round.Sum(s => s.Gen1), round.Sum(s => s.Gen2)));
            }
            undoCharacters = before.Length;
            encodedBytes = encoded.Length;
            if (reparsed.Count != 10 || reparsed[0].Tasks[0].Title != replacement.Title)
                throw new InvalidDataException("In-memory commit benchmark lost its draft edit.");
            GC.KeepAlive(before);
            GC.KeepAlive(next);
            GC.KeepAlive(reparsed);
            GC.KeepAlive(encoded);
        }

        Console.WriteLine("Read-only benchmark: warm-up, then " + iterations + " measured commit rounds; no repository writes or GUI.");
        CommitRound(0, false);
        for (var i = 1; i <= iterations; i++)
        {
            CollectBetweenRounds();
            CommitRound(i, true);
            Console.WriteLine($"Commit round {i}: {samples.Last().ElapsedMs:F1} ms; {samples.Last().AllocatedBytes / 1048576.0:F1} MiB allocated.");
        }

        // Baselines isolate pure domain validation and editor-open cloning from the
        // parser/serializer pipeline. These do not accumulate undo strings or drafts.
        foreach (var project in projects) PmSerializer.EnsureValid(project);
        _ = PmSerializer.Clone(projects[0]);
        for (var i = 1; i <= iterations; i++)
        {
            CollectBetweenRounds();
            samples.Add(Measure(i, "baseline.EnsureValid.all20000", () =>
            {
                foreach (var project in projects) PmSerializer.EnsureValid(project);
            }));
            Project? clone = null;
            samples.Add(Measure(i, "baseline.editorOpen.Clone.oneProject2000", () => clone = PmSerializer.Clone(projects[0])));
            GC.KeepAlive(clone);
        }

        var finalHashes = paths.ToDictionary(path => Path.GetFileName(path)!, HashFile);
        var fixtureUnchanged = initialHashes.All(pair => finalHashes[pair.Key] == pair.Value);
        if (!fixtureUnchanged) throw new IOException("Fixture files changed during the benchmark; discard this run.");

        var summary = samples.GroupBy(sample => sample.Stage).Select(group => new
        {
            stage = group.Key,
            iterations = group.Count(),
            elapsedMsMedian = Median(group.Select(sample => sample.ElapsedMs)),
            elapsedMsMean = group.Average(sample => sample.ElapsedMs),
            elapsedMsMin = group.Min(sample => sample.ElapsedMs),
            elapsedMsMax = group.Max(sample => sample.ElapsedMs),
            processCpuMsMedian = Median(group.Select(sample => sample.ProcessCpuMs)),
            allocatedBytesMean = group.Average(sample => sample.AllocatedBytes),
            gen0Total = group.Sum(sample => sample.Gen0),
            gen1Total = group.Sum(sample => sample.Gen1),
            gen2Total = group.Sum(sample => sample.Gen2)
        }).ToArray();
        var report = new
        {
            schemaVersion = 1,
            benchmarkKind = "pureproject-readonly-commit-cpu",
            generatedAtUtc = DateTimeOffset.UtcNow,
            fixtureDirectory,
            fixtureUnchanged,
            fixtureHashes = initialHashes,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            serverGc = GCSettings.IsServerGC,
            iterations,
            warmupRounds = 1,
            sourceCharacters = source.Length,
            undoSnapshotCharacters = undoCharacters,
            undoSnapshotStringPayloadBytes = undoCharacters * sizeof(char),
            encodedBytes,
            methodology = new[]
            {
                "Actual Core methods; same operation ordering as CommitAsync and repository pre-I/O work, with an existing-task title edit in memory.",
                "One warm-up round; forced GC only between complete rounds; natural GC inside each round is included.",
                "Stage stopwatch elapsed time, process CPU delta and current-thread allocated bytes; CPU granularity depends on Windows accounting.",
                "No JsonProjectRepository instance, disk write, GUI render, dispatcher scheduling or UI automation; result is not end-to-end save latency.",
                "Only one undo snapshot is retained per round, so long-session undo-stack retention is not included.",
                "Baseline EnsureValid time is not additive: existing export/parse/SaveTask stages already include validation."
            },
            summary,
            samples
        };
        var json = JsonSerializer.Serialize(report, ReportOptions);
        await File.WriteAllTextAsync(reportPath, json, new UTF8Encoding(false));
        Console.WriteLine(JsonSerializer.Serialize(summary, ReportOptions));
        Console.WriteLine("Report: " + reportPath);
        Console.WriteLine("Project data, settings and fixture marker SHA-256 are unchanged.");
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static double Median(IEnumerable<double> sequence)
    {
        var values = sequence.Order().ToArray();
        return values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }
    private sealed record Sample(int Iteration, string Stage, double ElapsedMs, double ProcessCpuMs,
        long AllocatedBytes, int Gen0, int Gen1, int Gen2);
}
