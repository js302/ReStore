using System.Runtime.CompilerServices;
using Octokit;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.github;

public class GitHubStorage(ILogger logger) : StorageBase(logger)
{
    private GitHubClient? _client;
    private string _repoOwner = string.Empty;
    private string _repoName = string.Empty;
    private bool _disposed = false;
    private string _defaultBranch = string.Empty;
    private const int MAX_RATE_LIMIT_RETRIES = 3;
    private static readonly TimeSpan MAX_RATE_LIMIT_WAIT = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DEFAULT_SECONDARY_WAIT = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TREE_CACHE_TTL = TimeSpan.FromSeconds(60);
    private readonly object _treeLock = new();
    private Dictionary<string, TreeEntry>? _treeCache;

    private readonly HttpMessageHandler? _handler;

    /// <summary>Test seam: routes every GitHub API call through <paramref name="handler"/> (a fake server).</summary>
    internal GitHubStorage(ILogger logger, HttpMessageHandler handler) : this(logger)
    {
        _handler = handler;
    }

    private GitHubClient CreateClient(string token)
    {
        var product = new ProductHeaderValue("ReStore");
        var credentials = new Credentials(token);
        if (_handler == null)
        {
            return new GitHubClient(product) { Credentials = credentials };
        }

        return new GitHubClient(new Connection(
            product,
            GitHubClient.GitHubApiUrl,
            new Octokit.Internal.InMemoryCredentialStore(credentials),
            new Octokit.Internal.HttpClientAdapter(() => _handler),
            new Octokit.Internal.SimpleJsonSerializer()));
    }

    internal enum TreeAvailability { Available, Empty, Truncated }

    /// <summary>Decides how listing proceeds: no tree = empty repository, truncated tree = use the contents API.</summary>
    internal static TreeAvailability Classify(TreeResponse? response) =>
        response == null ? TreeAvailability.Empty : response.Truncated ? TreeAvailability.Truncated : TreeAvailability.Available;

    private volatile bool _treeTruncated;

    private readonly record struct TreeEntry(string Sha, long Size);
    private DateTime _treeCachedAt;
    private const long MAX_FILE_SIZE_BYTES = 100 * 1024 * 1024; // GitHub API limit: 100MB
    private const long LARGE_FILE_WARNING_BYTES = 50 * 1024 * 1024;

    protected override async Task InitializeCoreAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try
        {
            _client = CreateClient(options["token"]);

            _repoOwner = options["owner"];
            _repoName = options["repo"];

            // Verify repository access
            var repository = await ExecuteWithRateLimitAsync(() => _client.Repository.Get(_repoOwner, _repoName), CancellationToken.None).ConfigureAwait(false);
            _defaultBranch = repository.DefaultBranch;
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

    protected override async Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct)
    {
        var fileInfo = new FileInfo(localPath);
        if (fileInfo.Length > MAX_FILE_SIZE_BYTES)
        {
            throw new InvalidOperationException(
                $"File '{Path.GetFileName(localPath)}' is {fileInfo.Length / (1024 * 1024)}MB which exceeds GitHub's 100MB file size limit. " +
                "Consider using a different storage provider for large backups.");
        }

        if (fileInfo.Length >= LARGE_FILE_WARNING_BYTES)
        {
            Logger.Log(
                $"Uploading {Path.GetFileName(localPath)} through GitHub storage requires buffering and base64 encoding a {fileInfo.Length / (1024 * 1024)}MB file. Consider object storage for large backups.",
                LogLevel.Warning);
        }

        var bytes = await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(false);
        var base64Content = Convert.ToBase64String(bytes);

        try
        {
            var existingSha = await GetExistingShaAsync(remotePath, ct).ConfigureAwait(false);
            if (existingSha == null)
            {
                var created = await ExecuteWithRateLimitAsync(() => _client!.Repository.Content.CreateFile(
                    _repoOwner,
                    _repoName,
                    remotePath,
                    new CreateFileRequest($"Create {remotePath}", base64Content, false)), ct).ConfigureAwait(false);
                UpdateTreeCache(remotePath, created.Content?.Sha, bytes.Length);
                Logger.Log($"Successfully uploaded {Path.GetFileName(localPath)} to GitHub", LogLevel.Debug);
            }
            else
            {
                var updated = await ExecuteWithRateLimitAsync(() => _client!.Repository.Content.UpdateFile(
                    _repoOwner,
                    _repoName,
                    remotePath,
                    new UpdateFileRequest($"Update {remotePath}", base64Content, existingSha, false)), ct).ConfigureAwait(false);
                UpdateTreeCache(remotePath, updated.Content?.Sha, bytes.Length);
                Logger.Log($"Successfully updated {Path.GetFileName(localPath)} on GitHub", LogLevel.Debug);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to upload {localPath}: {ex.Message}", LogLevel.Error);
            throw new InvalidOperationException($"Failed to upload {localPath} to GitHub", ex);
        }
    }

    protected override async Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken ct)
    {
        byte[] content;
        try
        {
            content = await ExecuteWithRateLimitAsync(() => _client!.Repository.Content.GetRawContent(_repoOwner, _repoName, remotePath), ct).ConfigureAwait(false);
        }
        catch (NotFoundException ex)
        {
            throw new StorageNotFoundException($"File not found in GitHub repository: {remotePath}", ex) { RemotePath = remotePath };
        }

        await DownloadAtomicAsync(localPath, partPath => File.WriteAllBytesAsync(partPath, content, ct)).ConfigureAwait(false);
    }

    protected override async Task<bool> ExistsCoreAsync(string remotePath, CancellationToken ct)
    {
        var normalized = NormalizeRepoPath(remotePath);
        var tree = await GetTreeAsync(ct).ConfigureAwait(false);
        if (tree != null)
        {
            lock (_treeLock)
            {
                return tree.ContainsKey(normalized);
            }
        }

        // Tree unavailable (truncated or empty repository): metadata lookup, which carries a sha but no download.
        return await GetExistingShaAsync(remotePath, ct).ConfigureAwait(false) != null;
    }

    protected override async Task<long?> GetSizeCoreAsync(string remotePath, CancellationToken ct)
    {
        var normalized = NormalizeRepoPath(remotePath);
        var tree = await GetTreeAsync(ct).ConfigureAwait(false);
        if (tree != null)
        {
            lock (_treeLock)
            {
                return tree.TryGetValue(normalized, out var entry) ? entry.Size : null;
            }
        }

        try
        {
            var contents = await ExecuteWithRateLimitAsync(() =>
                _client!.Repository.Content.GetAllContentsByRef(_repoOwner, _repoName, remotePath, _defaultBranch), ct).ConfigureAwait(false);
            return contents
                .FirstOrDefault(c => c.Type == ContentType.File &&
                    string.Equals(c.Path, normalized, StringComparison.Ordinal))?.Size;
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    protected override async IAsyncEnumerable<StorageEntry> ListCoreAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var normalized = (prefix ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var tree = await GetTreeAsync(ct, forceRefresh: true).ConfigureAwait(false);
        if (tree == null)
        {
            if (!_treeTruncated)
            {
                yield break; // empty repository: nothing to list
            }

            // Truncated tree: walk the directories through the contents API instead.
            var pending = new Stack<string>();
            pending.Push(normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : string.Empty);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                IReadOnlyList<RepositoryContent> children;
                try
                {
                    children = dir.Length == 0
                        ? await ExecuteWithRateLimitAsync(() => _client!.Repository.Content.GetAllContentsByRef(_repoOwner, _repoName, _defaultBranch), ct).ConfigureAwait(false)
                        : await ExecuteWithRateLimitAsync(() => _client!.Repository.Content.GetAllContentsByRef(_repoOwner, _repoName, dir, _defaultBranch), ct).ConfigureAwait(false);
                }
                catch (NotFoundException)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (child.Type == ContentType.Dir)
                    {
                        pending.Push(child.Path);
                    }
                    else if (child.Type == ContentType.File && child.Path.StartsWith(normalized, StringComparison.Ordinal))
                    {
                        yield return new StorageEntry(child.Path, child.Size);
                    }
                }
            }

            yield break;
        }

        List<StorageEntry> matches;
        lock (_treeLock)
        {
            matches = tree
                .Where(kv => kv.Key.StartsWith(normalized, StringComparison.Ordinal))
                .Select(kv => new StorageEntry(kv.Key, kv.Value.Size))
                .ToList();
        }

        foreach (var match in matches)
        {
            ct.ThrowIfCancellationRequested();
            yield return match;
        }
    }

    protected override async Task DeleteCoreAsync(string remotePath, CancellationToken ct)
    {
        var sha = await GetExistingShaAsync(remotePath, ct).ConfigureAwait(false);
        if (sha == null)
        {
            return;
        }

        try
        {
            await ExecuteWithRateLimitAsync(() => _client!.Repository.Content.DeleteFile(
                _repoOwner,
                _repoName,
                remotePath,
                new DeleteFileRequest($"Delete {remotePath}", sha)), ct).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            // Removed concurrently: delete of a missing path is a no-op.
        }

        UpdateTreeCache(remotePath, null, 0);
    }

    private static string NormalizeRepoPath(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>Returns the blob sha of the file at <paramref name="remotePath"/>, or null if it does not exist.</summary>
    private async Task<string?> GetExistingShaAsync(string remotePath, CancellationToken ct)
    {
        var normalized = NormalizeRepoPath(remotePath);

        // Prefer the git tree: it carries blob shas for every size, whereas the contents API refuses
        // (403 too_large) or omits content for files over 1 MB.
        var tree = await GetTreeAsync(ct).ConfigureAwait(false);
        if (tree != null)
        {
            lock (_treeLock)
            {
                return tree.TryGetValue(normalized, out var treeEntry) ? treeEntry.Sha : null;
            }
        }

        try
        {
            var contents = await ExecuteWithRateLimitAsync(() =>
                _client!.Repository.Content.GetAllContentsByRef(_repoOwner, _repoName, remotePath, _defaultBranch), ct).ConfigureAwait(false);
            return contents
                .FirstOrDefault(c => c.Type == ContentType.File &&
                    string.Equals(c.Path, normalized, StringComparison.Ordinal))?.Sha;
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cached set of file paths from the recursive git tree, so existence checks do not fetch file content.
    /// Returns null when the tree cannot be trusted (truncated response or empty repository).
    /// </summary>
    private async Task<Dictionary<string, TreeEntry>?> GetTreeAsync(CancellationToken ct, bool forceRefresh = false)
    {
        lock (_treeLock)
        {
            if (!forceRefresh && _treeCache != null && DateTime.UtcNow - _treeCachedAt < TREE_CACHE_TTL)
            {
                return _treeCache;
            }
        }

        try
        {
            var response = await ExecuteWithRateLimitAsync(() =>
                _client!.Git.Tree.GetRecursive(_repoOwner, _repoName, _defaultBranch), ct).ConfigureAwait(false);
            _treeTruncated = Classify(response) == TreeAvailability.Truncated;
            if (_treeTruncated)
            {
                return null;
            }

            var paths = response.Tree
                .Where(t => t.Type == TreeType.Blob)
                .ToDictionary(t => t.Path, t => new TreeEntry(t.Sha, t.Size), StringComparer.Ordinal);
            lock (_treeLock)
            {
                _treeCache = paths;
                _treeCachedAt = DateTime.UtcNow;
            }

            return paths;
        }
        catch (NotFoundException)
        {
            _treeTruncated = false;
            return null;
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // GitHub answers 409 "Git Repository is empty".
            _treeTruncated = false;
            return null;
        }
    }

    private void UpdateTreeCache(string remotePath, string? sha, long size)
    {
        var normalized = NormalizeRepoPath(remotePath);
        lock (_treeLock)
        {
            if (_treeCache == null)
            {
                return;
            }

            if (sha != null)
            {
                _treeCache[normalized] = new TreeEntry(sha, size);
            }
            else
            {
                _treeCache.Remove(normalized);
            }
        }
    }

    // Octokit has no cancellation support: WaitAsync(ct) abandons the in-flight request on cancellation.
    private Task ExecuteWithRateLimitAsync(Func<Task> operation, CancellationToken ct) =>
        ExecuteWithRateLimitAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }, ct);

    private async Task<T> ExecuteWithRateLimitAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MAX_RATE_LIMIT_RETRIES && GetRateLimitDelay(ex) is not null)
            {
                var delay = GetRateLimitDelay(ex)!.Value;
                Logger.Log($"GitHub rate limit hit; waiting {delay.TotalSeconds:0} seconds (retry {attempt + 1}/{MAX_RATE_LIMIT_RETRIES}).", LogLevel.Warning);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// If <paramref name="exception"/> is a (primary or secondary) rate-limit error that is worth waiting for,
    /// returns how long to wait; otherwise null. Waits longer than five minutes are not attempted.
    /// </summary>
    internal static TimeSpan? GetRateLimitDelay(Exception exception)
    {
        TimeSpan? delay = exception switch
        {
            RateLimitExceededException primary => ClampDelay(primary.GetRetryAfterTimeSpan()),
            SecondaryRateLimitExceededException secondary => ClampDelay(
                TryGetRetryAfterHeader(secondary) ?? DEFAULT_SECONDARY_WAIT),
            _ => null
        };

        if (delay is { } d && d > MAX_RATE_LIMIT_WAIT)
        {
            return null;
        }

        return delay;
    }

    private static TimeSpan ClampDelay(TimeSpan delay)
    {
        // Pad by a second: the reset time is second-granular. Never return a zero/negative wait.
        return (delay <= TimeSpan.Zero ? TimeSpan.Zero : delay) + TimeSpan.FromSeconds(1);
    }

    private static TimeSpan? TryGetRetryAfterHeader(ApiException exception)
    {
        var headers = exception.HttpResponse?.Headers;
        if (headers != null &&
            headers.TryGetValue("Retry-After", out var value) &&
            int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
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
