using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.local;

public class LocalStorage : StorageBase
{
    private string _basePath = string.Empty;
    private bool _disposed = false;

    public LocalStorage(ILogger logger) : base(logger)
    {
    }

    public override Task InitializeAsync(Dictionary<string, string> options)
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

    public override async Task UploadAsync(string localPath, string remotePath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            throw new FileNotFoundException($"Source file not found: {localPath}");
        }

        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new ArgumentException("Remote path cannot be null or empty", nameof(remotePath));
        }

        var targetPath = GetFullPath(remotePath);
        var targetDirectory = Path.GetDirectoryName(targetPath);

        if (targetDirectory != null)
        {
            Directory.CreateDirectory(targetDirectory);
        }

        try
        {
            await Task.Run(() => File.Copy(localPath, targetPath, overwrite: true));
            Logger.Log($"Successfully uploaded {Path.GetFileName(localPath)} to local storage", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to upload {localPath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to upload {localPath} to local storage", ex);
        }
    }

    public override async Task DownloadAsync(string remotePath, string localPath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new ArgumentException("Remote path cannot be null or empty", nameof(remotePath));
        }

        if (string.IsNullOrWhiteSpace(localPath))
        {
            throw new ArgumentException("Local path cannot be null or empty", nameof(localPath));
        }

        var sourcePath = GetFullPath(remotePath);
        
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"File not found in local storage: {remotePath}");
        }

        var localDirectory = Path.GetDirectoryName(localPath);
        if (localDirectory != null)
        {
            Directory.CreateDirectory(localDirectory);
        }

        try
        {
            await Task.Run(() => File.Copy(sourcePath, localPath, overwrite: true));
            Logger.Log($"Successfully downloaded {Path.GetFileName(remotePath)} from local storage", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to download {remotePath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to download {remotePath} from local storage", ex);
        }
    }

    public override Task<bool> ExistsAsync(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return Task.FromResult(false);
        }

        var fullPath = GetFullPath(remotePath);
        return Task.FromResult(File.Exists(fullPath));
    }

    public override Task DeleteAsync(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new ArgumentException("Remote path cannot be null or empty", nameof(remotePath));
        }

        var fullPath = GetFullPath(remotePath);
        
        if (!File.Exists(fullPath))
        {
            Logger.Log($"File not found, cannot delete: {remotePath}", LogLevel.Warning);
            return Task.CompletedTask;
        }

        try
        {
            File.Delete(fullPath);
            Logger.Log($"Successfully deleted {remotePath} from local storage", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to delete {remotePath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to delete {remotePath} from local storage", ex);
        }

        return Task.CompletedTask;
    }

    private string GetFullPath(string remotePath)
    {
        var normalizedPath = remotePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_basePath, normalizedPath);
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
