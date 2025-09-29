using ReStore.src.utils;
using ReStore.src.storage.aws;
using ReStore.src.storage.github;
using ReStore.src.storage.google;
using ReStore.src.storage.local;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReStore.src.storage;

// Inherit from IDisposable
public interface IStorage : IDisposable
{
    Task InitializeAsync(Dictionary<string, string> options);
    Task UploadAsync(string localPath, string remotePath);
    Task DownloadAsync(string remotePath, string localPath);
    Task<bool> ExistsAsync(string remotePath);
    Task DeleteAsync(string remotePath);
}

public abstract class StorageBase : IStorage
{
    protected readonly ILogger Logger;
    private bool _disposed = false; // Track disposal status

    protected StorageBase(ILogger logger)
    {
        Logger = logger;
    }

    public abstract Task InitializeAsync(Dictionary<string, string> options);
    public abstract Task UploadAsync(string localPath, string remotePath);
    public abstract Task DownloadAsync(string remotePath, string localPath);
    public abstract Task<bool> ExistsAsync(string remotePath);
    public abstract Task DeleteAsync(string remotePath);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    // Protected virtual Dispose method for subclasses to override
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // Dispose managed state (managed objects).
            // Subclasses will override this to dispose their specific clients.
            Logger.Log($"Disposing {GetType().Name} resources.", LogLevel.Debug);
        }

        // Free unmanaged resources (unmanaged objects) and override finalizer
        // Set large fields to null

        _disposed = true;
    }

    // Optional Finalizer (only if the base class directly owns unmanaged resources) Consider if needed
    // ~StorageBase()
    // {
    //     Dispose(false);
    // }
}

public class StorageFactory
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, Func<ILogger, IStorage>> _storageCreators;

    public StorageFactory(ILogger logger)
    {
        _logger = logger;
        _storageCreators = new()
        {
            ["s3"] = logger => new S3Storage(logger),
            ["github"] = logger => new GitHubStorage(logger),
            ["gdrive"] = logger => new DriveStorage(logger),
            ["local"] = logger => new LocalStorage(logger)
        };
    }

    public async Task<IStorage> CreateStorageAsync(string storageType, StorageConfig config)
    {
        if (!_storageCreators.TryGetValue(storageType.ToLower(), out var creator))
        {
            throw new ArgumentException($"Unsupported storage type: {storageType}");
        }

        var storage = creator(_logger);
        await storage.InitializeAsync(config.Options);
        return storage;
    }
}
