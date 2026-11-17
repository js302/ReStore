using System.Runtime.CompilerServices;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.local;

public class LocalStorage : StorageBase, ICreateIfAbsentStorage
{
    private string _basePath = string.Empty;
    private bool _disposed = false;

    protected override bool WarnsAboutLegacyPathOption => false;
    public override int MaxConcurrency => 4;

    public LocalStorage(ILogger logger) : base(logger)
    {
    }

    protected override Task InitializeCoreAsync(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("path", out var pathValue) || string.IsNullOrWhiteSpace(pathValue))
        {
            throw new ArgumentException("Local storage requires a 'path' option");
        }

        _basePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(pathValue));

        try
        {
            Directory.CreateDirectory(_basePath);
            Logger.Log($"Local storage initialized at: {_basePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize local storage at {_basePath}: {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>Creates the object only if absent: written to a temp name, then moved without overwrite (atomic on one volume).</summary>
    public async Task<bool> TryCreateAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        var targetPath = ResolveStoragePath(Qualify(remotePath));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempPath = $"{targetPath}.tmp-{Guid.NewGuid():N}";
        return await Task.Run(() =>
        {
            try
            {
                File.Copy(localPath, tempPath, overwrite: true);
                File.Move(tempPath, targetPath, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                return false;
            }
            finally
            {
                try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }, ct).ConfigureAwait(false);
    }

    protected override async Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            throw new FileNotFoundException($"Source file not found: {localPath}");
        }

        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new ArgumentException("Remote path cannot be null or empty", nameof(remotePath));
        }

        ct.ThrowIfCancellationRequested();
        var targetPath = ResolveStoragePath(remotePath);
        var targetDirectory = Path.GetDirectoryName(targetPath);

        if (targetDirectory != null)
        {
            Directory.CreateDirectory(targetDirectory);
        }

        // Write to a temp name and move into place so readers never see a partial file.
        var tempPath = $"{targetPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    File.Copy(localPath, tempPath, overwrite: true);
                    ct.ThrowIfCancellationRequested();
                    File.Move(tempPath, targetPath, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    throw;
                }
            }, ct).ConfigureAwait(false);
            Logger.Log($"Successfully uploaded {Path.GetFileName(localPath)} to local storage", LogLevel.Debug);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to upload {localPath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to upload {localPath} to local storage", ex);
        }
    }

    /// <summary>Streams straight into a temp name next to the target, then moves it into place (no intermediate temp file).</summary>
    public override async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new ArgumentException("Remote path cannot be null or empty", nameof(remotePath));
        }

        ct.ThrowIfCancellationRequested();
        var targetPath = ResolveStoragePath(Qualify(remotePath));
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (targetDirectory != null)
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var tempPath = $"{targetPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                await content.CopyToAsync(file, 81920, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, targetPath, overwrite: true);
            Logger.Log($"Successfully uploaded stream to {remotePath} in local storage", LogLevel.Debug);
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        catch (Exception ex)
        {
            try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Logger.Log($"Failed to upload {remotePath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to upload {remotePath} to local storage", ex);
        }
    }

    protected override async Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new ArgumentException("Remote path cannot be null or empty", nameof(remotePath));
        }

        if (string.IsNullOrWhiteSpace(localPath))
        {
            throw new ArgumentException("Local path cannot be null or empty", nameof(localPath));
        }

        ct.ThrowIfCancellationRequested();
        var sourcePath = ResolveStoragePath(remotePath);

        if (!File.Exists(sourcePath))
        {
            throw new StorageNotFoundException($"File not found in local storage: {remotePath}") { RemotePath = remotePath };
        }

        try
        {
            await DownloadAtomicAsync(localPath, partPath => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(sourcePath, partPath, overwrite: true);
            }, ct)).ConfigureAwait(false);
            Logger.Log($"Successfully downloaded {Path.GetFileName(remotePath)} from local storage", LogLevel.Debug);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to download {remotePath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to download {remotePath} from local storage", ex);
        }
    }

    protected override Task<bool> ExistsCoreAsync(string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return Task.FromResult(false);
        }

        var fullPath = ResolveStoragePath(remotePath);
        return Task.FromResult(File.Exists(fullPath));
    }

    protected override Task<long?> GetSizeCoreAsync(string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return Task.FromResult<long?>(null);
        }

        var info = new FileInfo(ResolveStoragePath(remotePath));
        return Task.FromResult<long?>(info.Exists ? info.Length : null);
    }

    protected override async IAsyncEnumerable<StorageEntry> ListCoreAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var normalized = (prefix ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var slash = normalized.LastIndexOf('/');
        var directoryPart = slash >= 0 ? normalized[..slash] : string.Empty;
        var searchRoot = directoryPart.Length == 0 ? _basePath : ResolveStoragePath(directoryPart);
        if (!Directory.Exists(searchRoot))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var logical = Path.GetRelativePath(_basePath, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!logical.StartsWith(normalized, StringComparison.Ordinal) || TempNamePattern.IsMatch(logical))
            {
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists) continue;
            }
            catch (IOException)
            {
                continue;
            }

            yield return new StorageEntry(logical, info.Length, info.LastWriteTimeUtc);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static readonly System.Text.RegularExpressions.Regex TempNamePattern =
        new(@"\.tmp-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    protected override Task DeleteCoreAsync(string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new ArgumentException("Remote path cannot be null or empty", nameof(remotePath));
        }

        var fullPath = ResolveStoragePath(remotePath);

        if (!File.Exists(fullPath))
        {
            Logger.Log($"File not found, cannot delete: {remotePath}", LogLevel.Warning);
            return Task.CompletedTask;
        }

        try
        {
            File.Delete(fullPath);
            Logger.Log($"Successfully deleted {remotePath} from local storage", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to delete {remotePath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to delete {remotePath} from local storage", ex);
        }

        return Task.CompletedTask;
    }

    private string ResolveStoragePath(string remotePath)
    {
        // Without a root every path would resolve against the process's working directory.
        if (string.IsNullOrEmpty(_basePath))
        {
            throw new InvalidOperationException("Local storage is not initialized: call InitializeAsync with a 'path' first.");
        }

        var normalizedPath = remotePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalizedPath) || HasWindowsRoot(remotePath))
        {
            throw new InvalidOperationException($"Rooted remote paths are not allowed: {remotePath}");
        }

        normalizedPath = normalizedPath
            .TrimStart(Path.DirectorySeparatorChar);

        var resolvedPath = Path.GetFullPath(Path.Combine(_basePath, normalizedPath));
        var normalizedBasePath = _basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!resolvedPath.StartsWith(normalizedBasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Remote path escapes storage root: {remotePath}");
        }

        return resolvedPath;
    }

    private static bool HasWindowsRoot(string path)
    {
        if (path.Length < 2 || path[1] != ':')
        {
            return false;
        }

        return char.IsAsciiLetter(path[0]);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Logger.Log("Disposed LocalStorage resources.", LogLevel.Debug);
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
