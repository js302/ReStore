using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using ReStore.Utils;

namespace ReStore.Storage.GoogleDrive;

public class DriveStorage: StorageBase
{
    private DriveService? _driveService;
    private string _backupFolderId = string.Empty;
    private const string BACKUP_FOLDER_NAME = "ReStore Backups";

    public DriveStorage(ILogger logger) : base(logger) { }

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

            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                clientSecrets,
                [DriveService.Scope.DriveFile],
                "user",
                CancellationToken.None,
                new FileDataStore("Drive.Storage.Token")
            );

            _driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "ReStore Backup"
            });

            _backupFolderId = await GetOrCreateBackupFolderAsync();
            Logger.Log($"Initialized Google Drive storage with backup folder: {_backupFolderId}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize Google Drive service", ex);
        }
    }

    private void ValidateOptions(Dictionary<string, string> options)
    {
        var required = new[] { "client_id", "client_secret", "refresh_token" };
        var missing = required.Where(key => !options.ContainsKey(key) || string.IsNullOrEmpty(options[key]));
        
        if (missing.Any())
        {
            throw new ArgumentException($"Missing Google Drive configuration: {string.Join(", ", missing)}");
        }
    }

    private async Task<string> GetOrCreateBackupFolderAsync()
    {
        var listRequest = _driveService!.Files.List();
        listRequest.Q = "name='ReStore Backups' and mimeType='application/vnd.google-apps.folder'";
        var folders = await listRequest.ExecuteAsync();

        if (folders.Files.Count > 0)
        {
            return folders.Files[0].Id;
        }

        var folderMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = BACKUP_FOLDER_NAME,
            MimeType = "application/vnd.google-apps.folder"
        };

        var request = _driveService.Files.Create(folderMetadata);
        var folder = await request.ExecuteAsync();
        return folder.Id;
    }

    public override async Task UploadAsync(string localPath, string remotePath)
    {
        var fileMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = Path.GetFileName(remotePath),
            Parents = [_backupFolderId]
        };

        using var stream = new FileStream(localPath, FileMode.Open);
        var request = _driveService!.Files.Create(fileMetadata, stream, "application/octet-stream");
        request.Fields = "id";

        var progress = await request.UploadAsync();
        if (progress.Status != UploadStatus.Completed)
        {
            throw new Exception($"Upload failed: {progress.Status}");
        }
    }

    public override async Task DownloadAsync(string remotePath, string localPath)
    {
        var fileId = await GetFileIdByNameAsync(remotePath);
        var request = _driveService!.Files.Get(fileId);

        using var stream = new FileStream(localPath, FileMode.Create);
        await request.DownloadAsync(stream);
    }

    public override async Task<bool> ExistsAsync(string remotePath)
    {
        try
        {
            await GetFileIdByNameAsync(remotePath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override async Task DeleteAsync(string remotePath)
    {
        var fileId = await GetFileIdByNameAsync(remotePath);
        await _driveService!.Files.Delete(fileId).ExecuteAsync();
    }

    private async Task<string> GetFileIdByNameAsync(string name)
    {
        var listRequest = _driveService!.Files.List();
        listRequest.Q = $"name='{Path.GetFileName(name)}' and '{_backupFolderId}' in parents";
        var files = await listRequest.ExecuteAsync();

        if (files.Files.Count == 0)
        {
            throw new FileNotFoundException($"File {name} not found in Google Drive");
        }

        return files.Files[0].Id;
    }
}
