using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using PureProject.Core;
using PureProject.Infrastructure;

// Deliberately opt-in: each case really exports ~512 MiB of canonical data.
// Run in isolation from UI/load tests; ordinary regression tests never execute this.
static class PackageCapacityFix
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(string output)
    {
        var service = new ProjectExchangeService(); var results = new List<object>(); var failed = 0;
        var manifestShape = new { Format = "PureProject", Version = 1, Projects = Enumerable.Range(0, 9).Select(i => new { Id = "capacity-" + i, Entry = $"projects/{i:D4}.json", Sha256 = new string('0', 64) }).ToArray() };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifestShape, Json).Length;
        foreach (var delta in new[] { -1, 0, 1 })
        {
            var timer = Stopwatch.StartNew();
            var expandedTarget = ProjectExchangeService.MaximumExpandedBytes + delta;
            var path = Path.Combine(output, $"expanded-{expandedTarget}.pureproject");
            var projects = new List<Project>(); var expectedHashes = new List<string>();
            var payloadTarget = expandedTarget - manifestBytes;
            long left = payloadTarget; long actualExpanded = 0; var rejected = false; var exact = false; var preserved = false; string? error = null;
            Console.WriteLine($"START actual package expanded target {expandedTarget}, manifest {manifestBytes}");
            try
            {
                for (var i = 0; i < 9; i++)
                {
                    var size = (int)(left / (9 - i));
                    var project = Sized(size, "capacity-" + i);
                    expectedHashes.Add(Hash(project)); projects.Add(project); left -= size;
                }
                service.ExportFile(path, [Fixtures.Create("atomic-destination", 0, false)]);
                var before = FileHash(path);
                try { service.ExportFile(path, projects); }
                catch (ProjectExchangeException ex) when (delta > 0 && ex.Message.Contains("512 MB", StringComparison.Ordinal)) { rejected = true; error = ex.Message; }
                preserved = before == FileHash(path);
                projects.Clear(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                if (delta > 0)
                {
                    if (!rejected || !preserved || Directory.GetFiles(output, Path.GetFileName(path) + ".*.tmp").Length != 0) throw new Exception("oversized export did not reject atomically");
                    exact = true;
                }
                else
                {
                    using (var zip = ZipFile.OpenRead(path))
                    {
                        actualExpanded = zip.Entries.Sum(entry => entry.Length);
                        if (zip.GetEntry("manifest.json")!.Length != manifestBytes || actualExpanded != expandedTarget) throw new Exception("actual ZIP budget mismatch");
                    }
                    var actual = service.ImportFile(path);
                    exact = actual.Count == expectedHashes.Count && actual.Select(Hash).SequenceEqual(expectedHashes);
                    if (!exact) throw new Exception("full canonical project hash mismatch after reimport");
                    actual = null!;
                }
            }
            catch (Exception ex) { failed++; error = ex.ToString(); }
            results.Add(new { delta, expandedTarget, payloadTarget, manifestBytes, actualExpanded, rejected, destinationPreserved = preserved, exact, elapsedSeconds = timer.Elapsed.TotalSeconds, error });
            File.WriteAllText(Path.Combine(output, "package-capacity-result.json"), JsonSerializer.Serialize(new { success = failed == 0 && results.Count == 3, failed, completed = results.Count, results }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"END target {expandedTarget}, exact={exact}, rejected={rejected}, elapsed={timer.Elapsed.TotalSeconds:F1}s, error={error}");
            projects.Clear(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        return failed == 0 ? 0 : 1;
    }

    private static Project Sized(int size, string id)
    {
        var project = Fixtures.Create(id, 0, false);
        project.Extra["capacity"] = JsonSerializer.SerializeToElement(Array.Empty<string>());
        var remaining = size - JsonSerializer.SerializeToUtf8Bytes(project, Json).Length;
        var chunks = new List<string>();
        while (remaining > 0)
        {
            var cost = chunks.Count == 0 ? 2 : 3;
            if (remaining <= cost) throw new Exception("invalid payload sizing tail");
            var count = Math.Min(199997, remaining - cost);
            if (remaining - cost - count is 1 or 2 or 3) count -= 4;
            chunks.Add(new string((char)('a' + chunks.Count % 26), count)); remaining -= count + cost;
        }
        project.Extra["capacity"] = JsonSerializer.SerializeToElement(chunks);
        if (JsonSerializer.SerializeToUtf8Bytes(project, Json).Length != size) throw new Exception("payload sizing mismatch");
        return project;
    }
    private static string Hash(Project project) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(project, Json)));
    private static string FileHash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
}
