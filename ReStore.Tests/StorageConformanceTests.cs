using System.Text.Json;
using FluentAssertions;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Behavioural contract every <see cref="IStorage"/> provider must satisfy (see the interface docs).
/// Derive and implement <see cref="CreateStorageAsync"/>; return null when the provider is not configured
/// (cloud providers without credentials) and the tests pass vacuously.
/// All keys live under a per-test unique folder so tests can run against a shared real bucket.
/// </summary>
public abstract class StorageConformanceTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "ReStoreConformance_" + Guid.NewGuid().ToString("N"));
    protected readonly ILogger Logger = new TestLogger();

    /// <summary>Unique remote folder for this test run.</summary>
    protected string Root { get; } = "conf-" + Guid.NewGuid().ToString("N")[..12];

    protected StorageConformanceTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    /// <summary>
    /// Creates and initialises a storage over the same backing repository on every call, applying the
    /// given <c>prefix</c> option ("" = none). Returns null when the provider is unavailable.
    /// </summary>
    protected abstract Task<IStorage?> CreateStorageAsync(string prefix);

    public virtual void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_scratch, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private async Task<IStorage?> OpenAsync(string prefix = "")
    {
        var storage = await CreateStorageAsync(prefix);
        if (storage != null) _disposables.Add(storage);
        return storage;
    }

    private string Scratch(string name) => Path.Combine(_scratch, name);

    private async Task<string> WriteLocalAsync(string name, byte[] content)
    {
        var path = Scratch(name);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static async Task<List<StorageEntry>> ToListAsync(IAsyncEnumerable<StorageEntry> source)
    {
        var list = new List<StorageEntry>();
        await foreach (var e in source) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Upload_Download_RoundTrips_AndOverwrites()
    {
        if (await OpenAsync() is not { } storage) return;
        var key = $"{Root}/a/b/file.bin";

        await storage.UploadAsync(await WriteLocalAsync("one.bin", Bytes("first")), key);
        await storage.UploadAsync(await WriteLocalAsync("two.bin", Bytes("second!")), key);
        await storage.DownloadAsync(key, Scratch("out/down.bin"));

        (await File.ReadAllBytesAsync(Scratch("out/down.bin"))).Should().Equal(Bytes("second!"));
        File.Exists(Scratch("out/down.bin.part")).Should().BeFalse();
    }

    [Fact]
    public async Task Upload_EmptyAndBinaryContent_RoundTrips()
    {
        if (await OpenAsync() is not { } storage) return;
        var binary = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 31)).ToArray();

        await storage.UploadAsync(await WriteLocalAsync("empty.bin", []), $"{Root}/empty.bin");
        await storage.UploadAsync(await WriteLocalAsync("bin.bin", binary), $"{Root}/bin.bin");
        await storage.DownloadAsync($"{Root}/empty.bin", Scratch("e.out"));
        await storage.DownloadAsync($"{Root}/bin.bin", Scratch("b.out"));

        (await File.ReadAllBytesAsync(Scratch("e.out"))).Should().BeEmpty();
        (await File.ReadAllBytesAsync(Scratch("b.out"))).Should().Equal(binary);
    }

    [Fact]
    public async Task UploadStream_RoundTrips_FromCurrentPosition()
    {
        if (await OpenAsync() is not { } storage) return;
        using var stream = new MemoryStream(Bytes("skip-this|payload"));
        stream.Position = "skip-this|".Length;

        await storage.UploadAsync(stream, $"{Root}/s/stream.bin");
        await storage.DownloadAsync($"{Root}/s/stream.bin", Scratch("stream.out"));

        (await File.ReadAllBytesAsync(Scratch("stream.out"))).Should().Equal(Bytes("payload"));
    }

    [Fact]
    public async Task UploadStream_NonSeekable_RoundTrips()
    {
        if (await OpenAsync() is not { } storage) return;
        using var stream = new NonSeekableStream(Bytes("forward-only"));

        await storage.UploadAsync(stream, $"{Root}/ns.bin");
        await storage.DownloadAsync($"{Root}/ns.bin", Scratch("ns.out"));

        (await File.ReadAllBytesAsync(Scratch("ns.out"))).Should().Equal(Bytes("forward-only"));
    }

    [Fact]
    public async Task Download_Missing_ThrowsStorageNotFound_AndLeavesNoFiles()
    {
        if (await OpenAsync() is not { } storage) return;
        var target = Scratch("missing/x.bin");

        var act = () => storage.DownloadAsync($"{Root}/nope.bin", target);

        await act.Should().ThrowAsync<StorageNotFoundException>();
        File.Exists(target).Should().BeFalse();
        File.Exists(target + ".part").Should().BeFalse();
    }

    [Fact]
    public async Task Download_Missing_DoesNotTouchExistingDestination()
    {
        if (await OpenAsync() is not { } storage) return;
        var target = await WriteLocalAsync("keep.bin", Bytes("keep me"));

        var act = () => storage.DownloadAsync($"{Root}/nope.bin", target);

        await act.Should().ThrowAsync<StorageNotFoundException>();
        (await File.ReadAllBytesAsync(target)).Should().Equal(Bytes("keep me"));
    }

    [Fact]
    public async Task Delete_Missing_IsNoOp_AndDeleteRemoves()
    {
        if (await OpenAsync() is not { } storage) return;
        var key = $"{Root}/del.bin";

        await storage.DeleteAsync(key);
        await storage.UploadAsync(await WriteLocalAsync("d.bin", Bytes("x")), key);
        (await storage.ExistsAsync(key)).Should().BeTrue();
        await storage.DeleteAsync(key);

        (await storage.ExistsAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task Exists_ReportsTrueAndFalse_WithoutThrowing()
    {
        if (await OpenAsync() is not { } storage) return;

        (await storage.ExistsAsync($"{Root}/absent/deep/none.bin")).Should().BeFalse();
        await storage.UploadAsync(await WriteLocalAsync("e.bin", Bytes("x")), $"{Root}/present.bin");

        (await storage.ExistsAsync($"{Root}/present.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task GetSize_ReturnsLength_OrNullWhenMissing()
    {
        if (await OpenAsync() is not { } storage) return;
        await storage.UploadAsync(await WriteLocalAsync("sz.bin", new byte[12345]), $"{Root}/n/sz.bin");

        (await storage.GetSizeAsync($"{Root}/n/sz.bin")).Should().Be(12345);
        (await storage.GetSizeAsync($"{Root}/n/none.bin")).Should().BeNull();
        (await storage.GetSizeAsync($"{Root}/nofolder/none.bin")).Should().BeNull();
    }

    [Fact]
    public async Task List_ReturnsNestedPathsMatchingPrefix_WithSizes()
    {
        if (await OpenAsync() is not { } storage) return;
        if (!storage.SupportsListing) return;
        var local = await WriteLocalAsync("l.bin", Bytes("12345"));
        var keys = new[] { "chunks/aa/1", "chunks/aa/2", "chunks/ab/3", "chunks/ab/4", "chunks/ab/sub/5", "snapshots/s1.json", "HEAD" };
        foreach (var k in keys) await storage.UploadAsync(local, $"{Root}/{k}");

        var all = await ToListAsync(storage.ListAsync($"{Root}/"));
        var chunks = await ToListAsync(storage.ListAsync($"{Root}/chunks/ab/"));
        var partial = await ToListAsync(storage.ListAsync($"{Root}/chunks/a"));
        var none = await ToListAsync(storage.ListAsync($"{Root}/zzz/"));

        all.Select(e => e.Path).Should().BeEquivalentTo(keys.Select(k => $"{Root}/{k}"));
        all.Should().OnlyContain(e => e.Size == 5);
        chunks.Select(e => e.Path).Should().BeEquivalentTo([$"{Root}/chunks/ab/3", $"{Root}/chunks/ab/4", $"{Root}/chunks/ab/sub/5"]);
        partial.Should().HaveCount(5);
        none.Should().BeEmpty();
    }

    [Fact]
    public async Task List_ManyEntries_AreAllReturnedExactlyOnce()
    {
        if (await OpenAsync() is not { } storage) return;
        if (!storage.SupportsListing) return;
        var local = await WriteLocalAsync("p.bin", Bytes("p"));
        var keys = Enumerable.Range(0, 11).Select(i => $"{Root}/page/{i:D3}").ToList();
        foreach (var k in keys) await storage.UploadAsync(local, k);

        var listed = (await ToListAsync(storage.ListAsync($"{Root}/page/"))).Select(e => e.Path).ToList();

        listed.Should().HaveCount(keys.Count).And.BeEquivalentTo(keys);
    }

    [Fact]
    public async Task Prefix_IsolatesRepositories_AndIsTransparentToCallers()
    {
        if (await OpenAsync("") is not { } root) return;
        var a = (await OpenAsync($"{Root}/repoA"))!;
        var b = (await OpenAsync($"{Root}/repoB/"))!;
        var local = await WriteLocalAsync("pf.bin", Bytes("A"));

        await a.UploadAsync(local, "chunks/x");
        await a.UploadAsync(await WriteLocalAsync("pf2.bin", Bytes("AA")), "HEAD");

        (await a.ExistsAsync("chunks/x")).Should().BeTrue();
        (await a.GetSizeAsync("HEAD")).Should().Be(2);
        (await b.ExistsAsync("chunks/x")).Should().BeFalse();
        (await root.ExistsAsync($"{Root}/repoA/chunks/x")).Should().BeTrue();
        await a.DownloadAsync("chunks/x", Scratch("pf.out"));
        (await File.ReadAllBytesAsync(Scratch("pf.out"))).Should().Equal(Bytes("A"));

        if (root.SupportsListing)
        {
            (await ToListAsync(a.ListAsync(""))).Select(e => e.Path).Should().BeEquivalentTo(["chunks/x", "HEAD"]);
            (await ToListAsync(a.ListAsync("chunks/"))).Select(e => e.Path).Should().BeEquivalentTo(["chunks/x"]);
            (await ToListAsync(b.ListAsync(""))).Should().BeEmpty();
        }

        await a.DeleteAsync("chunks/x");
        (await root.ExistsAsync($"{Root}/repoA/chunks/x")).Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_IsHonoured()
    {
        if (await OpenAsync() is not { } storage) return;
        var local = await WriteLocalAsync("c.bin", Bytes("c"));
        await storage.UploadAsync(local, $"{Root}/c.bin");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await ((Func<Task>)(() => storage.UploadAsync(local, $"{Root}/c2.bin", cts.Token))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => storage.DownloadAsync($"{Root}/c.bin", Scratch("c.out"), cts.Token))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => storage.ExistsAsync($"{Root}/c.bin", cts.Token))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => storage.GetSizeAsync($"{Root}/c.bin", cts.Token))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => storage.DeleteAsync($"{Root}/c.bin", cts.Token))).Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Scratch("c.out")).Should().BeFalse();
        File.Exists(Scratch("c.out.part")).Should().BeFalse();
        (await storage.ExistsAsync($"{Root}/c.bin")).Should().BeTrue();
        (await storage.ExistsAsync($"{Root}/c2.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_StopsListing()
    {
        if (await OpenAsync() is not { } storage) return;
        if (!storage.SupportsListing) return;
        await storage.UploadAsync(await WriteLocalAsync("cl.bin", Bytes("c")), $"{Root}/cl/1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => ToListAsync(storage.ListAsync($"{Root}/cl/", cts.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>True when cancelling between entries stops an in-progress listing (in-process providers).</summary>
    protected virtual bool CancelsListingBetweenEntries => false;

    [Fact]
    public async Task Cancellation_MidListing_StopsEnumeration()
    {
        if (!CancelsListingBetweenEntries || await OpenAsync() is not { } storage) return;
        var local = await WriteLocalAsync("ml.bin", Bytes("m"));
        for (var i = 0; i < 6; i++) await storage.UploadAsync(local, $"{Root}/ml/{i}");
        using var cts = new CancellationTokenSource();
        var seen = 0;

        var act = async () =>
        {
            await foreach (var _ in storage.ListAsync($"{Root}/ml/", cts.Token))
            {
                seen++;
                cts.Cancel();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().BeLessThan(6);
    }

    [Fact]
    public async Task Concurrency_Hint_IsPositive()
    {
        if (await OpenAsync() is not { } storage) return;
        storage.MaxConcurrency.Should().BeGreaterThanOrEqualTo(1);
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class LocalStorageConformanceTests : StorageConformanceTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReStoreLocalConformance_" + Guid.NewGuid().ToString("N"));

    protected override bool CancelsListingBetweenEntries => true;

    protected override async Task<IStorage?> CreateStorageAsync(string prefix)
    {
        var storage = new LocalStorage(Logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _dir, ["prefix"] = prefix });
        return storage;
    }

    public override void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }
}

public sealed class InMemoryStorageConformanceTests : StorageConformanceTests
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _store = new(StringComparer.Ordinal);

    protected override bool CancelsListingBetweenEntries => true;

    protected override async Task<IStorage?> CreateStorageAsync(string prefix)
    {
        var storage = new InMemoryStorage(Logger, _store);
        await storage.InitializeAsync(new Dictionary<string, string> { ["prefix"] = prefix });
        return storage;
    }
}

/// <summary>
/// Runs the contract against a real provider when configured, otherwise passes vacuously. Set
/// <c>RESTORE_CONFORMANCE_TYPE</c> (s3, azure, gcp, b2, sftp, dropbox, gdrive, github, local) and
/// <c>RESTORE_CONFORMANCE_OPTIONS</c> (JSON object of provider options, as in config.json). Tests write only
/// under a unique <c>conf-*</c> folder (they do not clean it up). xunit 2 has no runtime skip, so when the
/// variables are unset these tests pass vacuously (they are not reported as skipped).
/// </summary>
public sealed class EnvConfiguredStorageConformanceTests : StorageConformanceTests
{
    protected override async Task<IStorage?> CreateStorageAsync(string prefix)
    {
        var type = Environment.GetEnvironmentVariable("RESTORE_CONFORMANCE_TYPE");
        var json = Environment.GetEnvironmentVariable("RESTORE_CONFORMANCE_OPTIONS");
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var options = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        options["prefix"] = prefix;
        return await new StorageFactory(Logger).CreateStorageAsync(type, new StorageConfig { Options = options });
    }
}
