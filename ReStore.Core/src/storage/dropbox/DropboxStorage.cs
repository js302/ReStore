using System.Runtime.CompilerServices;
using Dropbox.Api;
using Dropbox.Api.Files;
using Dropbox.Api.Sharing;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.dropbox;

public class DropboxStorage(ILogger logger) : StorageBase(logger)
{
    private DropboxClient? _dropboxClient;
    private bool _disposed = false;
    private readonly DropboxClientConfig _clientConfig = new();

    /// <summary>Test seam: routes every Dropbox API call through <paramref name="httpClient"/> (a fake handler).</summary>
    internal DropboxStorage(ILogger logger, HttpClient httpClient) : this(logger)
    {
        _clientConfig = new DropboxClientConfig("ReStore", 0) { HttpClient = httpClient };
    }

    // The Dropbox SDK does not accept cancellation tokens: calls are awaited with WaitAsync(ct), which
    // returns promptly on cancellation while the in-flight request finishes in the background.
    public override int MaxConcurrency => 4;

    protected override async Task InitializeCoreAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try
        {
            if (options.TryGetValue("refreshToken", out var refreshToken) &&
                options.TryGetValue("appKey", out var appKey) &&
                options.TryGetValue("appSecret", out var appSecret))
            {
                _dropboxClient = new DropboxClient(refreshToken, appKey, appSecret, _clientConfig);
            }
            else
            {
                var accessToken = options["accessToken"];
                _dropboxClient = new DropboxClient(accessToken, _clientConfig);
            }

            var account = await _dropboxClient.Users.GetCurrentAccountAsync().ConfigureAwait(false);
            Logger.Log($"Connected to Dropbox as: {account.Name.DisplayName}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize Dropbox storage", ex);
        }
    }

    private void ValidateOptions(Dictionary<string, string> options)
    {
        var hasAccessToken = options.ContainsKey("accessToken") && !string.IsNullOrEmpty(options["accessToken"]);
        var hasRefreshToken = options.ContainsKey("refreshToken") && !string.IsNullOrEmpty(options["refreshToken"]) &&
                              options.ContainsKey("appKey") && !string.IsNullOrEmpty(options["appKey"]) &&
                              options.ContainsKey("appSecret") && !string.IsNullOrEmpty(options["appSecret"]);

        if (!hasAccessToken && !hasRefreshToken)
        {
            throw new ArgumentException("Missing Dropbox configuration: Must provide either 'accessToken' or ('refreshToken', 'appKey', 'appSecret')");
        }
    }

    private string NormalizePath(string path)
    {
        // Dropbox expects paths to start with /
        var normalized = path.Replace("\\", "/");
        if (!normalized.StartsWith("/"))
        {
            normalized = "/" + normalized;
        }
        return normalized;
    }

    private const int UPLOAD_CHUNK_BYTES = 8 * 1024 * 1024;

    protected override async Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var fileStream = File.OpenRead(localPath);
        await UploadStreamAsync(fileStream, NormalizePath(remotePath), ct).ConfigureAwait(false);
    }

    public override async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await UploadStreamAsync(content, NormalizePath(Qualify(remotePath)), ct).ConfigureAwait(false);
    }

    /// <summary>Single request for payloads up to one chunk; otherwise an upload session (the simple call is capped at 150 MB).</summary>
    private async Task UploadStreamAsync(Stream content, string dropboxPath, CancellationToken ct)
    {
        var current = await ReadChunkAsync(content, ct).ConfigureAwait(false);
        var next = current.Length < UPLOAD_CHUNK_BYTES ? [] : await ReadChunkAsync(content, ct).ConfigureAwait(false);
        var files = _dropboxClient!.Files;

        if (next.Length == 0)
        {
            using var body = new MemoryStream(current, writable: false);
            await files.UploadAsync(dropboxPath, WriteMode.Overwrite.Instance, body: body).WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        string sessionId;
        using (var body = new MemoryStream(current, writable: false))
        {
            var started = await files.UploadSessionStartAsync(false, null, null, body).WaitAsync(ct).ConfigureAwait(false);
            sessionId = started.SessionId;
        }

        ulong offset = (ulong)current.Length;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = next;
            next = await ReadChunkAsync(content, ct).ConfigureAwait(false);
            var cursor = new UploadSessionCursor(sessionId, offset);
            using var body = new MemoryStream(chunk, writable: false);
            if (next.Length == 0)
            {
                await files.UploadSessionFinishAsync(cursor, new CommitInfo(dropboxPath, WriteMode.Overwrite.Instance), null, body)
                    .WaitAsync(ct).ConfigureAwait(false);
                return;
            }

            await files.UploadSessionAppendV2Async(cursor, false, null, body).WaitAsync(ct).ConfigureAwait(false);
            offset += (ulong)chunk.Length;
        }
    }

    private static async Task<byte[]> ReadChunkAsync(Stream content, CancellationToken ct)
    {
        var buffer = new byte[UPLOAD_CHUNK_BYTES];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await content.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    protected override async Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken ct)
    {
        var dropboxPath = NormalizePath(remotePath);

        try
        {
            using var response = await _dropboxClient!.Files.DownloadAsync(dropboxPath).WaitAsync(ct).ConfigureAwait(false);
            await DownloadAtomicAsync(localPath, async partPath =>
            {
                using var fileStream = File.Create(partPath);
                var stream = await response.GetContentAsStreamAsync().WaitAsync(ct).ConfigureAwait(false);
                await stream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (ApiException<DownloadError> ex) when (ex.ErrorResponse.IsPath && ex.ErrorResponse.AsPath.Value.IsNotFound)
        {
            throw new StorageNotFoundException($"File not found in Dropbox: {remotePath}", ex) { RemotePath = remotePath };
        }
    }

    protected override async Task<bool> ExistsCoreAsync(string remotePath, CancellationToken ct)
    {
        var dropboxPath = NormalizePath(remotePath);
        try
        {
            var metadata = await _dropboxClient!.Files.GetMetadataAsync(dropboxPath).WaitAsync(ct).ConfigureAwait(false);
            return !metadata.IsDeleted;
        }
        catch (ApiException<GetMetadataError> ex)
        {
            if (ex.ErrorResponse.IsPath && ex.ErrorResponse.AsPath.Value.IsNotFound)
            {
                return false;
            }
            throw;
        }
    }

    protected override async Task<long?> GetSizeCoreAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            var metadata = await _dropboxClient!.Files.GetMetadataAsync(NormalizePath(remotePath)).WaitAsync(ct).ConfigureAwait(false);
            return metadata.IsFile ? (long)metadata.AsFile.Size : null;
        }
        catch (ApiException<GetMetadataError> ex) when (ex.ErrorResponse.IsPath && ex.ErrorResponse.AsPath.Value.IsNotFound)
        {
            return null;
        }
    }

    protected override async IAsyncEnumerable<StorageEntry> ListCoreAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var normalized = (prefix ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var slash = normalized.LastIndexOf('/');
        var folder = slash >= 0 ? "/" + normalized[..slash] : string.Empty;

        ListFolderResult? page;
        try
        {
            page = await _dropboxClient!.Files.ListFolderAsync(folder, recursive: true).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ApiException<ListFolderError> ex) when (ex.ErrorResponse.IsPath && ex.ErrorResponse.AsPath.Value.IsNotFound)
        {
            yield break;
        }

        while (true)
        {
            foreach (var item in page.Entries)
            {
                if (!item.IsFile)
                {
                    continue;
                }

                var logical = (item.PathDisplay ?? item.PathLower).TrimStart('/');
                if (logical.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new StorageEntry(logical, (long)item.AsFile.Size, item.AsFile.ServerModified.ToUniversalTime());
                }
            }

            if (!page.HasMore)
            {
                yield break;
            }

            page = await _dropboxClient!.Files.ListFolderContinueAsync(page.Cursor).WaitAsync(ct).ConfigureAwait(false);
        }
    }

    protected override async Task DeleteCoreAsync(string remotePath, CancellationToken ct)
    {
        var dropboxPath = NormalizePath(remotePath);
        try
        {
            await _dropboxClient!.Files.DeleteV2Async(dropboxPath).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ApiException<DeleteError> ex)
        {
            if (ex.ErrorResponse.IsPathLookup && ex.ErrorResponse.AsPathLookup.Value.IsNotFound)
            {
                // Ignore if already deleted
                return;
            }
            throw;
        }
    }

    protected override async Task<string> GenerateShareLinkCoreAsync(string remotePath, TimeSpan expiration)
    {
        var path = NormalizePath(remotePath);

        if (expiration != TimeSpan.Zero && expiration != TimeSpan.MaxValue)
        {
            Logger.Log(
                $"Warning: Requested link expiration of {expiration.TotalHours:F1} hours will be ignored. " +
                "Dropbox link expiration requires a Professional or Business plan. " +
                "The generated link will not expire automatically.",
                LogLevel.Warning);
        }

        try
        {
            var settings = new SharedLinkSettings(
                requestedVisibility: RequestedVisibility.Public.Instance,
                audience: LinkAudience.Public.Instance,
                access: RequestedLinkAccessLevel.Viewer.Instance
            );

            var link = await _dropboxClient!.Sharing.CreateSharedLinkWithSettingsAsync(path, settings).ConfigureAwait(false);
            return link.Url;
        }
        catch (ApiException<CreateSharedLinkWithSettingsError> ex)
        {
            if (ex.ErrorResponse.IsSharedLinkAlreadyExists)
            {
                var links = await _dropboxClient!.Sharing.ListSharedLinksAsync(path).ConfigureAwait(false);
                return links.Links.First().Url;
            }
            throw;
        }
    }

    public override bool SupportsSharing => true;

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _dropboxClient?.Dispose();
            _dropboxClient = null;
            Logger.Log("Disposed DropboxStorage resources.", LogLevel.Debug);
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
