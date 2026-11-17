using ReStore.Core.src.storage;

namespace ReStore.Tests;

/// <summary>Forwards every <see cref="IStorage"/> member to an inner storage; test wrappers override only what they observe.</summary>
internal abstract class DelegatingStorage(IStorage inner) : IStorage
{
    protected IStorage Inner { get; } = inner;

    public virtual Task InitializeAsync(Dictionary<string, string> options) => Inner.InitializeAsync(options);
    public virtual Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default) => Inner.UploadAsync(localPath, remotePath, ct);
    public virtual Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default) => Inner.UploadAsync(content, remotePath, ct);
    public virtual Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default) => Inner.DownloadAsync(remotePath, localPath, ct);
    public virtual Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default) => Inner.ExistsAsync(remotePath, ct);
    public virtual Task<long?> GetSizeAsync(string remotePath, CancellationToken ct = default) => Inner.GetSizeAsync(remotePath, ct);
    public virtual Task DeleteAsync(string remotePath, CancellationToken ct = default) => Inner.DeleteAsync(remotePath, ct);
    public virtual IAsyncEnumerable<StorageEntry> ListAsync(string prefix, CancellationToken ct = default) => Inner.ListAsync(prefix, ct);
    public virtual bool SupportsListing => Inner.SupportsListing;
    public virtual int MaxConcurrency => Inner.MaxConcurrency;
    public virtual Task<string> GenerateShareLinkAsync(string remotePath, TimeSpan expiration) => Inner.GenerateShareLinkAsync(remotePath, expiration);
    public virtual bool SupportsSharing => Inner.SupportsSharing;
    public virtual void Dispose() { }
}
