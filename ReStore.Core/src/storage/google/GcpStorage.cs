using System.Runtime.CompilerServices;
using Google.Cloud.Storage.V1;
using Google.Apis.Auth.OAuth2;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.google;

public class GcpStorage(ILogger logger) : StorageBase(logger)
{
    private StorageClient? _storageClient;
    private GoogleCredential? _credential;
    private string _bucketName = string.Empty;
    private bool _disposed = false;

    public override int MaxConcurrency => 8;

    protected override async Task InitializeCoreAsync(Dictionary<string, string> options)
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
                _storageClient = await StorageClient.CreateAsync(_credential).ConfigureAwait(false);
            }
            else 
            {
                // Fallback to default credentials
                // EmulatorOrProduction only changes behaviour when STORAGE_EMULATOR_HOST is set (used by the
                // conformance tests against fake-gcs-server); otherwise this is the normal default-credential client.
                _storageClient = await new StorageClientBuilder { EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOrProduction }
                    .BuildAsync().ConfigureAwait(false);
            }

            try 
            {
                await _storageClient.GetBucketAsync(_bucketName).ConfigureAwait(false);
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

    protected override async Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct)
    {
        using var fileStream = File.OpenRead(localPath);
        await _storageClient!.UploadObjectAsync(_bucketName, remotePath, null, fileStream, cancellationToken: ct).ConfigureAwait(false);
    }

    public override async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
    {
        // The Google upload client rewinds seekable streams, so hand it a view that starts at the caller's position.
        var source = content.CanSeek && content.Position != 0 ? new OffsetReadStream(content) : content;
        await _storageClient!.UploadObjectAsync(_bucketName, Qualify(remotePath), null, source, cancellationToken: ct).ConfigureAwait(false);
    }

    protected override async Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken ct)
    {
        try
        {
            await DownloadAtomicAsync(localPath, async partPath =>
            {
                using var fileStream = File.Create(partPath);
                await _storageClient!.DownloadObjectAsync(_bucketName, remotePath, fileStream, cancellationToken: ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new StorageNotFoundException($"Object not found in bucket '{_bucketName}': {remotePath}", ex) { RemotePath = remotePath };
        }
    }

    protected override async Task<bool> ExistsCoreAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            await _storageClient!.GetObjectAsync(_bucketName, remotePath, cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    protected override async Task<long?> GetSizeCoreAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            var obj = await _storageClient!.GetObjectAsync(_bucketName, remotePath, cancellationToken: ct).ConfigureAwait(false);
            return (long?)obj.Size ?? 0;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    protected override async IAsyncEnumerable<StorageEntry> ListCoreAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var obj in _storageClient!.ListObjectsAsync(_bucketName, prefix).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return new StorageEntry(obj.Name, (long?)obj.Size ?? 0, UpdatedUtc(obj));
        }
    }

    // The timestamp is parsed lazily from the raw string; some GCS-compatible servers emit fractional-second
    // widths the client cannot parse. A bad timestamp must not fail the whole listing, so treat it as unknown.
    private static DateTime? UpdatedUtc(Google.Apis.Storage.v1.Data.Object obj)
    {
        try
        {
            return obj.UpdatedDateTimeOffset?.UtcDateTime;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    protected override async Task DeleteCoreAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            await _storageClient!.DeleteObjectAsync(_bucketName, remotePath, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Ignore if already deleted
        }
    }

    protected override async Task<string> GenerateShareLinkCoreAsync(string remotePath, TimeSpan expiration)
    {
        if (_credential == null)
        {
             throw new NotSupportedException("Cannot generate signed URL without explicit credentials (credentialPath).");
        }

        var signer = UrlSigner.FromCredential(_credential);
        return await signer.SignAsync(_bucketName, remotePath, expiration, HttpMethod.Get).ConfigureAwait(false);
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
