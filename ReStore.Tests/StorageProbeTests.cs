using FluentAssertions;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;

namespace ReStore.Tests;

public class StorageProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreProbeTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static async Task<InMemoryStorage> NewMemoryAsync(string? prefix = null)
    {
        var storage = new InMemoryStorage(new TestLogger());
        await storage.InitializeAsync(prefix == null ? [] : new Dictionary<string, string> { ["prefix"] = prefix });
        return storage;
    }

    [Fact]
    public async Task RunAsync_PassesEveryStep_AndLeavesNothingBehind_InMemory()
    {
        using var storage = await NewMemoryAsync("backups");

        var result = await StorageProbe.RunAsync(storage);

        result.Success.Should().BeTrue(result.ToDisplayText());
        result.CleanedUp.Should().BeTrue();
        result.Steps.Select(s => s.Name).Should().Equal("upload", "size", "download", "list", "delete", "verify-deleted");
        result.Key.Should().StartWith("restore-probe/");
        storage.Store.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WorksAgainstLocalStorage()
    {
        using var storage = new LocalStorage(new TestLogger());
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _root });

        var result = await StorageProbe.RunAsync(storage);

        result.Success.Should().BeTrue(result.ToDisplayText());
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_SkipsListing_WhenUnsupported()
    {
        using var storage = new Probing(await NewMemoryAsync()) { ListingSupported = false };

        var result = await StorageProbe.RunAsync(storage);

        result.Success.Should().BeTrue();
        result.Steps.Single(s => s.Name == "list").Skipped.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ReportsUploadFailure_AndStillAttemptsCleanup()
    {
        using var storage = new Probing(await NewMemoryAsync()) { FailUpload = true };

        var result = await StorageProbe.RunAsync(storage);

        result.Success.Should().BeFalse();
        result.Steps.Single(s => s.Name == "upload").Error.Should().Contain("upload denied");
        result.Steps.Single(s => s.Name == "size").Passed.Should().BeFalse("nothing was uploaded");
        storage.DeleteCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunAsync_ReportsSizeMismatch()
    {
        using var storage = new Probing(await NewMemoryAsync()) { SizeOverride = 1 };

        var result = await StorageProbe.RunAsync(storage);

        result.Steps.Single(s => s.Name == "size").Error.Should().Contain("size mismatch");
        result.Steps.Single(s => s.Name == "download").Passed.Should().BeTrue();
        result.CleanedUp.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ReportsCorruptDownload()
    {
        using var storage = new Probing(await NewMemoryAsync()) { CorruptDownload = true };

        var result = await StorageProbe.RunAsync(storage);

        result.Steps.Single(s => s.Name == "download").Error.Should().Contain("differs");
        result.CleanedUp.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ReportsListingThatDoesNotSeeTheObject()
    {
        using var storage = new Probing(await NewMemoryAsync()) { HideFromListing = true };

        var result = await StorageProbe.RunAsync(storage);

        result.Steps.Single(s => s.Name == "list").Error.Should().Contain("listing");
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_ReportsDeleteFailure_RetriesCleanup_AndFlagsLeftover()
    {
        using var storage = new Probing(await NewMemoryAsync()) { FailDelete = true };

        var result = await StorageProbe.RunAsync(storage);

        result.Steps.Single(s => s.Name == "delete").Error.Should().Contain("delete denied");
        result.CleanedUp.Should().BeFalse();
        result.ToDisplayText().Should().Contain(result.Key);
        storage.DeleteCalls.Should().Be(2, "one probe delete plus one best-effort cleanup");
    }

    [Fact]
    public async Task RunAsync_ReportsObjectStillPresentAfterDelete()
    {
        using var storage = new Probing(await NewMemoryAsync()) { IgnoreDelete = true };

        var result = await StorageProbe.RunAsync(storage);

        result.Steps.Single(s => s.Name == "verify-deleted").Error.Should().Contain("still exists");
        result.CleanedUp.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_Cancellation_StillCleansUp()
    {
        using var cts = new CancellationTokenSource();
        using var storage = new Probing(await NewMemoryAsync()) { CancelAfterUpload = cts };

        var act = () => StorageProbe.RunAsync(storage, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        storage.DeleteCalls.Should().BeGreaterThan(0);
        storage.Store.Should().BeEmpty();
    }

    private sealed class Probing(InMemoryStorage inner) : DelegatingStorage(inner)
    {
        public bool FailUpload { get; init; }
        public bool FailDelete { get; init; }
        public bool IgnoreDelete { get; init; }
        public bool CorruptDownload { get; init; }
        public bool HideFromListing { get; init; }
        public bool ListingSupported { get; init; } = true;
        public long? SizeOverride { get; init; }
        public CancellationTokenSource? CancelAfterUpload { get; init; }
        public int DeleteCalls { get; private set; }
        public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Store => inner.Store;

        public override bool SupportsListing => ListingSupported && inner.SupportsListing;

        public override async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
        {
            if (FailUpload)
            {
                throw new IOException("upload denied");
            }

            await base.UploadAsync(content, remotePath, ct);
            CancelAfterUpload?.Cancel();
        }

        public override Task<long?> GetSizeAsync(string remotePath, CancellationToken ct = default) =>
            SizeOverride is { } size ? Task.FromResult<long?>(size) : base.GetSizeAsync(remotePath, ct);

        public override async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
        {
            await base.DownloadAsync(remotePath, localPath, ct);
            if (CorruptDownload)
            {
                await File.WriteAllTextAsync(localPath, "garbage", ct);
            }
        }

        public override IAsyncEnumerable<StorageEntry> ListAsync(string prefix, CancellationToken ct = default) =>
            HideFromListing ? Empty() : base.ListAsync(prefix, ct);

        private static async IAsyncEnumerable<StorageEntry> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }

        public override Task DeleteAsync(string remotePath, CancellationToken ct = default)
        {
            DeleteCalls++;
            if (FailDelete)
            {
                throw new IOException("delete denied");
            }

            return IgnoreDelete ? Task.CompletedTask : base.DeleteAsync(remotePath, ct);
        }
    }
}
