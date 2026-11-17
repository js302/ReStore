using System.Runtime.CompilerServices;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.google;

public class DriveStorage(ILogger logger) : StorageBase(logger)
{
    private DriveService? _driveService;
    private string _backupFolderId = string.Empty;
    private const string BACKUP_FOLDER_NAME = "ReStoreBackups";
    private const int MAX_RETRY_ATTEMPTS = 6;
    private static readonly TimeSpan AUTH_TIMEOUT = TimeSpan.FromMinutes(5);
    private readonly CancellationTokenSource _disposeCts = new();
    private static readonly TimeSpan CACHE_TTL = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (string Id, DateTime CachedAt)> _folderCache = [];
    private readonly object _cacheLock = new();
    // Serialises folder creation so concurrent uploads never create duplicate folders with the same name.
    private readonly SemaphoreSlim _folderCreateLock = new(1, 1);
    private bool _disposed = false;

    public override int MaxConcurrency => 3;

    private bool TryGetCachedFolderId(string path, out string? folderId)
    {
        lock (_cacheLock)
        {
            if (_folderCache.TryGetValue(path, out var cached))
            {
                if (DateTime.UtcNow - cached.CachedAt < CACHE_TTL)
                {
                    folderId = cached.Id;
                    return true;
                }
                _folderCache.Remove(path);
            }
        }
        folderId = null;
        return false;
    }

    private void CacheFolderId(string path, string folderId)
    {
        lock (_cacheLock)
        {
            _folderCache[path] = (folderId, DateTime.UtcNow);
        }
    }

    /// <summary>Test seam: uses an already-built (fake-backed) service instead of the interactive OAuth flow.</summary>
    internal async Task AttachServiceAsync(DriveService service, string backupFolderName = BACKUP_FOLDER_NAME)
    {
        _driveService = service;
        _backupFolderId = await GetOrCreateBackupFolderAsync(backupFolderName).ConfigureAwait(false);
    }

    protected override async Task InitializeCoreAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try
        {
            var clientSecrets = new ClientSecrets
            {
                ClientId = options["client_id"],
                ClientSecret = options["client_secret"]
            };

            string tokenFolder = options.TryGetValue("token_folder", out var folder)
                ? folder
                : DefaultTokenFolder;

            // Interactive consent opens a browser and waits for a loopback callback; bound the wait so an
            // unattended (scheduled) run fails instead of hanging forever.
            using var authTimeout = new CancellationTokenSource(AUTH_TIMEOUT);
            Google.Apis.Auth.OAuth2.UserCredential credential;
            try
            {
                credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    clientSecrets,
                    // Full Drive scope (not drive.file) is required: existing tokens and previously uploaded data depend on it.
                    [DriveService.Scope.Drive],
                    TokenUserKey,
                    authTimeout.Token,
                    CreateTokenStore(tokenFolder, Logger)
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (authTimeout.IsCancellationRequested)
            {
                throw new StorageAuthRequiredException(
                    $"Google Drive authorization was not completed within {AUTH_TIMEOUT.TotalMinutes:0} minutes. " +
                    "Run ReStore interactively to sign in to Google Drive, then retry the scheduled backup.");
            }

            _driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "ReStore Backup"
            });

            string backupFolderName = options.TryGetValue("backup_folder_name", out var customName)
                ? customName
                : BACKUP_FOLDER_NAME;

            _backupFolderId = await GetOrCreateBackupFolderAsync(backupFolderName).ConfigureAwait(false);
            Logger.Log($"Initialized Google Drive storage with backup folder: {_backupFolderId}");
        }
        catch (StorageAuthRequiredException ex)
        {
            Logger.Log(ex.Message, LogLevel.Error);
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to initialize Google Drive service: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException("Failed to initialize Google Drive service", ex);
        }
    }

    /// <summary>
    /// DPAPI-encrypted token cache on Windows (migrating tokens a previous version left in the plaintext
    /// <see cref="FileDataStore"/>); the plain store elsewhere, where DPAPI does not exist.
    /// </summary>
    internal static IDataStore CreateTokenStore(string tokenFolder, ILogger? logger = null, Func<bool>? isUnattended = null) =>
        DpapiSecretProtector.Instance.IsAvailable
            ? new DpapiDataStore(DpapiDataStore.ResolveFolder(tokenFolder), DpapiSecretProtector.Instance, new FileDataStore(tokenFolder), logger, isUnattended ?? (() => UnattendedMode.IsActive))
            : new FileDataStore(tokenFolder);

    /// <summary>The token folder used when the <c>token_folder</c> option is absent.</summary>
    internal const string DefaultTokenFolder = "Drive.Storage.Token";

    /// <summary>The user key the sign-in token is stored under; <see cref="DriveSignIn"/> uses the same.</summary>
    internal const string TokenUserKey = "user";

    /// <summary>
    /// True when a sign-in token is stored for <paramref name="tokenFolder"/> (the DPAPI cache, or the plain legacy store). Only looks
    /// at files; never creates a folder, opens a browser or decrypts anything.
    /// </summary>
    internal static bool HasStoredToken(string tokenFolder)
    {
        var key = $"{typeof(Google.Apis.Auth.OAuth2.Responses.TokenResponse).FullName}-{TokenUserKey}";
        var candidates = new List<string> { Path.Combine(DpapiDataStore.ResolveFolder(tokenFolder), Uri.EscapeDataString(key) + ".dpapi") };
        candidates.Add(Path.IsPathRooted(tokenFolder)
            ? Path.Combine(tokenFolder, key)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), tokenFolder, key));
        return candidates.Any(File.Exists);
    }

    private static void ValidateOptions(Dictionary<string, string> options)
    {
        var required = new[] { "client_id", "client_secret" };
        var missing = required.Where(key => !options.ContainsKey(key) || string.IsNullOrEmpty(options[key])).ToList();

        if (missing.Count != 0)
        {
            throw new ArgumentException($"Missing Google Drive configuration: {string.Join(", ", missing)}");
        }
    }

    private async Task<string> GetOrCreateBackupFolderAsync(string folderName = BACKUP_FOLDER_NAME)
    {
        try
        {
            var listRequest = _driveService!.Files.List();
            listRequest.Q = $"name='{EscapeDriveQueryValue(folderName)}' and mimeType='application/vnd.google-apps.folder' and trashed=false";
            listRequest.Spaces = "drive";
            listRequest.Fields = "files(id, name)";

            var folders = await ExecuteWithRetryAsync(async () => await listRequest.ExecuteAsync().ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);

            if (folders.Files.Count > 0)
            {
                return folders.Files[0].Id;
            }

            var folderMetadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = folderName,
                MimeType = "application/vnd.google-apps.folder"
            };

            var request = _driveService.Files.Create(folderMetadata);
            request.Fields = "id";
            var folder = await ExecuteWithRetryAsync(async () => await request.ExecuteAsync().ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);

            return folder.Id;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to create backup folder: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to create backup folder '{folderName}'", ex);
        }
    }

    protected override async Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct)
    {
        try
        {
            string parentId = await EnsureDirectoryPathExistsAsync(remotePath, ct).ConfigureAwait(false);
            var fileName = Path.GetFileName(remotePath);
            var existingFileIds = await GetFileIdsByNameInParentAsync(fileName, parentId, ct).ConfigureAwait(false);

            var fileMetadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = fileName
            };

            using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            void ReportProgress(IUploadProgress p)
            {
                if (p.Status == UploadStatus.Uploading && p.BytesSent > 0 && stream.Length > 0)
                {
                    var percentComplete = (int)((double)p.BytesSent / stream.Length * 100);
                    Logger.Log($"Uploading {Path.GetFileName(localPath)}: {percentComplete}%", LogLevel.Debug);
                }
            }

            IUploadProgress result;
            if (existingFileIds.Count > 0)
            {
                var updateRequest = _driveService!.Files.Update(fileMetadata, existingFileIds[0], stream, GetMimeType(localPath));
                updateRequest.Fields = "id, name, size";
                updateRequest.ProgressChanged += ReportProgress;

                result = await ExecuteWithRetryAsync(async () =>
                {
                    // Each retry must resend from the start.
                    stream.Position = 0;
                    return ThrowIfUploadFailed(await updateRequest.UploadAsync(ct).ConfigureAwait(false));
                }, ct).ConfigureAwait(false);
            }
            else
            {
                fileMetadata.Parents = [parentId];
                var createRequest = _driveService!.Files.Create(fileMetadata, stream, GetMimeType(localPath));
                createRequest.Fields = "id, name, size";
                createRequest.ProgressChanged += ReportProgress;

                result = await ExecuteWithRetryAsync(async () =>
                {
                    stream.Position = 0;
                    return ThrowIfUploadFailed(await createRequest.UploadAsync(ct).ConfigureAwait(false));
                }, ct).ConfigureAwait(false);
            }

            if (result.Status != UploadStatus.Completed)
            {
                throw new Exception($"Upload failed: {result.Status} - {result.Exception?.Message}");
            }

            foreach (var duplicateFileId in existingFileIds.Skip(1))
            {
                try
                {
                    await ExecuteWithRetryAsync(async () =>
                    {
                        await _driveService!.Files.Delete(duplicateFileId).ExecuteAsync(ct).ConfigureAwait(false);
                        return true;
                    }, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Log($"Failed to delete duplicate Google Drive file for {remotePath}: {ex.Message}", LogLevel.Warning);
                }
            }

            Logger.Log($"Successfully uploaded {Path.GetFileName(localPath)} to Google Drive", LogLevel.Debug);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to upload {localPath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to upload {localPath} to Google Drive", ex);
        }
    }

    protected override async Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken ct)
    {
        try
        {
            var fileId = await GetFileIdByPathAsync(remotePath, ct).ConfigureAwait(false);
            var request = _driveService!.Files.Get(fileId);

            await DownloadAtomicAsync(localPath, async partPath =>
            {
                using var stream = new FileStream(partPath, FileMode.Create);

                await ExecuteWithRetryAsync(async () =>
                {
                    // Discard whatever a failed attempt wrote.
                    stream.Position = 0;
                    stream.SetLength(0);
                    var progress = await request.DownloadAsync(stream, ct).ConfigureAwait(false);
                    ThrowIfDownloadFailed(progress);
                    return true;
                }, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            Logger.Log($"Successfully downloaded {Path.GetFileName(remotePath)} from Google Drive", LogLevel.Debug);
        }
        catch (Exception ex) when (ex is StorageNotFoundException or OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to download {remotePath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to download {remotePath} from Google Drive", ex);
        }
    }

    protected override async Task<bool> ExistsCoreAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            await GetFileIdByPathAsync(remotePath, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"Error checking if file exists: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    protected override async Task DeleteCoreAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            var fileId = await GetFileIdByPathAsync(remotePath, ct).ConfigureAwait(false);

            await ExecuteWithRetryAsync(async () =>
            {
                await _driveService!.Files.Delete(fileId).ExecuteAsync(ct).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);

            Logger.Log($"Successfully deleted {remotePath} from Google Drive", LogLevel.Debug);
        }
        catch (StorageNotFoundException)
        {
            // Already gone: delete of a missing path is a no-op.
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Removed between lookup and delete.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to delete {remotePath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to delete {remotePath} from Google Drive", ex);
        }
    }

    private async Task<string> GetFileIdByPathAsync(string remotePath, CancellationToken ct)
    {
        var pathParts = remotePath.Split('/', '\\').Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (pathParts.Length == 0)
        {
            throw new ArgumentException("Invalid remote path");
        }

        string fileName = pathParts[^1];

        string parentId = _backupFolderId;
        if (pathParts.Length > 1)
        {
            string dirPath = string.Join("/", pathParts.Take(pathParts.Length - 1));
            try
            {
                parentId = await GetFolderIdByPathAsync(dirPath, ct).ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException ex)
            {
                throw new StorageNotFoundException($"File {fileName} not found in Google Drive path {remotePath}", ex) { RemotePath = remotePath };
            }
        }

        var fileIds = await GetFileIdsByNameInParentAsync(fileName, parentId, ct).ConfigureAwait(false);
        if (fileIds.Count == 0)
        {
            throw new StorageNotFoundException($"File {fileName} not found in Google Drive path {remotePath}") { RemotePath = remotePath };
        }

        return fileIds[0];
    }

    private async Task<List<Google.Apis.Drive.v3.Data.File>> GetFilesByNameInParentAsync(string fileName, string parentId, CancellationToken ct)
    {
        var listRequest = _driveService!.Files.List();
        listRequest.Q = $"name='{EscapeDriveQueryValue(fileName)}' and '{parentId}' in parents and trashed=false";
        listRequest.Fields = "files(id, name, size, modifiedTime)";
        listRequest.OrderBy = "modifiedTime desc";

        var files = await ExecuteWithRetryAsync(async () => await listRequest.ExecuteAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return files.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.Id))
            .ToList();
    }

    private async Task<List<string>> GetFileIdsByNameInParentAsync(string fileName, string parentId, CancellationToken ct)
    {
        var files = await GetFilesByNameInParentAsync(fileName, parentId, ct).ConfigureAwait(false);
        return files.Select(file => file.Id).ToList();
    }

    protected override async Task<long?> GetSizeCoreAsync(string remotePath, CancellationToken ct)
    {
        var pathParts = remotePath.Split('/', '\\').Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (pathParts.Length == 0)
        {
            return null;
        }

        var parentId = _backupFolderId;
        if (pathParts.Length > 1)
        {
            try
            {
                parentId = await GetFolderIdByPathAsync(string.Join("/", pathParts.Take(pathParts.Length - 1)), ct).ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        var files = await GetFilesByNameInParentAsync(pathParts[^1], parentId, ct).ConfigureAwait(false);
        return files.Count == 0 ? null : files[0].Size ?? 0;
    }

    protected override async IAsyncEnumerable<StorageEntry> ListCoreAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var normalized = (prefix ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var slash = normalized.LastIndexOf('/');
        var directoryPart = slash >= 0 ? normalized[..slash] : string.Empty;

        string rootId;
        if (directoryPart.Length == 0)
        {
            rootId = _backupFolderId;
        }
        else
        {
            try
            {
                rootId = await GetFolderIdByPathAsync(directoryPart, ct).ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException)
            {
                yield break;
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(string Id, string Path)>();
        pending.Push((rootId, directoryPart));
        while (pending.Count > 0)
        {
            var (folderId, folderPath) = pending.Pop();
            string? pageToken = null;
            do
            {
                var request = _driveService!.Files.List();
                request.Q = $"'{folderId}' in parents and trashed=false";
                request.Fields = "nextPageToken, files(id, name, mimeType, size, modifiedTime)";
                request.PageSize = 1000;
                request.PageToken = pageToken;

                var page = await ExecuteWithRetryAsync(async () => await request.ExecuteAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                foreach (var file in page.Files ?? [])
                {
                    var logical = folderPath.Length == 0 ? file.Name : folderPath + "/" + file.Name;
                    if (file.MimeType == "application/vnd.google-apps.folder")
                    {
                        pending.Push((file.Id, logical));
                    }
                    else if (logical.StartsWith(normalized, StringComparison.Ordinal) && seen.Add(logical))
                    {
                        yield return new StorageEntry(logical, file.Size ?? 0, file.ModifiedTimeDateTimeOffset?.UtcDateTime);
                    }
                }

                pageToken = page.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));
        }
    }

    private async Task<string> GetFolderIdByPathAsync(string folderPath, CancellationToken ct)
    {
        if (TryGetCachedFolderId(folderPath, out var cachedId))
        {
            return cachedId!;
        }

        var pathParts = folderPath.Split('/', '\\').Where(p => !string.IsNullOrEmpty(p)).ToArray();
        string currentParentId = _backupFolderId;
        string currentPath = "";

        foreach (var folderName in pathParts)
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? folderName : currentPath + "/" + folderName;

            if (TryGetCachedFolderId(currentPath, out var partialCachedId))
            {
                currentParentId = partialCachedId!;
                continue;
            }

            var listRequest = _driveService!.Files.List();
            listRequest.Q = $"name='{EscapeDriveQueryValue(folderName)}' and '{currentParentId}' in parents and mimeType='application/vnd.google-apps.folder' and trashed=false";
            listRequest.Fields = "files(id, name)";

            var folders = await ExecuteWithRetryAsync(async () => await listRequest.ExecuteAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

            if (folders.Files.Count == 0)
            {
                throw new DirectoryNotFoundException($"Folder {folderName} not found in path {currentPath}");
            }

            currentParentId = folders.Files[0].Id;

            CacheFolderId(currentPath, currentParentId);
        }

        return currentParentId;
    }

    private static string EscapeDriveQueryValue(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }

    private async Task<string> EnsureDirectoryPathExistsAsync(string remotePath, CancellationToken ct)
    {
        var pathParts = Path.GetDirectoryName(remotePath)?.Split('/', '\\').Where(p => !string.IsNullOrEmpty(p)).ToArray() ?? [];
        if (pathParts.Length == 0)
        {
            return _backupFolderId;
        }

        string currentParentId = _backupFolderId;
        string currentPath = "";

        await _folderCreateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var folderName in pathParts)
            {
                currentPath = string.IsNullOrEmpty(currentPath) ? folderName : currentPath + "/" + folderName;

                try
                {
                    currentParentId = await GetFolderIdByPathAsync(currentPath, ct).ConfigureAwait(false);
                }
                catch (DirectoryNotFoundException)
                {
                    var folderMetadata = new Google.Apis.Drive.v3.Data.File
                    {
                        Name = folderName,
                        MimeType = "application/vnd.google-apps.folder",
                        Parents = [currentParentId]
                    };

                    var request = _driveService!.Files.Create(folderMetadata);
                    request.Fields = "id";

                    var folder = await ExecuteWithRetryAsync(async () => await request.ExecuteAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    currentParentId = folder.Id;

                    CacheFolderId(currentPath, currentParentId);
                }
            }
        }
        finally
        {
            _folderCreateLock.Release();
        }

        return currentParentId;
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                attempt++;
                ct.ThrowIfCancellationRequested();
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Retrying a permanent error (bad credentials, missing file, revoked access)
                // just delays the inevitable failure by the full backoff budget.
                if (attempt >= MAX_RETRY_ATTEMPTS || !IsTransient(ex))
                {
                    if (attempt > 1)
                    {
                        Logger.Log($"Operation failed after {attempt} attempt(s): {ex.Message}", LogLevel.Error);
                    }

                    throw;
                }

                var delay = ComputeRetryDelay(attempt, Random.Shared.NextDouble());
                Logger.Log($"Attempt {attempt} failed: {ex.Message}. Retrying in {delay.TotalSeconds:0.#} seconds...", LogLevel.Warning);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Transient means "the same call could plausibly succeed shortly": rate limits, server
    /// faults, timeouts and transport errors. Auth and not-found failures are not.
    /// </summary>
    internal static bool IsTransient(Exception exception)
    {
        return exception switch
        {
            Google.GoogleApiException apiException => apiException.HttpStatusCode is
                System.Net.HttpStatusCode.RequestTimeout
                or System.Net.HttpStatusCode.TooManyRequests
                or System.Net.HttpStatusCode.InternalServerError
                or System.Net.HttpStatusCode.BadGateway
                or System.Net.HttpStatusCode.ServiceUnavailable
                or System.Net.HttpStatusCode.GatewayTimeout
                || IsRateLimitForbidden(apiException),
            // An HttpClient timeout surfaces as TaskCanceledException wrapping a TimeoutException; any other
            // cancellation is the caller (or shutdown) cancelling and must never be retried.
            OperationCanceledException cancelled => cancelled.InnerException is TimeoutException,
            HttpRequestException => true,
            TimeoutException => true,
            // Local disk problems are permanent for this call; only network-ish IO is worth retrying.
            FileNotFoundException or DirectoryNotFoundException or PathTooLongException
                or DriveNotFoundException or UnauthorizedAccessException => false,
            IOException => true,
            _ => false
        };
    }

    /// <summary>The Google client reports failed downloads via status, not by throwing.</summary>
    internal static void ThrowIfDownloadFailed(Google.Apis.Download.IDownloadProgress progress)
    {
        if (progress.Status != Google.Apis.Download.DownloadStatus.Completed)
        {
            throw progress.Exception ?? new IOException($"Google Drive download did not complete (status {progress.Status}).");
        }
    }

    /// <summary>The Google client reports failed uploads via status, not by throwing.</summary>
    internal static IUploadProgress ThrowIfUploadFailed(IUploadProgress progress)
    {
        if (progress.Status == UploadStatus.Failed)
        {
            throw progress.Exception ?? new IOException("Google Drive upload failed.");
        }

        return progress;
    }

    /// <summary>Drive reports quota throttling as 403 with a rate-limit reason rather than 429.</summary>
    private static bool IsRateLimitForbidden(Google.GoogleApiException exception)
    {
        if (exception.HttpStatusCode != System.Net.HttpStatusCode.Forbidden)
        {
            return false;
        }

        return exception.Error?.Errors?.Any(e =>
            string.Equals(e.Reason, "rateLimitExceeded", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Reason, "userRateLimitExceeded", StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>
    /// Exponential backoff (1s, 2s, 4s ... capped at 30s) with up to +/-25% jitter.
    /// <paramref name="jitter"/> is a uniform sample in [0,1).
    /// </summary>
    internal static TimeSpan ComputeRetryDelay(int attempt, double jitter)
    {
        var baseMs = Math.Min(1000 * Math.Pow(2, Math.Max(attempt, 1) - 1), 30000);
        var factor = 0.75 + (0.5 * Math.Clamp(jitter, 0, 1));
        return TimeSpan.FromMilliseconds(baseMs * factor);
    }

    private string GetMimeType(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".txt" => "text/plain",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _disposeCts.Cancel();
            _disposeCts.Dispose();
            _folderCreateLock.Dispose();
            _driveService?.Dispose();
            _driveService = null;
            Logger.Log("Disposed DriveService.", LogLevel.Debug);
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
