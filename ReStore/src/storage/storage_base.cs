using ReStore.src.utils;
using ReStore.src.storage.aws;
using ReStore.src.storage.github;
using ReStore.src.storage.google;

namespace ReStore.src.storage;

public interface IStorage
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

    protected StorageBase(ILogger logger)
    {
        Logger = logger;
    }

    public abstract Task InitializeAsync(Dictionary<string, string> options);
    public abstract Task UploadAsync(string localPath, string remotePath);
    public abstract Task DownloadAsync(string remotePath, string localPath);
    public abstract Task<bool> ExistsAsync(string remotePath);
    public abstract Task DeleteAsync(string remotePath);
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
            ["gdrive"] = logger => new DriveStorage(logger)
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
