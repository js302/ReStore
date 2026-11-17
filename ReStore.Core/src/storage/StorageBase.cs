using ReStore.Core.src.utils;
using ReStore.Core.src.storage.aws;
using ReStore.Core.src.storage.github;
using ReStore.Core.src.storage.google;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.storage.azure;
using ReStore.Core.src.storage.dropbox;
using ReStore.Core.src.storage.sftp;
using ReStore.Core.src.storage.backblaze;

namespace ReStore.Core.src.storage;

/// <summary>One remote object returned by <see cref="IStorage.ListAsync"/>.</summary>
/// <param name="Path">Logical remote path (relative to the provider's prefix/root), using '/' separators.</param>
/// <param name="Size">Object size in bytes.</param>
/// <param name="LastModifiedUtc">Last modification time when the provider reports it.</param>
public sealed record StorageEntry(string Path, long Size, DateTime? LastModifiedUtc = null);

/// <summary>
/// Remote storage provider contract. Behavioural contract (pinned by the storage conformance tests):
/// <list type="bullet">
/// <item>Download of a missing path throws <see cref="StorageNotFoundException"/> (a <see cref="FileNotFoundException"/>).</item>
/// <item>Delete of a missing path is a no-op; Exists/GetSize of a missing path return false/null and never throw.</item>
/// <item>Uploads are atomic; downloads never leave a partial file at the destination.</item>
/// <item>The optional <c>prefix</c> option is applied to every path by <see cref="StorageBase"/>.</item>
/// <item>All operations honour the cancellation token.</item>
/// </list>
/// </summary>
public interface IStorage : IDisposable
{
    Task InitializeAsync(Dictionary<string, string> options);
    Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default);
    /// <summary>Uploads the stream from its current position to the end.</summary>
    Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default);
    Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default);
    Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default);
    /// <summary>Size in bytes, or null when the object does not exist.</summary>
    Task<long?> GetSizeAsync(string remotePath, CancellationToken ct = default);
    Task DeleteAsync(string remotePath, CancellationToken ct = default);
    /// <summary>
    /// Lists objects whose logical path starts with <paramref name="prefix"/> (recursively), paging lazily.
    /// Throws <see cref="NotSupportedException"/> when <see cref="SupportsListing"/> is false.
    /// </summary>
    IAsyncEnumerable<StorageEntry> ListAsync(string prefix, CancellationToken ct = default);
    bool SupportsListing { get; }
    /// <summary>Maximum number of operations that may safely run concurrently (1 = not thread-safe).</summary>
    int MaxConcurrency { get; }
    Task<string> GenerateShareLinkAsync(string remotePath, TimeSpan expiration);
    bool SupportsSharing { get; }
}

/// <summary>Optional capability: an upload that fails (returns false) instead of replacing an existing object, atomically.</summary>
public interface ICreateIfAbsentStorage
{
    /// <returns>True when the object was created, false when one already existed (nothing is changed).</returns>
    Task<bool> TryCreateAsync(string localPath, string remotePath, CancellationToken ct = default);
}

public abstract class StorageBase(ILogger logger) : IStorage
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LegacyPathWarned = new();

    /// <summary>Lets tests observe the once-per-process legacy <c>path</c> warning.</summary>
    internal static void ResetLegacyPathWarningsForTests() => LegacyPathWarned.Clear();

    protected readonly ILogger Logger = logger;
    private bool _disposed = false;

    /// <summary>Normalised <c>prefix</c> option: empty, or segments joined by '/' with a trailing '/'.</summary>
    protected string Prefix { get; private set; } = string.Empty;

    /// <summary>True for remote providers, where the legacy <c>path</c> option is ignored (use <c>prefix</c>).</summary>
    protected virtual bool WarnsAboutLegacyPathOption => true;

    public virtual bool SupportsListing => true;
    public virtual int MaxConcurrency => 1;

    public async Task InitializeAsync(Dictionary<string, string> options)
    {
        Prefix = NormalizePrefix(options.TryGetValue("prefix", out var prefix) ? prefix : null);

        if (WarnsAboutLegacyPathOption
            && options.TryGetValue("path", out var legacy) && !string.IsNullOrWhiteSpace(legacy)
            && LegacyPathWarned.TryAdd(GetType().Name, 0))
        {
            Logger.Log($"{GetType().Name}: the 'path' option is ignored by remote providers. Use the 'prefix' option to place the repository under a sub-folder.", LogLevel.Warning);
        }

        await InitializeCoreAsync(options).ConfigureAwait(false);
    }

    /// <summary>Normalises and validates a prefix; throws <see cref="ArgumentException"/> when invalid.</summary>
    public static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        var value = prefix.Trim().Replace('\\', '/');
        if (value.StartsWith('/'))
        {
            throw new ArgumentException("The 'prefix' option must not start with '/'.");
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".." || segment == "."))
        {
            throw new ArgumentException("The 'prefix' option must not contain '.' or '..' segments.");
        }

        return segments.Length == 0 ? string.Empty : string.Join('/', segments) + "/";
    }

    /// <summary>Applies the prefix to a logical path, producing the provider's real key.</summary>
    protected string Qualify(string path) => Prefix.Length == 0 ? path : Prefix + path.TrimStart('/');

    protected abstract Task InitializeCoreAsync(Dictionary<string, string> options);
    protected abstract Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct);
    protected abstract Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken ct);
    protected abstract Task<bool> ExistsCoreAsync(string remotePath, CancellationToken ct);
    protected abstract Task<long?> GetSizeCoreAsync(string remotePath, CancellationToken ct);
    protected abstract Task DeleteCoreAsync(string remotePath, CancellationToken ct);

    /// <summary>Lists real keys that start with <paramref name="prefix"/> (already qualified).</summary>
    protected virtual IAsyncEnumerable<StorageEntry> ListCoreAsync(string prefix, CancellationToken ct) =>
        throw new NotSupportedException("Listing is not supported by this storage provider.");

    public Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default) =>
        UploadCoreAsync(localPath, Qualify(remotePath), ct);

    public virtual async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var temp = Path.Combine(Path.GetTempPath(), "restore-upload-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                await content.CopyToAsync(file, 81920, ct).ConfigureAwait(false);
            }

            await UploadAsync(temp, remotePath, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default) =>
        DownloadCoreAsync(Qualify(remotePath), localPath, ct);

    public Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default) =>
        ExistsCoreAsync(Qualify(remotePath), ct);

    public Task<long?> GetSizeAsync(string remotePath, CancellationToken ct = default) =>
        GetSizeCoreAsync(Qualify(remotePath), ct);

    public Task DeleteAsync(string remotePath, CancellationToken ct = default) =>
        DeleteCoreAsync(Qualify(remotePath), ct);

    public async IAsyncEnumerable<StorageEntry> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!SupportsListing)
        {
            throw new NotSupportedException("Listing is not supported by this storage provider.");
        }

        await foreach (var entry in ListCoreAsync(Qualify(prefix ?? string.Empty), ct).WithCancellation(ct).ConfigureAwait(false))
        {
            if (Prefix.Length == 0)
            {
                yield return entry;
            }
            else if (entry.Path.StartsWith(Prefix, StringComparison.Ordinal))
            {
                yield return entry with { Path = entry.Path[Prefix.Length..] };
            }
        }
    }

    public Task<string> GenerateShareLinkAsync(string remotePath, TimeSpan expiration) =>
        GenerateShareLinkCoreAsync(Qualify(remotePath), expiration);

    protected virtual Task<string> GenerateShareLinkCoreAsync(string remotePath, TimeSpan expiration)
    {
        throw new NotSupportedException("Sharing is not supported by this storage provider.");
    }

    public virtual bool SupportsSharing => false;

    /// <summary>
    /// Downloads into <c>&lt;localPath&gt;.part</c> and moves it into place only on success, so a failed
    /// or cancelled download never leaves a truncated file at <paramref name="localPath"/>. The callback
    /// must close any stream it opens on the path it is given before returning.
    /// </summary>
    protected static async Task DownloadAtomicAsync(string localPath, Func<string, Task> downloadToPartFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(localPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var partPath = localPath + ".part";
        try
        {
            await downloadToPartFile(partPath).ConfigureAwait(false);
            File.Move(partPath, localPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(partPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Logger.Log($"Disposing {GetType().Name} resources.", LogLevel.Debug);
        }

        _disposed = true;
    }
}

public class StorageFactory
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, Func<ILogger, IStorage>> _storageCreators;

    public StorageFactory(ILogger logger)
    {
        _logger = logger;
        _storageCreators = new(StringComparer.OrdinalIgnoreCase)
        {
            ["s3"] = logger => new S3Storage(logger),
            ["github"] = logger => new GitHubStorage(logger),
            ["gdrive"] = logger => new DriveStorage(logger),
            ["local"] = logger => new LocalStorage(logger),
            ["azure"] = logger => new AzureStorage(logger),
            ["gcp"] = logger => new GcpStorage(logger),
            ["dropbox"] = logger => new DropboxStorage(logger),
            ["sftp"] = logger => new SftpStorage(logger),
            ["b2"] = logger => new B2Storage(logger)
        };
    }

    /// <summary>Registers (or replaces) a provider creator; used by tests to inject fakes.</summary>
    internal void RegisterForTests(string storageType, Func<ILogger, IStorage> creator) =>
        _storageCreators[storageType] = creator;

    public async Task<IStorage> CreateStorageAsync(string storageType, StorageConfig config)
    {
        if (!_storageCreators.TryGetValue(storageType, out var creator))
        {
            throw new ArgumentException($"Unsupported storage type: {storageType}");
        }

        var storage = creator(_logger);
        try
        {
            await storage.InitializeAsync(config.Options).ConfigureAwait(false);
        }
        catch
        {
            storage.Dispose();
            throw;
        }

        return storage;
    }
}
