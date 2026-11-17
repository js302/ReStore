using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.backblaze;

public class B2Storage(ILogger logger) : StorageBase(logger)
{
    private AmazonS3Client? _s3Client;
    private string _bucketName = string.Empty;
    private bool _disposed = false;

    public override int MaxConcurrency => 8;

    protected override async Task InitializeCoreAsync(Dictionary<string, string> options)
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
            await _s3Client.GetBucketLocationAsync(_bucketName).ConfigureAwait(false);
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

    protected override async Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct)
    {
        var request = new PutObjectRequest
        {
            FilePath = localPath,
            BucketName = _bucketName,
            Key = remotePath
        };

        await _s3Client!.PutObjectAsync(request, ct).ConfigureAwait(false);
    }

    public override async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
    {
        if (!content.CanSeek)
        {
            await base.UploadAsync(content, remotePath, ct).ConfigureAwait(false);
            return;
        }

        // The S3 client rewinds the stream it is given, so hand it a view whose start is the caller's position.
        var source = content.Position == 0 ? content : new OffsetReadStream(content);
        var request = new PutObjectRequest
        {
            InputStream = source,
            AutoCloseStream = false,
            BucketName = _bucketName,
            Key = Qualify(remotePath)
        };
        request.Headers.ContentLength = source.Length - source.Position;

        await _s3Client!.PutObjectAsync(request, ct).ConfigureAwait(false);
    }

    protected override async Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken ct)
    {
        var request = new GetObjectRequest
        {
            BucketName = _bucketName,
            Key = remotePath
        };

        try
        {
            using var response = await _s3Client!.GetObjectAsync(request, ct).ConfigureAwait(false);
            await DownloadAtomicAsync(localPath, partPath =>
                response.WriteResponseStreamToFileAsync(partPath, false, ct)).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            throw new StorageNotFoundException($"Object not found in bucket '{_bucketName}': {remotePath}", ex) { RemotePath = remotePath };
        }
    }

    protected override async Task<bool> ExistsCoreAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            await _s3Client!.GetObjectMetadataAsync(_bucketName, remotePath, ct).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    protected override async Task<long?> GetSizeCoreAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            var response = await _s3Client!.GetObjectMetadataAsync(_bucketName, remotePath, ct).ConfigureAwait(false);
            return response.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    protected override async IAsyncEnumerable<StorageEntry> ListCoreAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        string? token = null;
        do
        {
            var response = await _s3Client!.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucketName,
                Prefix = prefix,
                ContinuationToken = token
            }, ct).ConfigureAwait(false);

            foreach (var item in response.S3Objects ?? [])
            {
                yield return new StorageEntry(item.Key, item.Size ?? 0, item.LastModified?.ToUniversalTime());
            }

            token = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (token != null);
    }

    protected override async Task DeleteCoreAsync(string remotePath, CancellationToken ct)
    {
        await _s3Client!.DeleteObjectAsync(_bucketName, remotePath, ct).ConfigureAwait(false);
    }

    protected override Task<string> GenerateShareLinkCoreAsync(string remotePath, TimeSpan expiration)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = remotePath,
            Expires = DateTime.UtcNow.Add(expiration)
        };

        string url = _s3Client!.GetPreSignedURL(request);
        return Task.FromResult(url);
    }

    public override bool SupportsSharing => true;

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
