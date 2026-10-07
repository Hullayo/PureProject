using System.Text;

namespace PureProject.Infrastructure;

public static class AtomicFile
{
    public static async Task WriteAsync(string path, string contents, bool keepBackup = true, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(contents);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
            // Cleanup must never replace the actual write failure with a different exception.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
