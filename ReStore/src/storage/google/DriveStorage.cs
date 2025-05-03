using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using ReStore.src.utils;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace ReStore.src.storage.google;

public class DriveStorage(ILogger logger) : StorageBase(logger)
{
    private DriveService? _driveService;
    private string _backupFolderId = string.Empty;
    private const string BACKUP_FOLDER_NAME = "ReStore Backups";
    private const int MAX_RETRY_ATTEMPTS = 3;
    private readonly Dictionary<string, string> _folderCache = new();

    public override async Task InitializeAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try
        {
            var clientSecrets = new ClientSecrets
            {
                ClientId = options["client_id"],
                ClientSecret = options["client_secret"]
            };

            // Use a custom token folder if specified
            string tokenFolder = options.TryGetValue("token_folder", out var folder)
                ? folder
                : "Drive.Storage.Token";

            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                clientSecrets,
                [DriveService.Scope.Drive], // Use full Drive scope to allow folder operations
                "user",
                CancellationToken.None,
                new FileDataStore(tokenFolder)
            );

            _driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "ReStore Backup"
            });

            // Use custom backup folder name if provided
            string backupFolderName = options.TryGetValue("backup_folder_name", out var customName)
                ? customName
                : BACKUP_FOLDER_NAME;

            _backupFolderId = await GetOrCreateBackupFolderAsync(backupFolderName);
            Logger.Log($"Initialized Google Drive storage with backup folder: {_backupFolderId}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to initialize Google Drive service: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException("Failed to initialize Google Drive service", ex);
        }
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
            listRequest.Q = $"name='{folderName}' and mimeType='application/vnd.google-apps.folder' and trashed=false";
            listRequest.Spaces = "drive";
            listRequest.Fields = "files(id, name)";

            var folders = await ExecuteWithRetryAsync(async () => await listRequest.ExecuteAsync());

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
            var folder = await ExecuteWithRetryAsync(async () => await request.ExecuteAsync());

            return folder.Id;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to create backup folder: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to create backup folder '{folderName}'", ex);
        }
    }

    public override async Task UploadAsync(string localPath, string remotePath)
    {
        try
        {
            // Create necessary folders in the path
            string parentId = await EnsureDirectoryPathExistsAsync(remotePath);

            var fileMetadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = Path.GetFileName(remotePath),
                Parents = [parentId]
            };

            using var stream = new FileStream(localPath, FileMode.Open);
            var request = _driveService!.Files.Create(fileMetadata, stream, GetMimeType(localPath));
            request.Fields = "id, name, size";

            // Setup progress tracking
            var progress = new Progress<IUploadProgress>(p =>
            {
                if (p.Status == UploadStatus.Uploading && p.BytesSent > 0)
                {
                    var percentComplete = (int)((double)p.BytesSent / stream.Length * 100);
                    Logger.Log($"Uploading {Path.GetFileName(localPath)}: {percentComplete}%", LogLevel.Debug);
                }
            });

            var result = await ExecuteWithRetryAsync(async () =>
            {
                return await request.UploadAsync(CancellationToken.None);
            });

            if (result.Status != UploadStatus.Completed)
            {
                throw new Exception($"Upload failed: {result.Status} - {result.Exception?.Message}");
            }

            Logger.Log($"Successfully uploaded {Path.GetFileName(localPath)} to Google Drive", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to upload {localPath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to upload {localPath} to Google Drive", ex);
        }
    }

    public override async Task DownloadAsync(string remotePath, string localPath)
    {
        try
        {
            // Create local directory if it doesn't exist
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            var fileId = await GetFileIdByPathAsync(remotePath);
            var request = _driveService!.Files.Get(fileId);

            using var stream = new FileStream(localPath, FileMode.Create);

            await ExecuteWithRetryAsync(async () =>
            {
                await request.DownloadAsync(stream);
                return true;
            });

            Logger.Log($"Successfully downloaded {Path.GetFileName(remotePath)} from Google Drive", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to download {remotePath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to download {remotePath} from Google Drive", ex);
        }
    }

    public override async Task<bool> ExistsAsync(string remotePath)
    {
        try
        {
            await GetFileIdByPathAsync(remotePath);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"Error checking if file exists: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    public override async Task DeleteAsync(string remotePath)
    {
        try
        {
            var fileId = await GetFileIdByPathAsync(remotePath);

            await ExecuteWithRetryAsync(async () =>
            {
                await _driveService!.Files.Delete(fileId).ExecuteAsync();
                return true;
            });

            Logger.Log($"Successfully deleted {remotePath} from Google Drive", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to delete {remotePath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to delete {remotePath} from Google Drive", ex);
        }
    }

    private async Task<string> GetFileIdByPathAsync(string remotePath)
    {
        var pathParts = remotePath.Split('/', '\\').Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (pathParts.Length == 0)
        {
            throw new ArgumentException("Invalid remote path");
        }

        // Get the file name (last part of the path)
        string fileName = pathParts[^1];

        // Get the parent folder ID by traversing the path
        string parentId = _backupFolderId;
        if (pathParts.Length > 1)
        {
            string dirPath = string.Join("/", pathParts.Take(pathParts.Length - 1));
            parentId = await GetFolderIdByPathAsync(dirPath);
        }

        // Search for the file in the parent folder
        var listRequest = _driveService!.Files.List();
        listRequest.Q = $"name='{fileName}' and '{parentId}' in parents and trashed=false";
        listRequest.Fields = "files(id, name)";

        var files = await ExecuteWithRetryAsync(async () => await listRequest.ExecuteAsync());

        if (files.Files.Count == 0)
        {
            throw new FileNotFoundException($"File {fileName} not found in Google Drive path {remotePath}");
        }

        return files.Files[0].Id;
    }

    private async Task<string> GetFolderIdByPathAsync(string folderPath)
    {
        // Check cache first
        if (_folderCache.TryGetValue(folderPath, out var cachedId))
        {
            return cachedId;
        }

        var pathParts = folderPath.Split('/', '\\').Where(p => !string.IsNullOrEmpty(p)).ToArray();
        string currentParentId = _backupFolderId;
        string currentPath = "";

        foreach (var folderName in pathParts)
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? folderName : currentPath + "/" + folderName;

            // Check cache again for partial path
            if (_folderCache.TryGetValue(currentPath, out var partialCachedId))
            {
                currentParentId = partialCachedId;
                continue;
            }

            var listRequest = _driveService!.Files.List();
            listRequest.Q = $"name='{folderName}' and '{currentParentId}' in parents and mimeType='application/vnd.google-apps.folder' and trashed=false";
            listRequest.Fields = "files(id, name)";

            var folders = await ExecuteWithRetryAsync(async () => await listRequest.ExecuteAsync());

            if (folders.Files.Count == 0)
            {
                throw new DirectoryNotFoundException($"Folder {folderName} not found in path {currentPath}");
            }

            currentParentId = folders.Files[0].Id;

            // Cache the folder ID
            _folderCache[currentPath] = currentParentId;
        }

        return currentParentId;
    }

    private async Task<string> EnsureDirectoryPathExistsAsync(string remotePath)
    {
        var pathParts = Path.GetDirectoryName(remotePath)?.Split('/', '\\').Where(p => !string.IsNullOrEmpty(p)).ToArray() ?? [];
        if (pathParts.Length == 0)
        {
            return _backupFolderId;
        }

        string currentParentId = _backupFolderId;
        string currentPath = "";

        foreach (var folderName in pathParts)
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? folderName : currentPath + "/" + folderName;

            // Check if folder exists
            try
            {
                currentParentId = await GetFolderIdByPathAsync(currentPath);
            }
            catch (DirectoryNotFoundException)
            {
                // Create folder if it doesn't exist
                var folderMetadata = new Google.Apis.Drive.v3.Data.File
                {
                    Name = folderName,
                    MimeType = "application/vnd.google-apps.folder",
                    Parents = [currentParentId]
                };

                var request = _driveService!.Files.Create(folderMetadata);
                request.Fields = "id";

                var folder = await ExecuteWithRetryAsync(async () => await request.ExecuteAsync());
                currentParentId = folder.Id;

                // Cache the new folder ID
                _folderCache[currentPath] = currentParentId;
            }
        }

        return currentParentId;
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                attempt++;
                return await operation();
            }
            catch (Exception ex)
            {
                if (attempt >= MAX_RETRY_ATTEMPTS)
                {
                    Logger.Log($"Operation failed after {MAX_RETRY_ATTEMPTS} attempts: {ex.Message}", LogLevel.Error);
                    throw;
                }

                int delayMs = Math.Min(1000 * (int)Math.Pow(2, attempt - 1), 30000);
                Logger.Log($"Attempt {attempt} failed: {ex.Message}. Retrying in {delayMs / 1000} seconds...", LogLevel.Warning);
                await Task.Delay(delayMs);
            }
        }
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
}
