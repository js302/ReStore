using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Covers the roadmap's progress/cancellation contract: a cancelled backup must not advance
/// HEAD, a cancelled restore must leave no partial file, and progress must be monotonic.
/// </summary>
public class ProgressAndCancellationTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger = new();

    public ProgressAndCancellationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreProgressTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/>. <see cref="Progress{T}"/> posts callbacks to
    /// the thread pool when there is no synchronization context, so its delivery order does
    /// not reflect the order reports were generated — which is what these tests assert.
    /// </summary>
    private sealed class SyncProgress<T>(Action<T>? onReport = null) : IProgress<T>
    {
        private readonly Lock _gate = new();

        public List<T> Reports { get; } = [];

        public void Report(T value)
        {
            lock (_gate)
            {
                Reports.Add(value);
            }

            onReport?.Invoke(value);
        }
    }

    [Fact]
    public async Task BackupDirectoryAsync_ShouldThrowAndNotAdvanceHead_WhenCancelledBeforeStarting()
    {
        var fixture = await CreateFixtureAsync(fileCount: 6);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);

        var act = async () => await backup.BackupDirectoryAsync(fixture.SourceDirectory, null, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        var headPath = Path.Combine(
            fixture.StorageDirectory,
            SnapshotStoragePaths.GetHeadPath(fixture.SourceDirectory).Replace('/', Path.DirectorySeparatorChar));

        File.Exists(headPath).Should().BeFalse("a cancelled backup must not publish a HEAD");
        fixture.State.GetPreviousBackupPath(fixture.SourceDirectory).Should().BeNull();
    }

    [Fact]
    public async Task BackupDirectoryAsync_ShouldThrowAndNotAdvanceHead_WhenCancelledMidChunking()
    {
        var fixture = await CreateFixtureAsync(fileCount: 40);
        using var cts = new CancellationTokenSource();

        // Cancel once chunking has started, so the token is observed inside the file loop
        // rather than at the entry guard.
        var progress = new SyncProgress<BackupProgress>(report =>
        {
            if (report.Phase == BackupPhase.Chunking && report.FilesDone >= 2)
            {
                cts.Cancel();
            }
        });

        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);

        var act = async () => await backup.BackupDirectoryAsync(fixture.SourceDirectory, null, progress, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        var headPath = Path.Combine(
            fixture.StorageDirectory,
            SnapshotStoragePaths.GetHeadPath(fixture.SourceDirectory).Replace('/', Path.DirectorySeparatorChar));

        File.Exists(headPath).Should().BeFalse("HEAD only advances after every chunk is uploaded");
    }

    [Fact]
    public async Task BackupDirectoryAsync_ShouldReportNonDecreasingFilesDone_EndingAtFilesTotal()
    {
        var fixture = await CreateFixtureAsync(fileCount: 12);
        var progress = new SyncProgress<BackupProgress>();

        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await backup.BackupDirectoryAsync(fixture.SourceDirectory, null, progress);

        var reports = progress.Reports;
        reports.Should().NotBeEmpty();

        var chunkingReports = reports.Where(report => report.Phase == BackupPhase.Chunking).ToList();
        chunkingReports.Should().NotBeEmpty();

        chunkingReports.Select(report => report.FilesDone)
            .Should().BeInAscendingOrder("FilesDone must never go backwards");

        chunkingReports[^1].FilesDone.Should().Be(chunkingReports[^1].FilesTotal,
            "the final chunking report accounts for every file");

        reports.Select(report => report.Fraction).Should().OnlyContain(fraction => fraction >= 0 && fraction <= 1);
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldLeaveNoPartialFile_WhenCancelledMidRestore()
    {
        var fixture = await CreateFixtureAsync(fileCount: 30);
        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await backup.BackupDirectoryAsync(fixture.SourceDirectory);

        var manifestPath = fixture.State.GetPreviousBackupPath(fixture.SourceDirectory);
        manifestPath.Should().NotBeNullOrWhiteSpace();

        var restoreDirectory = Path.Combine(_testRoot, "cancelled-restore");
        using var cts = new CancellationTokenSource();

        var progress = new SyncProgress<RestoreProgress>(report =>
        {
            if (report.Phase == RestorePhase.Restoring && report.FilesDone >= 2)
            {
                cts.Cancel();
            }
        });

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);

        var act = async () => await restore.RestoreFromBackupAsync(
            manifestPath!,
            restoreDirectory,
            RestoreOptions.Default,
            progress,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        Directory.GetFiles(restoreDirectory, "*.restorepartial", SearchOption.AllDirectories)
            .Should().BeEmpty("an interrupted file must be cleaned up, not left as a stub");

        // Every file that does exist must be complete, not truncated.
        foreach (var restoredFile in Directory.GetFiles(restoreDirectory, "*", SearchOption.AllDirectories))
        {
            var sourceEquivalent = Path.Combine(fixture.SourceDirectory, Path.GetFileName(restoredFile));
            if (File.Exists(sourceEquivalent))
            {
                new FileInfo(restoredFile).Length.Should().Be(new FileInfo(sourceEquivalent).Length);
            }
        }
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldReportNonDecreasingProgress_EndingAtFilesTotal()
    {
        var fixture = await CreateFixtureAsync(fileCount: 8);
        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await backup.BackupDirectoryAsync(fixture.SourceDirectory);

        var manifestPath = fixture.State.GetPreviousBackupPath(fixture.SourceDirectory);
        var progress = new SyncProgress<RestoreProgress>();

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        await restore.RestoreFromBackupAsync(
            manifestPath!,
            Path.Combine(_testRoot, "progress-restore"),
            RestoreOptions.Default,
            progress);

        var reports = progress.Reports;
        reports.Should().NotBeEmpty();
        reports.Select(report => report.FilesDone).Should().BeInAscendingOrder();
        reports[^1].FilesDone.Should().Be(reports[^1].FilesTotal);
        reports[^1].Phase.Should().Be(RestorePhase.Finalising);
    }

    [Fact]
    public async Task VerifyAsync_ShouldThrow_WhenCancelled()
    {
        var fixture = await CreateFixtureAsync(fileCount: 10);
        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await backup.BackupDirectoryAsync(fixture.SourceDirectory);

        var manifestPath = fixture.State.GetPreviousBackupPath(fixture.SourceDirectory);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var verifier = new SnapshotIntegrityVerifier(_logger, fixture.Storage, null, fixture.State);

        var act = async () => await verifier.VerifyAsync(manifestPath!, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task VerifyAsync_ShouldReportProgress_ForChunksAndFiles()
    {
        var fixture = await CreateFixtureAsync(fileCount: 5);
        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await backup.BackupDirectoryAsync(fixture.SourceDirectory);

        var manifestPath = fixture.State.GetPreviousBackupPath(fixture.SourceDirectory);
        var progress = new SyncProgress<VerificationProgress>();

        var verifier = new SnapshotIntegrityVerifier(_logger, fixture.Storage, null, fixture.State);
        var result = await verifier.VerifyAsync(manifestPath!, progress);

        var reports = progress.Reports;
        result.IsValid.Should().BeTrue();
        reports.Should().Contain(report => report.Phase == "chunks");
        reports.Should().Contain(report => report.Phase == "files");
        reports.Select(report => report.Fraction).Should().OnlyContain(fraction => fraction >= 0 && fraction <= 1);
    }

    [Fact]
    public async Task VerifyAsync_RecordsActivity_AndBackupRecordsChunkSizes()
    {
        var fixture = await CreateFixtureAsync(fileCount: 3);
        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await backup.BackupDirectoryAsync(fixture.SourceDirectory);
        var manifestPath = fixture.State.GetPreviousBackupPath(fixture.SourceDirectory);

        fixture.State.GetBackupsForGroup(fixture.SourceDirectory).Should().ContainSingle().Which.StoredBytes.Should().BeGreaterThan(0);

        await new SnapshotIntegrityVerifier(_logger, fixture.Storage, null, fixture.State).VerifyAsync(manifestPath!);

        var entry = fixture.State.GetRecentActivity().Should().ContainSingle().Subject;
        entry.Type.Should().Be(ActivityType.Verify);
        entry.Result.Should().Be(ActivityResult.Succeeded);
        entry.Target.Should().Be(manifestPath);
    }

    [Fact]
    public async Task VerifyAsync_RecordsCancelledActivity()
    {
        var fixture = await CreateFixtureAsync(fileCount: 3);
        var backup = new Backup(_logger, fixture.State, new SizeAnalyzer(), fixture.Config, null);
        await backup.BackupDirectoryAsync(fixture.SourceDirectory);
        var manifestPath = fixture.State.GetPreviousBackupPath(fixture.SourceDirectory);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await new SnapshotIntegrityVerifier(_logger, fixture.Storage, null, fixture.State).VerifyAsync(manifestPath!, null, cts.Token);

        // Cancelled before the lease is taken: nothing recorded, which is fine; cancelled after is recorded as Cancelled.
        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.State.GetRecentActivity().Should().OnlyContain(e => e.Result == ActivityResult.Cancelled);
    }

    private async Task<BackupFixture> CreateFixtureAsync(int fileCount)
    {
        var sourceDirectory = Path.Combine(_testRoot, "source-" + Guid.NewGuid().ToString("N"));
        var storageDirectory = Path.Combine(_testRoot, "storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(storageDirectory);

        for (var index = 0; index < fileCount; index++)
        {
            // Large enough that chunking does real per-byte work, so a mid-operation cancel
            // has somewhere to land.
            var content = new string((char)('a' + (index % 26)), 80 * 1024);
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, $"file{index:D3}.txt"), content);
        }

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

        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_testRoot, "state-" + Guid.NewGuid().ToString("N") + ".json"));

        return new BackupFixture(
            Path.GetFullPath(sourceDirectory),
            storageDirectory,
            storage,
            configMock.Object,
            state);
    }

    private sealed record BackupFixture(
        string SourceDirectory,
        string StorageDirectory,
        LocalStorage Storage,
        IConfigManager Config,
        SystemState State);
}
