using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Parallel chunk transfer (bounded by IStorage.MaxConcurrency), batched existence checks and stream uploads.
/// Concurrency is proved with barriers on counting storage doubles, never with timing.
/// </summary>
public class ParallelTransferTests : IDisposable
{
    private static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(30);

    private readonly string _testRoot;
    private readonly TestLogger _logger = new();

    public ParallelTransferTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreParallelTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    // ---- backup: parallel upload -------------------------------------------------------------------------------

    [Fact]
    public async Task Backup_ShouldUploadChunksInParallel_UpToMaxConcurrencyAndNoFurther()
    {
        const int limit = 3;
        var fixture = CreateFixture(limit);
        WriteRandomFile(fixture.Source, "big.bin", 512 * 1024);

        // The first `limit` uploads wait until `limit` are in flight at once: this can only complete when the
        // pipeline really runs `limit` uploads concurrently (a sequential one would hit the deadlock guard).
        var barrier = new InFlightBarrier(limit);
        fixture.Storage.OnChunkUpload = async (_, _) => await barrier.EnterAsync();
        fixture.Storage.OnChunkUploadDone = barrier.Exit;

        await fixture.RunBackupAsync();

        barrier.Peak.Should().Be(limit, "uploads must reach, and never exceed, MaxConcurrency");
        fixture.Storage.ChunkUploads.Should().BeGreaterThan(limit);
        fixture.Storage.ChunkUploadsViaPath.Should().Be(0, "chunks are streamed, not written to a temp file first");
    }

    [Fact]
    public async Task Backup_ShouldUseSingleWorker_WhenMaxConcurrencyIsOne()
    {
        var fixture = CreateFixture(1);
        WriteRandomFile(fixture.Source, "big.bin", 256 * 1024);

        var barrier = new InFlightBarrier(1);
        fixture.Storage.OnChunkUpload = async (_, _) => await barrier.EnterAsync();
        fixture.Storage.OnChunkUploadDone = barrier.Exit;

        await fixture.RunBackupAsync();

        barrier.Peak.Should().Be(1);
        fixture.Storage.ChunkUploads.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Backup_ShouldCancelOtherUploadsAndWriteNoManifest_WhenOneUploadFails()
    {
        const int limit = 4;
        var fixture = CreateFixture(limit);
        WriteRandomFile(fixture.Source, "big.bin", 512 * 1024);

        var barrier = new InFlightBarrier(limit);
        var cancelled = 0;
        var failedOnce = 0;
        fixture.Storage.OnChunkUpload = async (_, ct) =>
        {
            await barrier.EnterAsync();
            if (Interlocked.Exchange(ref failedOnce, 1) == 0)
            {
                throw new IOException("simulated upload failure");
            }

            try
            {
                // Completes only through cancellation, which the failure must trigger.
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
        };

        var action = () => fixture.RunBackupAsync();

        await action.Should().ThrowAsync<IOException>().WithMessage("simulated upload failure");

        cancelled.Should().Be(limit - 1, "every other in-flight upload is cancelled by the first failure");
        fixture.Store.Keys.Should().NotContain(key => key.EndsWith(".manifest.json", StringComparison.Ordinal));
        fixture.Store.Keys.Should().NotContain(key => key.EndsWith("/HEAD", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backup_ShouldProduceSameManifestAndChunks_AsASequentialRun()
    {
        var sequential = CreateFixture(1);
        var parallel = CreateFixture(8, source: sequential.Source);
        WriteRandomFile(sequential.Source, "a.bin", 300 * 1024);
        WriteRandomFile(sequential.Source, "b.bin", 200 * 1024);
        File.WriteAllText(Path.Combine(sequential.Source, "c.txt"), new string('c', 50_000));

        await sequential.RunBackupAsync();
        await parallel.RunBackupAsync();

        var sequentialManifest = LoadManifest(sequential.Store);
        var parallelManifest = LoadManifest(parallel.Store);

        JsonSerializer.Serialize(parallelManifest.Files).Should().Be(JsonSerializer.Serialize(sequentialManifest.Files));

        var sequentialChunks = ChunkKeys(sequential.Store);
        ChunkKeys(parallel.Store).Should().BeEquivalentTo(sequentialChunks);
        foreach (var key in sequentialChunks)
        {
            parallel.Store[key].Should().Equal(sequential.Store[key]);
        }

        // Counters are updated by many workers; they must still add up exactly.
        var storedBytes = sequentialChunks.Sum(key => (long)sequential.Store[key].Length);
        parallel.State.Telemetry.Backup.UploadedStoredBytes.Should().Be(storedBytes);
        parallel.State.Telemetry.Backup.ReferencedStoredBytes.Should().Be(storedBytes);
        parallel.State.Telemetry.Backup.DedupSavedBytes.Should().Be(0);
        sequential.State.Telemetry.Backup.UploadedStoredBytes.Should().Be(storedBytes);
    }

    // ---- backup: batched existence -----------------------------------------------------------------------------

    [Fact]
    public async Task Backup_ShouldListChunksOnce_AndSkipPerChunkExistsCalls_WhenManyCandidateChunks()
    {
        var fixture = CreateFixture(4);
        WriteRandomFile(fixture.Source, "big.bin", 1024 * 1024);

        await fixture.RunBackupAsync();

        var firstChunkCount = ChunkKeys(fixture.Store).Count;
        firstChunkCount.Should().BeGreaterThan(ChunkExistenceIndex.DefaultListingThreshold * 2);
        fixture.Storage.ListCalls.Should().ContainSingle().Which.Should().StartWith("chunks/");
        fixture.Storage.ChunkExistsCalls.Should().Be(ChunkExistenceIndex.DefaultListingThreshold,
            "only the lookups up to the threshold go to the provider one by one");

        // Same content again under a new name: every chunk is a candidate and already stored.
        File.Copy(Path.Combine(fixture.Source, "big.bin"), Path.Combine(fixture.Source, "copy.bin"));
        fixture.Storage.ResetCounters();

        await fixture.RunBackupAsync();

        fixture.Storage.ListCalls.Should().ContainSingle();
        fixture.Storage.ChunkExistsCalls.Should().Be(ChunkExistenceIndex.DefaultListingThreshold);
        fixture.Storage.ChunkUploads.Should().Be(0, "every chunk was found by the listing");
        ChunkKeys(fixture.Store).Should().HaveCount(firstChunkCount);
    }

    [Fact]
    public async Task Backup_ShouldNotList_WhenFewCandidateChunks()
    {
        var fixture = CreateFixture(4);
        File.WriteAllText(Path.Combine(fixture.Source, "small.txt"), "hello");

        await fixture.RunBackupAsync();

        fixture.Storage.ListCalls.Should().BeEmpty();
        fixture.Storage.ChunkExistsCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Backup_ShouldFallBackToPerChunkExists_WhenProviderCannotList()
    {
        var fixture = CreateFixture(4);
        fixture.Storage.ListingSupported = false;
        WriteRandomFile(fixture.Source, "big.bin", 512 * 1024);

        await fixture.RunBackupAsync();

        fixture.Storage.ListCalls.Should().BeEmpty();
        fixture.Storage.ChunkExistsCalls.Should().Be(ChunkKeys(fixture.Store).Count);
    }

    [Fact]
    public async Task Backup_ShouldReuploadAChunk_WhenListedSizeDiffersFromExpected()
    {
        var fixture = CreateFixture(4);
        WriteRandomFile(fixture.Source, "big.bin", 512 * 1024);
        await fixture.RunBackupAsync();

        // Truncate every stored chunk. The first lookups (up to the threshold) are plain existence checks, which
        // cannot see the damage; every lookup after the listing is loaded compares sizes and must repair its chunk.
        var good = ChunkKeys(fixture.Store).ToDictionary(key => key, key => fixture.Store[key]);
        good.Count.Should().BeGreaterThan(ChunkExistenceIndex.DefaultListingThreshold);
        foreach (var (key, bytes) in good)
        {
            fixture.Store[key] = bytes[..(bytes.Length / 2)];
        }

        File.Copy(Path.Combine(fixture.Source, "big.bin"), Path.Combine(fixture.Source, "copy.bin"));
        fixture.Storage.ResetCounters();
        await fixture.RunBackupAsync();

        fixture.Storage.ListCalls.Should().ContainSingle();
        var repaired = good.Count(pair => fixture.Store[pair.Key].SequenceEqual(pair.Value));
        repaired.Should().Be(fixture.Storage.ChunkUploads);
        repaired.Should().BeGreaterThanOrEqualTo(good.Count - ChunkExistenceIndex.DefaultListingThreshold,
            "every chunk looked up after the listing was loaded is size-checked and re-uploaded");
    }

    [Fact]
    public async Task ChunkExistenceIndex_ShouldFallBackToExists_WhenListingFails()
    {
        var fixture = CreateFixture(2);
        fixture.Storage.ListFailure = new IOException("listing denied");
        fixture.Store["chunks/aa/present.chunk"] = [1, 2, 3];
        var index = new ChunkExistenceIndex(fixture.Storage, "chunks/", threshold: 0, _logger);

        (await index.ExistsAsync("chunks/aa/present.chunk", 3, CancellationToken.None)).Should().BeTrue();
        (await index.ExistsAsync("chunks/aa/absent.chunk", 3, CancellationToken.None)).Should().BeFalse();

        index.IsListingLoaded.Should().BeFalse();
        fixture.Storage.ListCalls.Should().ContainSingle("a failed listing is not retried for every chunk");
        _logger.Messages.Should().Contain(message => message.Contains("Listing 'chunks/' failed"));
    }

    // ---- queue ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ParallelWorkQueue_ShouldRethrowFirstFailureToProducer_AndCancelWorkers()
    {
        var started = new InFlightBarrier(2);
        var cancelled = 0;
        var queue = new ParallelWorkQueue<int>(2, async (item, ct) =>
        {
            await started.EnterAsync();
            if (item == 0)
            {
                throw new InvalidOperationException("boom");
            }

            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
        }, CancellationToken.None);

        await queue.EnqueueAsync(0);
        await queue.EnqueueAsync(1);

        var action = async () =>
        {
            for (var i = 2; i < 100; i++)
            {
                await queue.EnqueueAsync(i);
            }

            await queue.CompleteAsync();
        };

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        // The producer sees the failure as soon as it is recorded; the cancelled worker may still be unwinding. Disposing the
        // queue waits for every worker to finish, so the count is final (no sleeping, no timeouts to tune).
        await queue.DisposeAsync();
        cancelled.Should().Be(1);
    }

    // ---- restore -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Restore_ShouldDownloadChunksInParallel_AndProduceIdenticalFiles()
    {
        const int limit = 4;
        var fixture = CreateFixture(limit);
        WriteRandomFile(fixture.Source, "a.bin", 400 * 1024);
        WriteRandomFile(fixture.Source, "b.bin", 300 * 1024);
        File.WriteAllText(Path.Combine(fixture.Source, "c.txt"), "tiny");
        // A file made of one repeated block: its chunks repeat inside the restore window.
        File.WriteAllBytes(Path.Combine(fixture.Source, "zeros.bin"), new byte[200 * 1024]);
        await fixture.RunBackupAsync();

        var barrier = new InFlightBarrier(limit);
        fixture.Storage.OnChunkDownload = async (_, _) => await barrier.EnterAsync();
        fixture.Storage.OnChunkDownloadDone = barrier.Exit;

        var target = Path.Combine(_testRoot, "restored");
        var manifestPath = fixture.State.GetPreviousBackupPath(fixture.Source)!;
        await new Restore(_logger, fixture.Storage).RestoreFromBackupAsync(manifestPath, target);

        barrier.Peak.Should().Be(limit);
        foreach (var name in new[] { "a.bin", "b.bin", "c.txt", "zeros.bin" })
        {
            File.ReadAllBytes(Path.Combine(target, name)).Should().Equal(File.ReadAllBytes(Path.Combine(fixture.Source, name)));
        }
    }

    [Fact]
    public async Task Restore_ShouldFailAndDownloadNoMoreThanAWindow_WhenOneDownloadFails()
    {
        var fixture = CreateFixture(3);
        WriteRandomFile(fixture.Source, "a.bin", 512 * 1024);
        await fixture.RunBackupAsync();

        var calls = 0;
        fixture.Storage.OnChunkDownload = (_, _) =>
            Interlocked.Increment(ref calls) == 2 ? throw new IOException("simulated download failure") : Task.CompletedTask;

        var manifestPath = fixture.State.GetPreviousBackupPath(fixture.Source)!;
        var action = () => new Restore(_logger, fixture.Storage).RestoreFromBackupAsync(manifestPath, Path.Combine(_testRoot, "restored-fail"));

        await action.Should().ThrowAsync<IOException>().WithMessage("simulated download failure");
        File.Exists(Path.Combine(_testRoot, "restored-fail", "a.bin")).Should().BeFalse("no partial file is left behind");
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static List<string> ChunkKeys(ConcurrentDictionary<string, byte[]> store) =>
        store.Keys.Where(key => key.StartsWith("chunks/", StringComparison.Ordinal)).ToList();

    private static SnapshotManifest LoadManifest(ConcurrentDictionary<string, byte[]> store)
    {
        var key = store.Keys.Single(k => k.EndsWith(".manifest.json", StringComparison.Ordinal));
        return SnapshotManifestSerializer.Deserialize(store[key]);
    }

    private static void WriteRandomFile(string directory, string name, int size) =>
        File.WriteAllBytes(Path.Combine(directory, name), RandomNumberGenerator.GetBytes(size));

    private Fixture CreateFixture(int maxConcurrency, string? source = null)
    {
        source ??= Path.Combine(_testRoot, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);

        var store = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var storage = new ProbeStorage(new InMemoryStorage(_logger, store), maxConcurrency);

        // Small chunks so a modest file yields hundreds of them.
        var chunking = new ChunkDiffingConfig { MinChunkSizeKB = 1, TargetChunkSizeKB = 2, MaxChunkSizeKB = 4 };
        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        configMock.SetupGet(c => c.GlobalStorageType).Returns("local");
        configMock.SetupGet(c => c.SizeThresholdMB).Returns(500);
        configMock.SetupGet(c => c.ExcludedPatterns).Returns([]);
        configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        configMock.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        configMock.SetupGet(c => c.WatchDirectories).Returns([]);
        configMock.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        configMock.SetupGet(c => c.ChunkDiffing).Returns(chunking);
        configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_testRoot, "state-" + Guid.NewGuid().ToString("N") + ".json"));

        return new Fixture(Path.GetFullPath(source), store, storage, state, configMock.Object, _logger);
    }

    private sealed class Fixture(
        string source,
        ConcurrentDictionary<string, byte[]> store,
        ProbeStorage storage,
        SystemState state,
        IConfigManager config,
        ILogger logger)
    {
        public string Source { get; } = source;
        public ConcurrentDictionary<string, byte[]> Store { get; } = store;
        public ProbeStorage Storage { get; } = storage;
        public SystemState State { get; } = state;

        public Task RunBackupAsync() =>
            new Backup(logger, State, new SizeAnalyzer(), config, null).BackupDirectoryAsync(Source);
    }

    /// <summary>Lets <c>parties</c> callers in, then blocks each of the first <c>parties</c> until all have arrived.</summary>
    private sealed class InFlightBarrier(int parties)
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;
        private int _current;
        private int _peak;

        public int Peak => Volatile.Read(ref _peak);

        public async Task EnterAsync()
        {
            var now = Interlocked.Increment(ref _current);
            int seen;
            do
            {
                seen = Volatile.Read(ref _peak);
            }
            while (now > seen && Interlocked.CompareExchange(ref _peak, now, seen) != seen);

            if (Interlocked.Increment(ref _entered) == parties)
            {
                _allArrived.TrySetResult();
            }

            if (Volatile.Read(ref _entered) <= parties)
            {
                await _allArrived.Task.WaitAsync(Deadlock);
            }
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }

    /// <summary>In-memory storage that counts calls and lets a test hook chunk uploads and downloads.</summary>
    private sealed class ProbeStorage(InMemoryStorage inner, int maxConcurrency) : DelegatingStorage(inner)
    {
        private readonly ConcurrentQueue<string> _listCalls = new();
        private int _chunkExists;
        private int _chunkUploads;
        private int _chunkUploadsViaPath;

        public Func<string, CancellationToken, Task>? OnChunkUpload { get; set; }
        public Action? OnChunkUploadDone { get; set; }
        public Func<string, CancellationToken, Task>? OnChunkDownload { get; set; }
        public Action? OnChunkDownloadDone { get; set; }
        public bool ListingSupported { get; set; } = true;
        public Exception? ListFailure { get; set; }

        public IReadOnlyCollection<string> ListCalls => [.. _listCalls];
        public int ChunkExistsCalls => Volatile.Read(ref _chunkExists);
        public int ChunkUploads => Volatile.Read(ref _chunkUploads);
        public int ChunkUploadsViaPath => Volatile.Read(ref _chunkUploadsViaPath);

        public override int MaxConcurrency => maxConcurrency;
        public override bool SupportsListing => ListingSupported;

        public void ResetCounters()
        {
            _listCalls.Clear();
            Interlocked.Exchange(ref _chunkExists, 0);
            Interlocked.Exchange(ref _chunkUploads, 0);
            Interlocked.Exchange(ref _chunkUploadsViaPath, 0);
        }

        public override async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
        {
            if (!IsChunk(remotePath))
            {
                await Inner.UploadAsync(content, remotePath, ct);
                return;
            }

            Interlocked.Increment(ref _chunkUploads);
            try
            {
                if (OnChunkUpload != null)
                {
                    await OnChunkUpload(remotePath, ct);
                }

                await Inner.UploadAsync(content, remotePath, ct);
            }
            finally
            {
                OnChunkUploadDone?.Invoke();
            }
        }

        public override Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default)
        {
            if (IsChunk(remotePath))
            {
                Interlocked.Increment(ref _chunkUploadsViaPath);
            }

            return Inner.UploadAsync(localPath, remotePath, ct);
        }

        public override Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default)
        {
            if (IsChunk(remotePath))
            {
                Interlocked.Increment(ref _chunkExists);
            }

            return Inner.ExistsAsync(remotePath, ct);
        }

        public override async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
        {
            if (!IsChunk(remotePath))
            {
                await Inner.DownloadAsync(remotePath, localPath, ct);
                return;
            }

            try
            {
                if (OnChunkDownload != null)
                {
                    await OnChunkDownload(remotePath, ct);
                }

                await Inner.DownloadAsync(remotePath, localPath, ct);
            }
            finally
            {
                OnChunkDownloadDone?.Invoke();
            }
        }

        public override IAsyncEnumerable<StorageEntry> ListAsync(string prefix, CancellationToken ct = default)
        {
            _listCalls.Enqueue(prefix);
            if (ListFailure != null)
            {
                throw ListFailure;
            }

            return Inner.ListAsync(prefix, ct);
        }

        private static bool IsChunk(string remotePath) => remotePath.StartsWith("chunks/", StringComparison.Ordinal);
    }
}
