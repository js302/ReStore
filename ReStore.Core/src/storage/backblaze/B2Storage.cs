using Amazon.S3;
using Amazon.S3.Model;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.backblaze;

public class B2Storage(ILogger logger) : StorageBase(logger)
{
    private AmazonS3Client? _s3Client;
    private string _bucketName = string.Empty;
    private bool _disposed = false;

    public override async Task InitializeAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try
        {
            var credentials = new Amazon.Runtime.BasicAWSCredentials(
                options["keyId"],
                options["applicationKey"]
            );

            var config = new AmazonS3Config
            {
                ServiceURL = options["serviceUrl"],
                ForcePathStyle = true
            };

            _bucketName = options["bucketName"];
            _s3Client = new AmazonS3Client(credentials, config);

            // Verify bucket exists and is accessible
            await _s3Client.GetBucketLocationAsync(_bucketName);
            Logger.Log($"Connected to Backblaze B2 bucket: {_bucketName}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize Backblaze B2 storage", ex);
        }
    }

    private void ValidateOptions(Dictionary<string, string> options)
    {
        var required = new[] { "keyId", "applicationKey", "serviceUrl", "bucketName" };
        var missing = required.Where(key => !options.ContainsKey(key) || string.IsNullOrEmpty(options[key]));
        
        if (missing.Any())
        {
            throw new ArgumentException($"Missing Backblaze B2 configuration: {string.Join(", ", missing)}");
        }
    }

    public override async Task UploadAsync(string localPath, string remotePath)
    {
        var request = new PutObjectRequest
        {
            FilePath = localPath,
            BucketName = _bucketName,
            Key = remotePath
        };

        await _s3Client!.PutObjectAsync(request);
    }

    public override async Task DownloadAsync(string remotePath, string localPath)
    {
        var request = new GetObjectRequest
        {
            BucketName = _bucketName,
            Key = remotePath
        };

        using var response = await _s3Client!.GetObjectAsync(request);
        await response.WriteResponseStreamToFileAsync(localPath, false, CancellationToken.None);
    }

    public override async Task<bool> ExistsAsync(string remotePath)
    {
        try
        {
            await _s3Client!.GetObjectMetadataAsync(_bucketName, remotePath);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public override async Task DeleteAsync(string remotePath)
    {
        await _s3Client!.DeleteObjectAsync(_bucketName, remotePath);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _s3Client?.Dispose();
            _s3Client = null;
            Logger.Log("Disposed B2Storage resources.", LogLevel.Debug);
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
