using System.Text;

namespace SmartMacro.Core.Storage;

/// <summary>
/// Writes files safely: data goes to a temp file first and only replaces the real file
/// once it's completely on disk. A crash mid-save leaves the old file intact,
/// and the previous version is kept as "&lt;file&gt;.bak".
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string BackupPathFor(string path) => path + ".bak";

    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The path has no folder.", nameof(path));
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Utf8NoBom.GetBytes(content);
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(fullPath))
                File.Replace(tempPath, fullPath, BackupPathFor(fullPath), ignoreMetadataErrors: true);
            else
                File.Move(tempPath, fullPath);
        }
        finally
        {
            // Only left behind if something failed before the swap.
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Leftover temp files are harmless and get ignored when loading.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
