using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// In-memory <see cref="IStorage"/> for tests. Several instances may share one <see cref="Store"/>
/// (with different <c>prefix</c> options) to model one remote repository seen through different prefixes.
/// Listing is served in small pages so paging code paths are exercised.
/// </summary>
internal class InMemoryStorage(ILogger logger, ConcurrentDictionary<string, byte[]>? store = null) : StorageBase(logger)
{
    public const int PageSize = 2;

    public ConcurrentDictionary<string, byte[]> Store { get; } = store ?? new(StringComparer.Ordinal);

    /// <summary>Last-modified times as stamped on upload; tests age objects with <see cref="SetLastModified"/>.</summary>
    public ConcurrentDictionary<string, DateTime> Modified { get; } = new(StringComparer.Ordinal);

    /// <summary>Test hook: backdates (or sets) the real key's modification time.</summary>
    public void SetLastModified(string realKey, DateTime utc) => Modified[realKey] = utc;

    public override int MaxConcurrency => 8;

    protected override Task InitializeCoreAsync(Dictionary<string, string> options) => Task.CompletedTask;

    protected override async Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Store[remotePath] = await File.ReadAllBytesAsync(localPath, ct);
        Modified[remotePath] = DateTime.UtcNow;
    }

    public override async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        Store[Qualify(remotePath)] = buffer.ToArray();
        Modified[Qualify(remotePath)] = DateTime.UtcNow;
    }

    protected override Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Store.TryGetValue(remotePath, out var bytes))
        {
            throw new StorageNotFoundException($"Not found in memory storage: {remotePath}");
        }

        return DownloadAtomicAsync(localPath, partPath => File.WriteAllBytesAsync(partPath, bytes, ct));
    }

    protected override Task<bool> ExistsCoreAsync(string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Store.ContainsKey(remotePath));
    }

    protected override Task<long?> GetSizeCoreAsync(string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Store.TryGetValue(remotePath, out var bytes) ? (long?)bytes.Length : null);
    }

    protected override Task DeleteCoreAsync(string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Store.TryRemove(remotePath, out _);
        return Task.CompletedTask;
    }

    protected override async IAsyncEnumerable<StorageEntry> ListCoreAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var keys = Store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        for (var i = 0; i < keys.Count; i++)
        {
            if (i % PageSize == 0)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (Store.TryGetValue(keys[i], out var bytes))
            {
                yield return new StorageEntry(keys[i], bytes.Length, Modified.TryGetValue(keys[i], out var modified) ? modified : null);
            }
        }
    }
}
