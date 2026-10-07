using PureProject.Core;

namespace PureProject.Infrastructure;

public sealed record ProjectTemporaryFile(string FilePath, long Bytes, DateTime LastWriteTimeUtc,
    string Action, string? Detail = null);

/// <summary>Only an explicit import/recovery may publish an uncommitted candidate.</summary>
public sealed record ProjectTemporaryRecoveryReport(IReadOnlyList<ProjectTemporaryFile> Files,
    bool ScanLimitReached, long ValidatedBytes, string? ScanWarning = null)
{
    public const int MaximumScannedEntries = 128;
    public const int MaximumCandidates = 64;
    public const int MaximumRetainedCandidates = 8;
    public const long MaximumRetainedBytes = 256L * 1024 * 1024;
    public const long MaximumValidationBytes = 16L * 1024 * 1024;
    public const long MaximumSingleValidationBytes = 8L * 1024 * 1024;
    public static TimeSpan MinimumAge => TimeSpan.FromMinutes(2);
    public bool HasUncommittedFiles => ScanLimitReached || ScanWarning is not null ||
        Files.Any(f => f.Action is not "RemovedEmpty" and not "RemovedRedundant");
    public static ProjectTemporaryRecoveryReport Empty { get; } = new([], false, 0);
}

/// <summary>Streams a staged database and validates it before replacing the prior durable file.</summary>
internal static class AtomicProjectFile
{
    /// <summary>
    /// Called only while the owning repository holds its directory/session and operation locks.
    /// Never traverses subdirectories or publishes a staged version. Budgets bound automatic work;
    /// nonempty unique/unknown candidates beyond retention budgets remain for explicit inspection.
    /// </summary>
    internal static async Task<ProjectTemporaryRecoveryReport> RecoverTemporaryFilesAsync(string target,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var results = new List<ProjectTemporaryFile>();
        var candidates = new List<FileInfo>();
        var limitReached = false;
        long validatedBytes = 0, retainedBytes = 0;
        var retainedCount = 0;
        try
        {
            // FullPath alone does not resolve a junction/symlink. Do not scan through one.
            for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    return new([], false, 0, "数据目录包含重解析点，未自动处理暂存文件。");
            var scanned = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, ".projects.json.*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > ProjectTemporaryRecoveryReport.MaximumScannedEntries || candidates.Count >= ProjectTemporaryRecoveryReport.MaximumCandidates)
                { limitReached = true; break; }
                var name = Path.GetFileName(path);
                if (!IsTemporaryCandidateName(name)) continue;
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), directory, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(path);
                if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    results.Add(new(path, 0, info.LastWriteTimeUtc, "SkippedUnsafePath", "目录或重解析点保持原样。"));
                    continue;
                }
                candidates.Add(info);
                if (name.EndsWith(".recovery", StringComparison.Ordinal))
                { retainedCount++; retainedBytes += info.Length; }
            }

            // Inspect the newest bounded set first; never delete a unique older valid version
            // merely to make a retention quota appear satisfied.
            foreach (var info in candidates.OrderByDescending(f => f.LastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = info.FullName;
                var bytes = info.Length;
                var time = info.LastWriteTimeUtc;
                try
                {
                    if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.ReadOnly)) != 0)
                    { results.Add(new(path, bytes, time, "SkippedUnsafeOrReadOnly")); continue; }
                    if (DateTime.UtcNow - time < ProjectTemporaryRecoveryReport.MinimumAge)
                    { results.Add(new(path, bytes, time, "RetainedRecent", "暂存不足两分钟，未自动处理。")); continue; }
                    if (path.EndsWith(".recovery", StringComparison.Ordinal))
                    { results.Add(new(path, bytes, time, "RetainedRecovery")); continue; }

                    var valid = false;
                    var redundant = false;
                    string? detail = null;
                    // Exclusive open excludes an active writer even if it does not honor session.lock.
                    await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        bytes = stream.Length;
                        if (bytes > 0 && bytes <= ProjectTemporaryRecoveryReport.MaximumSingleValidationBytes &&
                            validatedBytes + bytes <= ProjectTemporaryRecoveryReport.MaximumValidationBytes)
                        {
                            validatedBytes += bytes;
                            try { await PmSerializer.ValidateInternalAsync(stream, cancellationToken).ConfigureAwait(false); valid = true; }
                            catch (PmValidationException error) { detail = "校验未通过，保留供检查：" + error.Message; }
                            if (valid)
                                redundant = await SameBytesAsync(stream, target, cancellationToken).ConfigureAwait(false) ||
                                    await SameBytesAsync(stream, target + ".bak", cancellationToken).ConfigureAwait(false);
                        }
                        else if (bytes > 0) detail = "超过自动校验字节预算，未解析，保留原始内容。";
                    }
                    // Recheck the path after releasing the exclusive handle and before mutation.
                    if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.ReadOnly)) != 0)
                    { results.Add(new(path, bytes, time, "SkippedUnsafeOrReadOnly")); continue; }
                    if (bytes == 0 || redundant)
                    {
                        File.Delete(path);
                        results.Add(new(path, bytes, time, bytes == 0 ? "RemovedEmpty" : "RemovedRedundant"));
                        continue;
                    }
                    if (retainedCount >= ProjectTemporaryRecoveryReport.MaximumRetainedCandidates ||
                        bytes > ProjectTemporaryRecoveryReport.MaximumRetainedBytes - retainedBytes)
                    { results.Add(new(path, bytes, time, "RetainedOverBudget", "恢复保留配额已满；独有非空内容未删除，请手动检查。")); continue; }
                    var recovery = Path.ChangeExtension(path, ".recovery");
                    File.Move(path, recovery, overwrite: false);
                    retainedCount++; retainedBytes += bytes;
                    results.Add(new(recovery, bytes, time, valid ? "QuarantinedValid" : "QuarantinedUnverified", detail));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { results.Add(new(path, bytes, time, "RetainedUnavailable", error.Message)); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(results, limitReached, validatedBytes, error.Message); }
        return new(results, limitReached, validatedBytes);
    }

    private static bool IsTemporaryCandidateName(string name)
    {
        const string prefix = ".projects.json.";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = name.EndsWith(".tmp", StringComparison.Ordinal) ? ".tmp" :
            name.EndsWith(".recovery", StringComparison.Ordinal) ? ".recovery" : null;
        if (suffix is null || name.Length != prefix.Length + 32 + suffix.Length) return false;
        var guid = name.AsSpan(prefix.Length, 32);
        // AtomicProjectFile writes lowercase Guid:N. Reject near-matches and foreign names.
        foreach (var value in guid) if (value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        return Guid.TryParseExact(guid, "N", out _);
    }

    private static async Task<bool> SameBytesAsync(FileStream candidate, string durablePath, CancellationToken token)
    {
        if (!File.Exists(durablePath) || (File.GetAttributes(durablePath) & FileAttributes.ReparsePoint) != 0) return false;
        await using var durable = new FileStream(durablePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (candidate.Length != durable.Length) return false;
        candidate.Position = 0;
        var left = new byte[64 * 1024]; var right = new byte[left.Length];
        int count;
        while ((count = await candidate.ReadAsync(left, token).ConfigureAwait(false)) > 0)
        {
            await durable.ReadExactlyAsync(right.AsMemory(0, count), token).ConfigureAwait(false);
            if (!left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, count))) return false;
        }
        return true;
    }

    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> write,
        Func<Stream, CancellationToken, Task> validate, bool keepBackup = true, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Position = 0;
                await validate(stream, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(target))
                File.Replace(temporary, target, keepBackup ? target + ".bak" : null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, target);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
