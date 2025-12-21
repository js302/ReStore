using ReStore.Core.src.utils;
using ReStore.Core.src.storage.aws;
using ReStore.Core.src.storage.github;
using ReStore.Core.src.storage.google;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.storage.azure;
using ReStore.Core.src.storage.dropbox;
using ReStore.Core.src.storage.sftp;
using ReStore.Core.src.storage.backblaze;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReStore.Core.src.storage;

// Inherit from IDisposable
public interface IStorage : IDisposable
{
    Task InitializeAsync(Dictionary<string, string> options);
    Task UploadAsync(string localPath, string remotePath);
    Task DownloadAsync(string remotePath, string localPath);
    Task<bool> ExistsAsync(string remotePath);
    Task DeleteAsync(string remotePath);
    Task<string> GenerateShareLinkAsync(string remotePath, TimeSpan expiration);
    bool SupportsSharing { get; }
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

    public virtual Task<string> GenerateShareLinkAsync(string remotePath, TimeSpan expiration)
    {
        throw new NotSupportedException("Sharing is not supported by this storage provider.");
    }

    public virtual bool SupportsSharing => false;

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
            ["local"] = logger => new LocalStorage(logger),
            ["azure"] = logger => new AzureStorage(logger),
            ["gcp"] = logger => new GcpStorage(logger),
            ["dropbox"] = logger => new DropboxStorage(logger),
            ["sftp"] = logger => new SftpStorage(logger),
            ["b2"] = logger => new B2Storage(logger)
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
