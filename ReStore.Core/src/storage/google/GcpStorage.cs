using Google.Cloud.Storage.V1;
using Google.Apis.Auth.OAuth2;
using ReStore.Core.src.utils;
using System.IO;

namespace ReStore.Core.src.storage.google;

public class GcpStorage(ILogger logger) : StorageBase(logger)
{
    private StorageClient? _storageClient;
    private GoogleCredential? _credential;
    private string _bucketName = string.Empty;
    private bool _disposed = false;

    public override async Task InitializeAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try
        {
            _bucketName = options["bucketName"];
            
            if (options.TryGetValue("credentialPath", out var credentialPath) && !string.IsNullOrEmpty(credentialPath))
            {
                using var stream = File.OpenRead(credentialPath);
                #pragma warning disable CS0618 // Type or member is obsolete
                _credential = GoogleCredential.FromStream(stream);
                #pragma warning restore CS0618 // Type or member is obsolete
                _storageClient = await StorageClient.CreateAsync(_credential);
            }
            else 
            {
                // Fallback to default credentials
                _storageClient = await StorageClient.CreateAsync();
            }

            // Verify bucket exists
            try 
            {
                await _storageClient.GetBucketAsync(_bucketName);
                Logger.Log($"Connected to GCP Bucket: {_bucketName}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Bucket '{_bucketName}' not found or not accessible.", ex);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize GCP storage", ex);
        }
    }

    private void ValidateOptions(Dictionary<string, string> options)
    {
        var required = new[] { "bucketName" };
        var missing = required.Where(key => !options.ContainsKey(key) || string.IsNullOrEmpty(options[key]));
        
        if (missing.Any())
        {
            throw new ArgumentException($"Missing GCP configuration: {string.Join(", ", missing)}");
        }
    }

    public override async Task UploadAsync(string localPath, string remotePath)
    {
        using var fileStream = File.OpenRead(localPath);
        await _storageClient!.UploadObjectAsync(_bucketName, remotePath, null, fileStream);
    }

    public override async Task DownloadAsync(string remotePath, string localPath)
    {
        // Ensure local directory exists
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fileStream = File.Create(localPath);
        await _storageClient!.DownloadObjectAsync(_bucketName, remotePath, fileStream);
    }

    public override async Task<bool> ExistsAsync(string remotePath)
    {
        try
        {
            await _storageClient!.GetObjectAsync(_bucketName, remotePath);
            return true;
        }
        catch (Google.GoogleApiException ex) when (ex.Error.Code == 404)
        {
            return false;
        }
    }

    public override async Task DeleteAsync(string remotePath)
    {
        try
        {
            await _storageClient!.DeleteObjectAsync(_bucketName, remotePath);
        }
        catch (Google.GoogleApiException ex) when (ex.Error.Code == 404)
        {
            // Ignore if already deleted
        }
    }

    public override async Task<string> GenerateShareLinkAsync(string remotePath, TimeSpan expiration)
    {
        if (_credential == null)
        {
             throw new NotSupportedException("Cannot generate signed URL without explicit credentials (credentialPath).");
        }

        var signer = UrlSigner.FromCredential(_credential);
        return await signer.SignAsync(_bucketName, remotePath, expiration, HttpMethod.Get);
    }

    public override bool SupportsSharing => true;

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _storageClient?.Dispose();
            _storageClient = null;
            Logger.Log("Disposed GcpStorage resources.", LogLevel.Debug);
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
