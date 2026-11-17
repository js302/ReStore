namespace ReStore.Core.src.utils;

/// <summary>
/// File reads and swaps that tolerate other processes on Windows. ReStore's state and config files are read and rewritten by the GUI,
/// the CLI and the service at the same time, and antivirus/indexers open files briefly. A swap (<see cref="File.Replace(string, string, string?)"/>,
/// <see cref="File.Move(string, string, bool)"/>) fails with a sharing violation ("Unable to remove the file to be replaced") while any
/// handle is open without delete sharing. So reads share read/write/delete (a swap can replace the file under an open reader) and
/// swaps retry briefly on transient errors.
/// </summary>
public static class SharedFile
{
    internal const int DefaultAttempts = 8;

    /// <summary>A temp name no other writer can share (GUI, service and CLI write the same files): <c>&lt;file&gt;.&lt;pid&gt;.&lt;guid&gt;.tmp</c>.</summary>
    public static string UniqueTempPath(string path) => $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

    /// <summary>Deletes <paramref name="path"/> when it exists; never throws (a leftover temp file is harmless).</summary>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>Writes through a unique temp file and moves it over <paramref name="path"/>; the temp file is removed when anything fails.</summary>
    public static void WriteAllTextAtomic(string path, string text, System.Text.Encoding? encoding = null) =>
        WriteAtomic(path, temp => File.WriteAllText(temp, text, encoding ?? new System.Text.UTF8Encoding(false)));

    /// <summary>Bytes variant of <see cref="WriteAllTextAtomic"/>.</summary>
    public static void WriteAllBytesAtomic(string path, byte[] bytes) => WriteAtomic(path, temp => File.WriteAllBytes(temp, bytes));

    private static void WriteAtomic(string path, Action<string> write)
    {
        var temp = UniqueTempPath(path);
        try
        {
            write(temp);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Reads a whole text file without blocking concurrent writers, renames or replacements of it.</summary>
    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary><see cref="File.Replace(string, string, string?)"/> with a short retry on transient sharing errors.</summary>
    public static Task ReplaceAsync(string sourcePath, string destinationPath, string? backupPath, CancellationToken cancellationToken = default) =>
        RetryAsync(() => File.Replace(sourcePath, destinationPath, backupPath), DefaultAttempts, cancellationToken);

    /// <summary><see cref="File.Move(string, string, bool)"/> (overwriting) with a short retry on transient sharing errors.</summary>
    /// <remarks>When the destination exists this uses <see cref="File.Replace(string, string, string?)"/>: on Windows an overwriting move is
    /// refused (access denied) while any reader holds the destination, even with delete sharing, whereas a replace succeeds.</remarks>
    public static Task MoveOverwriteAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        RetryAsync(() =>
        {
            if (File.Exists(destinationPath))
            {
                File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
            }
        }, DefaultAttempts, cancellationToken);

    /// <summary>Runs <paramref name="action"/>, retrying transient I/O failures with a growing delay (about 0.7 s in total by default).</summary>
    internal static async Task RetryAsync(Action action, int attempts, CancellationToken cancellationToken = default, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < attempts && IsTransient(ex))
            {
                await delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sharing violations and access denied (an open handle, an antivirus scan) are transient; a missing file or folder is not.</summary>
    internal static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException && ex is not FileNotFoundException and not DirectoryNotFoundException and not PathTooLongException and not EndOfStreamException);
}
