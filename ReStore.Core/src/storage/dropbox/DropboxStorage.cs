using Dropbox.Api;
using Dropbox.Api.Files;
using Dropbox.Api.Sharing;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.dropbox;

public class DropboxStorage(ILogger logger) : StorageBase(logger)
{
    private DropboxClient? _dropboxClient;
    private bool _disposed = false;

    public override async Task InitializeAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try
        {
            if (options.TryGetValue("refreshToken", out var refreshToken) && 
                options.TryGetValue("appKey", out var appKey) && 
                options.TryGetValue("appSecret", out var appSecret))
            {
                _dropboxClient = new DropboxClient(refreshToken, appKey, appSecret);
            }
            else
            {
                var accessToken = options["accessToken"];
                _dropboxClient = new DropboxClient(accessToken);
            }
            
            // Verify connection
            var account = await _dropboxClient.Users.GetCurrentAccountAsync();
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

    public override async Task UploadAsync(string localPath, string remotePath)
    {
        var dropboxPath = NormalizePath(remotePath);
        
        using var fileStream = File.OpenRead(localPath);
        await _dropboxClient!.Files.UploadAsync(
            dropboxPath,
            WriteMode.Overwrite.Instance,
            body: fileStream);
    }

    public override async Task DownloadAsync(string remotePath, string localPath)
    {
        var dropboxPath = NormalizePath(remotePath);

        // Ensure local directory exists
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var response = await _dropboxClient!.Files.DownloadAsync(dropboxPath);
        using var fileStream = File.Create(localPath);
        var stream = await response.GetContentAsStreamAsync();
        await stream.CopyToAsync(fileStream);
    }

    public override async Task<bool> ExistsAsync(string remotePath)
    {
        var dropboxPath = NormalizePath(remotePath);
        try
        {
            var metadata = await _dropboxClient!.Files.GetMetadataAsync(dropboxPath);
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

    public override async Task DeleteAsync(string remotePath)
    {
        var dropboxPath = NormalizePath(remotePath);
        try
        {
            await _dropboxClient!.Files.DeleteV2Async(dropboxPath);
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

    public override async Task<string> GenerateShareLinkAsync(string remotePath, TimeSpan expiration)
    {
        var path = NormalizePath(remotePath);

        try 
        {
            // Note: Expiration is only supported on Professional/Business plans
            var settings = new SharedLinkSettings(
                requestedVisibility: RequestedVisibility.Public.Instance,
                audience: LinkAudience.Public.Instance,
                access: RequestedLinkAccessLevel.Viewer.Instance
            );

            var link = await _dropboxClient!.Sharing.CreateSharedLinkWithSettingsAsync(path, settings);
            return link.Url;
        }
        catch (ApiException<CreateSharedLinkWithSettingsError> ex)
        {
            if (ex.ErrorResponse.IsSharedLinkAlreadyExists)
            {
                var links = await _dropboxClient!.Sharing.ListSharedLinksAsync(path);
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
