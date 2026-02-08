using Octokit;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.github;

public class GitHubStorage(ILogger logger) : StorageBase(logger)
{
    private GitHubClient? _client;
    private string _repoOwner = string.Empty;
    private string _repoName = string.Empty;
    private bool _disposed = false;
    private const long MAX_FILE_SIZE_BYTES = 100 * 1024 * 1024; // GitHub API limit: 100MB

    public override async Task InitializeAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try
        {
            _client = new GitHubClient(new ProductHeaderValue("ReStore"))
            {
                Credentials = new Credentials(options["token"])
            };

            _repoOwner = options["owner"];
            _repoName = options["repo"];

            // Verify repository access
            var repository = await _client.Repository.Get(_repoOwner, _repoName);
            Logger.Log($"Connected to GitHub repository: {repository.FullName}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize GitHub storage", ex);
        }
    }

    private void ValidateOptions(Dictionary<string, string> options)
    {
        var required = new[] { "token", "owner", "repo" };
        var missing = required.Where(key => !options.ContainsKey(key) || string.IsNullOrEmpty(options[key]));

        if (missing.Any())
        {
            throw new ArgumentException($"Missing GitHub configuration: {string.Join(", ", missing)}");
        }
    }

    public override async Task UploadAsync(string localPath, string remotePath)
    {
        var fileInfo = new FileInfo(localPath);
        if (fileInfo.Length > MAX_FILE_SIZE_BYTES)
        {
            throw new InvalidOperationException(
                $"File '{Path.GetFileName(localPath)}' is {fileInfo.Length / (1024 * 1024)}MB which exceeds GitHub's 100MB file size limit. " +
                "Consider using a different storage provider for large backups.");
        }

        var bytes = await File.ReadAllBytesAsync(localPath);
        var base64Content = Convert.ToBase64String(bytes);

        try
        {
            await _client!.Repository.Content.CreateFile(
                _repoOwner,
                _repoName,
                remotePath,
                new CreateFileRequest(
                    $"Create {remotePath}",
                    base64Content,
                    false
                )
            );
            Logger.Log($"Successfully uploaded {Path.GetFileName(localPath)} to GitHub", LogLevel.Info);
        }
        catch (ApiValidationException ex) when (ex.ApiError?.Message?.Contains("already exists") == true)
        {
            Logger.Log($"File {remotePath} already exists, updating...", LogLevel.Info);

            var existingFile = await _client!.Repository.Content.GetAllContentsByRef(_repoOwner, _repoName, remotePath);
            await _client.Repository.Content.UpdateFile(
                _repoOwner,
                _repoName,
                remotePath,
                new UpdateFileRequest(
                    $"Update {remotePath}",
                    base64Content,
                    existingFile[0].Sha,
                    false
                )
            );
            Logger.Log($"Successfully updated {Path.GetFileName(localPath)} on GitHub", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to upload {localPath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to upload {localPath} to GitHub", ex);
        }
    }

    public override async Task DownloadAsync(string remotePath, string localPath)
    {
        var content = await _client!.Repository.Content.GetRawContent(_repoOwner, _repoName, remotePath);
        await File.WriteAllBytesAsync(localPath, content);
    }

    public override async Task<bool> ExistsAsync(string remotePath)
    {
        try
        {
            await _client!.Repository.Content.GetRawContent(_repoOwner, _repoName, remotePath);
            return true;
        }
        catch (NotFoundException)
        {
            return false;
        }
    }

    public override async Task DeleteAsync(string remotePath)
    {
        var content = await _client!.Repository.Content.GetAllContentsByRef(_repoOwner, _repoName, remotePath);
        await _client.Repository.Content.DeleteFile(
            _repoOwner,
            _repoName,
            remotePath,
            new DeleteFileRequest($"Delete {remotePath}", content[0].Sha)
        );
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _client = null;
            Logger.Log("Disposed GitHubStorage resources.", LogLevel.Debug);
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
