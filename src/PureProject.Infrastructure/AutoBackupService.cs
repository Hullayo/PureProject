using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PureProject.Core;

namespace PureProject.Infrastructure;

public sealed record AutoBackupFailure(string ProjectId, string ProjectName, string Message);

public sealed record AutoBackupResult(bool Skipped, IReadOnlyList<string> Files, IReadOnlyList<AutoBackupFailure> Failures);

/// <summary>Writes independent, recoverable project snapshots without touching the active repository.</summary>
public sealed class AutoBackupService(string? backupDirectory = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProjectExchangeService _exchange = new();
    public static string DefaultBackupDirectory => Path.Combine(AppContext.BaseDirectory, "backup");
    public string BackupDirectory { get; } = Path.GetFullPath(backupDirectory ?? DefaultBackupDirectory);

    /// <summary>Checks the fixed destination before enabling automatic backups. Leaves no probe file.</summary>
    public void EnsureBackupDirectoryWritable()
    {
        Directory.CreateDirectory(BackupDirectory);
        RejectDirectoryLink(BackupDirectory);
        var probe = Path.Combine(BackupDirectory, ".write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.DeleteOnClose);
        stream.WriteByte(0);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// The caller owns scheduling and must pass stable project snapshots that will not be mutated
    /// during the operation. File I/O, validation and compression run on a background thread.
    /// An overlapping request is skipped; cancelling prevents further snapshots from being published.
    /// </summary>
    public async Task<AutoBackupResult> BackupAsync(IReadOnlyList<Project> snapshots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(true, [], []);
        try
        {
            var projects = snapshots.ToArray();
            if (projects.Any(project => project is null)) throw new ArgumentException("备份不能包含空项目。", nameof(snapshots));
            return await Task.Run(() => WriteBackups(projects, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private AutoBackupResult WriteBackups(IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        List<string> files = [];
        List<AutoBackupFailure> failures = [];
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PmSerializer.EnsureValid(project);
                var directory = GetProjectDirectory(project);
                var path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}--{Guid.NewGuid():N}.pureproject");
                WriteSnapshot(path, project, cancellationToken);
                files.Add(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or PmValidationException or JsonException)
            {
                failures.Add(new(project.Id, project.Name, error.Message));
            }
        }
        return new(false, files, failures);
    }

    private string GetProjectDirectory(Project project)
    {
        // Fixed Windows filename rules also apply when tests run on another platform.
        const string invalid = "<>:\"/\\|?*";
        var name = new string(project.Name.Take(48).Select(character => character < 32 || invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0) name = "项目";
        if (char.IsHighSurrogate(name[^1])) name = name[..^1];
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(stem[3]))) name = "_" + name;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(project.Id)));
        var suffix = "--" + hash;
        Directory.CreateDirectory(BackupDirectory);
        RejectDirectoryLink(BackupDirectory);
        // Reuse the ID suffix after renaming a project; identical display names still remain distinct.
        var existing = Directory.EnumerateDirectories(BackupDirectory, "*" + suffix, SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal).FirstOrDefault();
        var directory = existing ?? Path.Combine(BackupDirectory, name + suffix);
        var fullPath = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.TrimEndingDirectorySeparator(BackupDirectory), StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份目录必须位于软件 backup 目录内。");
        Directory.CreateDirectory(fullPath);
        RejectDirectoryLink(fullPath);
        return fullPath;
    }

    private static void RejectDirectoryLink(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("备份目录不能是符号链接或目录联接。");
    }

    private void WriteSnapshot(string path, Project project, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                _exchange.Export(stream, [project], ProjectExchangeFormat.PureProject, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Create a new historical snapshot. Existing backups are never replaced.
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
