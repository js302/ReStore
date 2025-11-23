using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.azure;

public class AzureStorage(ILogger logger) : StorageBase(logger)
{
    private BlobContainerClient? _containerClient;
    private bool _disposed = false;

    public override async Task InitializeAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try 
        {
            var connectionString = options["connectionString"];
            var containerName = options["containerName"];

            _containerClient = new BlobContainerClient(connectionString, containerName);
            await _containerClient.CreateIfNotExistsAsync();
            
            Logger.Log($"Connected to Azure Blob Container: {containerName}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize Azure storage", ex);
        }
    }

    private void ValidateOptions(Dictionary<string, string> options)
    {
        var required = new[] { "connectionString", "containerName" };
        var missing = required.Where(key => !options.ContainsKey(key) || string.IsNullOrEmpty(options[key]));
        
        if (missing.Any())
        {
            throw new ArgumentException($"Missing Azure configuration: {string.Join(", ", missing)}");
        }
    }

    public override async Task UploadAsync(string localPath, string remotePath)
    {
        var blobClient = _containerClient!.GetBlobClient(remotePath);
        await blobClient.UploadAsync(localPath, overwrite: true);
    }

    public override async Task DownloadAsync(string remotePath, string localPath)
    {
        var blobClient = _containerClient!.GetBlobClient(remotePath);
        
        // Ensure local directory exists
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await blobClient.DownloadToAsync(localPath);
    }

    public override async Task<bool> ExistsAsync(string remotePath)
    {
        var blobClient = _containerClient!.GetBlobClient(remotePath);
        return await blobClient.ExistsAsync();
    }

    public override async Task DeleteAsync(string remotePath)
    {
        var blobClient = _containerClient!.GetBlobClient(remotePath);
        await blobClient.DeleteIfExistsAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _containerClient = null;
            Logger.Log("Disposed AzureStorage resources.", LogLevel.Debug);
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
