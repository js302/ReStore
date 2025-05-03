using Amazon.S3;
using Amazon.S3.Model;
using ReStore.src.utils;

namespace ReStore.src.storage.aws;

public class S3Storage(ILogger logger) : StorageBase(logger)
{
    private AmazonS3Client? _s3Client;
    private string _bucketName = string.Empty;

    public override async Task InitializeAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try
        {
            var credentials = new Amazon.Runtime.BasicAWSCredentials(
                options["accessKeyId"],
                options["secretAccessKey"]
            );

            var config = new AmazonS3Config
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options["region"])
            };

            _bucketName = options["bucketName"];
            _s3Client = new AmazonS3Client(credentials, config);

            // Verify bucket exists and is accessible
            await _s3Client.GetBucketLocationAsync(_bucketName);
            Logger.Log($"Connected to S3 bucket: {_bucketName} in region: {options["region"]}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize S3 storage", ex);
        }
    }

    private void ValidateOptions(Dictionary<string, string> options)
    {
        var required = new[] { "accessKeyId", "secretAccessKey", "region", "bucketName" };
        var missing = required.Where(key => !options.ContainsKey(key) || string.IsNullOrEmpty(options[key]));
        
        if (missing.Any())
        {
            throw new ArgumentException($"Missing S3 configuration: {string.Join(", ", missing)}");
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
}
