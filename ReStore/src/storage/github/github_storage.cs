using Octokit;
using ReStore.src.utils;

namespace ReStore.src.storage.github;

public class GitHubStorage(ILogger logger) : StorageBase(logger)
{
    private GitHubClient? _client;
    private string _repoOwner = string.Empty;
    private string _repoName = string.Empty;

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
        // Note: This is a simplified implementation
        // A full implementation would need to:
        // 1. Use Git LFS for large files
        // 2. Handle commits and branches
        // 3. Deal with rate limits
        var bytes = await File.ReadAllBytesAsync(localPath);
        var base64Content = Convert.ToBase64String(bytes);

        try
        {
            await _client!.Repository.Content.CreateFile(
                _repoOwner,
                _repoName,
                remotePath,
                new CreateFileRequest(
                    $"Update {remotePath}",
                    base64Content,
                    false
                )
            );
        }
        catch (NotFoundException)
        {
            Logger.Log($"Creating new file: {remotePath}");
            throw;
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
}
