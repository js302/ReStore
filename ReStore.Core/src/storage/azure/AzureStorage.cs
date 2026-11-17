using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.azure;

public class AzureStorage(ILogger logger) : StorageBase(logger)
{
    private BlobContainerClient? _containerClient;
    private bool _disposed = false;

    public override int MaxConcurrency => 8;

    protected override async Task InitializeCoreAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try 
        {
            var connectionString = options["connectionString"];
            var containerName = options["containerName"];

            _containerClient = new BlobContainerClient(connectionString, containerName);
            await _containerClient.CreateIfNotExistsAsync().ConfigureAwait(false);
            
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

    protected override async Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct)
    {
        var blobClient = _containerClient!.GetBlobClient(remotePath);
        await blobClient.UploadAsync(localPath, overwrite: true, cancellationToken: ct).ConfigureAwait(false);
    }

    public override async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
    {
        var blobClient = _containerClient!.GetBlobClient(Qualify(remotePath));
        await blobClient.UploadAsync(content, overwrite: true, cancellationToken: ct).ConfigureAwait(false);
    }

    protected override async Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken ct)
    {
        var blobClient = _containerClient!.GetBlobClient(remotePath);
        
        try
        {
            await DownloadAtomicAsync(localPath, partPath => blobClient.DownloadToAsync(partPath, ct)).ConfigureAwait(false);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            throw new StorageNotFoundException($"Blob not found: {remotePath}", ex) { RemotePath = remotePath };
        }
    }

    protected override async Task<bool> ExistsCoreAsync(string remotePath, CancellationToken ct)
    {
        var blobClient = _containerClient!.GetBlobClient(remotePath);
        return await blobClient.ExistsAsync(ct).ConfigureAwait(false);
    }

    protected override async Task<long?> GetSizeCoreAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            var properties = await _containerClient!.GetBlobClient(remotePath).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
            return properties.Value.ContentLength;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    protected override async IAsyncEnumerable<StorageEntry> ListCoreAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var blob in _containerClient!.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct).ConfigureAwait(false))
        {
            yield return new StorageEntry(blob.Name, blob.Properties.ContentLength ?? 0, blob.Properties.LastModified?.UtcDateTime);
        }
    }

    protected override async Task DeleteCoreAsync(string remotePath, CancellationToken ct)
    {
        var blobClient = _containerClient!.GetBlobClient(remotePath);
        await blobClient.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    protected override Task<string> GenerateShareLinkCoreAsync(string remotePath, TimeSpan expiration)
    {
        var blobClient = _containerClient!.GetBlobClient(remotePath);
        
        if (!blobClient.CanGenerateSasUri)
        {
             throw new NotSupportedException("Blob client cannot generate SAS URI. Check credentials.");
        }

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _containerClient.Name,
            BlobName = remotePath,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(expiration)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        Uri sasUri = blobClient.GenerateSasUri(sasBuilder);
        return Task.FromResult(sasUri.ToString());
    }

    public override bool SupportsSharing => true;

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
