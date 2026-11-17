using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Dedup savings are reported as an exact byte figure, not an average-size estimate, so these
/// pin the arithmetic against real chunk stored sizes.
/// </summary>
public class DedupTelemetryTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger = new();

    public DedupTelemetryTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreDedupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task FirstBackup_ShouldSaveNothing_BecauseEveryChunkIsNew()
    {
        var fixture = await CreateFixtureAsync();
        await WriteFileAsync(fixture, "data.txt", new string('a', 200 * 1024));

        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await backup.BackupDirectoryAsync(fixture.SourceDirectory);

        var telemetry = fixture.State.Telemetry.Backup;

        telemetry.ReferencedStoredBytes.Should().BeGreaterThan(0);
        telemetry.UploadedStoredBytes.Should().Be(telemetry.ReferencedStoredBytes,
            "nothing existed in storage yet, so every referenced byte had to be uploaded");
        telemetry.DedupSavedBytes.Should().Be(0);
    }

    [Fact]
    public async Task SecondBackup_ShouldSaveTheBytesOfChunksItDidNotReupload()
    {
        var fixture = await CreateFixtureAsync();
        var body = new string('b', 300 * 1024);
        await WriteFileAsync(fixture, "data.txt", body);

        var firstBackup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await firstBackup.BackupDirectoryAsync(fixture.SourceDirectory);

        fixture.State.Telemetry.Backup.DedupSavedBytes.Should().Be(0, "the first snapshot uploaded everything");

        var afterFirstReferenced = fixture.State.Telemetry.Backup.ReferencedStoredBytes;
        var afterFirstUploaded = fixture.State.Telemetry.Backup.UploadedStoredBytes;

        // A new file forces a second snapshot. data.txt is unchanged, so the second snapshot
        // re-references its chunks without re-uploading them.
        await WriteFileAsync(fixture, "added.txt", "small addition");

        var secondBackup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await secondBackup.BackupDirectoryAsync(fixture.SourceDirectory);

        var telemetry = fixture.State.Telemetry.Backup;
        telemetry.SnapshotCount.Should().Be(2);

        var secondReferenced = telemetry.ReferencedStoredBytes - afterFirstReferenced;
        var secondUploaded = telemetry.UploadedStoredBytes - afterFirstUploaded;

        secondReferenced.Should().BeGreaterThan(secondUploaded,
            "the second snapshot referenced the unchanged file's chunks without transferring them");

        telemetry.DedupSavedBytes.Should().Be(secondReferenced - secondUploaded,
            "savings are exactly the referenced bytes that did not have to be transferred");
        telemetry.DedupSavedBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DedupSavedBytes_ShouldEqualReferencedMinusUploaded()
    {
        var fixture = await CreateFixtureAsync();

        // Two files sharing a long identical prefix, so some chunks dedup within one snapshot.
        var shared = new string('c', 400 * 1024);
        await WriteFileAsync(fixture, "one.txt", shared + "tail-one");
        await WriteFileAsync(fixture, "two.txt", shared + "tail-two");

        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await backup.BackupDirectoryAsync(fixture.SourceDirectory);

        await WriteFileAsync(fixture, "three.txt", shared + "tail-three");

        var secondBackup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await secondBackup.BackupDirectoryAsync(fixture.SourceDirectory);

        var telemetry = fixture.State.Telemetry.Backup;

        telemetry.DedupSavedBytes.Should().Be(
            telemetry.ReferencedStoredBytes - telemetry.UploadedStoredBytes);
        telemetry.DedupSavedBytes.Should().BeGreaterThan(0,
            "the shared prefix means the second snapshot reused chunks");
    }

    [Fact]
    public async Task DedupSavedBytes_ShouldNeverBeNegative()
    {
        var fixture = await CreateFixtureAsync();
        await WriteFileAsync(fixture, "tiny.txt", "x");

        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await backup.BackupDirectoryAsync(fixture.SourceDirectory);

        fixture.State.Telemetry.Backup.DedupSavedBytes.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task DedupTelemetry_ShouldSurviveSaveAndLoad()
    {
        var fixture = await CreateFixtureAsync();
        await WriteFileAsync(fixture, "data.txt", new string('d', 150 * 1024));

        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await backup.BackupDirectoryAsync(fixture.SourceDirectory);
        await fixture.State.SaveStateAsync();

        var expectedReferenced = fixture.State.Telemetry.Backup.ReferencedStoredBytes;
        var expectedUploaded = fixture.State.Telemetry.Backup.UploadedStoredBytes;

        var reloaded = new SystemState(_logger);
        reloaded.SetStateFilePath(fixture.StatePath);
        await reloaded.LoadStateAsync();

        reloaded.Telemetry.Backup.ReferencedStoredBytes.Should().Be(expectedReferenced);
        reloaded.Telemetry.Backup.UploadedStoredBytes.Should().Be(expectedUploaded);
    }

    private static async Task WriteFileAsync(BackupFixture fixture, string name, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(fixture.SourceDirectory, name), content);
    }

    private async Task<BackupFixture> CreateFixtureAsync()
    {
        var sourceDirectory = Path.Combine(_testRoot, "source-" + Guid.NewGuid().ToString("N"));
        var storageDirectory = Path.Combine(_testRoot, "storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(storageDirectory);

        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDirectory });

        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        configMock.SetupGet(c => c.GlobalStorageType).Returns("local");
        configMock.SetupGet(c => c.SizeThresholdMB).Returns(500);
        configMock.SetupGet(c => c.ExcludedPatterns).Returns([]);
        configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        configMock.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        configMock.SetupGet(c => c.WatchDirectories).Returns([]);
        configMock.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var statePath = Path.Combine(_testRoot, "state-" + Guid.NewGuid().ToString("N") + ".json");
        var state = new SystemState(_logger);
        state.SetStateFilePath(statePath);

        return new BackupFixture(
            Path.GetFullPath(sourceDirectory),
            storageDirectory,
            statePath,
            configMock.Object,
            state);
    }

    [Fact]
    public async Task Backup_ShouldUploadChunksWhileStillChunking_NotAfterEveryFileIsRead()
    {
        var sourceDirectory = Path.Combine(_testRoot, "interleave-source");
        var storageDirectory = Path.Combine(_testRoot, "interleave-storage");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(storageDirectory);

        // Far more distinct chunks than the upload queue plus the in-flight uploads can hold (LocalStorage: 4 workers + 8 queued),
        // so while uploads are held back the chunking loop cannot get through every file.
        const int fileCount = 40;
        for (var index = 0; index < fileCount; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, $"file{index:D2}.txt"),
                new string((char)('A' + index), 200 * 1024));
        }

        var inner = new LocalStorage(_logger);
        await inner.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDirectory });

        var observing = new UploadObservingStorage(inner);

        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        configMock.SetupGet(c => c.GlobalStorageType).Returns("local");
        configMock.SetupGet(c => c.SizeThresholdMB).Returns(500);
        configMock.SetupGet(c => c.ExcludedPatterns).Returns([]);
        configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        configMock.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        configMock.SetupGet(c => c.WatchDirectories).Returns([]);
        configMock.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(observing);

        // Records how many files had been fully chunked at the moment of the first upload. The progress callback runs
        // synchronously on the chunking thread (no Progress<T> post to the thread pool), and chunk uploads are held at a
        // gate, so the answer does not depend on how the machine schedules the threads: the chunking loop can only run
        // ahead of held uploads by the bounded queue, never through all the files.
        var filesChunkedAtFirstUpload = -1;
        var filesChunked = 0;
        var firstUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new SynchronousProgress(report =>
        {
            if (report.Phase == BackupPhase.Chunking)
            {
                Volatile.Write(ref filesChunked, report.FilesDone);
            }
        });

        observing.OnChunkUpload = () =>
        {
            if (filesChunkedAtFirstUpload < 0)
            {
                filesChunkedAtFirstUpload = Volatile.Read(ref filesChunked);
                firstUpload.TrySetResult();
            }
        };
        observing.ChunkGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_testRoot, "interleave-state.json"));

        var backup = new Backup(_logger, state, new SizeAnalyzer(), configMock.Object, null);
        var backupTask = backup.BackupDirectoryAsync(sourceDirectory, null, progress);

        // Event-driven: the first chunk upload either arrives (and is counted) or the backup died before it.
        await Task.WhenAny(firstUpload.Task, backupTask).WaitAsync(TimeSpan.FromMinutes(2));
        firstUpload.Task.IsCompleted.Should().BeTrue("a chunk must be uploaded while the chunking loop is still running");
        observing.ChunkGate.TrySetResult();
        await backupTask;

        observing.ChunkUploads.Should().BeGreaterThan(0);

        // Chunks upload as they are produced, so the first object stored is a chunk and the
        // manifest follows all of them. Buffering payloads until the end would make peak
        // memory scale with the size of the change set.
        observing.ManifestUploadedBeforeAnyChunk.Should().BeFalse();
        observing.UploadOrder.Should().NotBeEmpty();
        observing.UploadOrder[0].Should().StartWith("chunks/");

        filesChunkedAtFirstUpload.Should().BeLessThan(fileCount,
            "the first chunk must reach storage before the last file has finished chunking");

        state.GetPreviousBackupPath(Path.GetFullPath(sourceDirectory)).Should().NotBeNullOrWhiteSpace(
            "the snapshot must still commit correctly");
    }

    /// <summary>Records upload order so chunk/manifest interleaving can be asserted.</summary>
    /// <summary>Calls the callback on the reporting thread, like a plain IProgress implementation (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SynchronousProgress(Action<BackupProgress> callback) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => callback(value);
    }

    private sealed class UploadObservingStorage(LocalStorage inner) : DelegatingStorage(inner)
    {
        public List<string> UploadOrder { get; } = [];

        public int ChunkUploads { get; private set; }

        public bool ManifestUploadedBeforeAnyChunk { get; private set; }

        public Action? OnChunkUpload { get; set; }

        /// <summary>When set, chunk uploads are counted on entry and then held until the gate opens.</summary>
        public TaskCompletionSource? ChunkGate { get; set; }

        private readonly Lock _gate = new();

        public override async Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default)
        {
            await HoldAsync(Observe(remotePath), ct);
            await Inner.UploadAsync(localPath, remotePath, ct);
        }

        public override async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
        {
            await HoldAsync(Observe(remotePath), ct);
            await Inner.UploadAsync(content, remotePath, ct);
        }

        private async Task HoldAsync(bool isChunk, CancellationToken ct)
        {
            if (isChunk && ChunkGate is { } gate)
            {
                await gate.Task.WaitAsync(TimeSpan.FromMinutes(2), ct);
            }
        }

        private bool Observe(string remotePath)
        {
            lock (_gate)
            {
                UploadOrder.Add(remotePath);

                if (remotePath.StartsWith("chunks/", StringComparison.OrdinalIgnoreCase))
                {
                    ChunkUploads++;
                    OnChunkUpload?.Invoke();
                    return true;
                }

                if (remotePath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase) && ChunkUploads == 0)
                {
                    ManifestUploadedBeforeAnyChunk = true;
                }

                return false;
            }
        }

        public override void Dispose() => Inner.Dispose();
    }

    private sealed record BackupFixture(
        string SourceDirectory,
        string StorageDirectory,
        string StatePath,
        IConfigManager Config,
        SystemState State);
}
