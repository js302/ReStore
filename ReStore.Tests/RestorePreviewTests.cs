using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Covers restore preview, overwrite protection and subset selection. The overwrite cases
/// are the sharpest edge in the product: a wrong answer destroys a user's real file.
/// </summary>
public class RestorePreviewTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger = new();

    public RestorePreviewTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStorePreviewTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task PreviewRestoreAsync_ShouldMatchManifestCounts_WithoutDownloadingChunks()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["alpha.txt"] = "alpha",
            ["beta.txt"] = "beta",
            ["nested/gamma.txt"] = "gamma"
        });

        var storageSpy = new CountingStorage(fixture.Storage);
        var restore = new Restore(_logger, storageSpy, null, fixture.State);

        var preview = await restore.PreviewRestoreAsync(fixture.ManifestPath, Path.Combine(_testRoot, "preview-target"));

        preview.FileCount.Should().Be(3);
        preview.TotalBytes.Should().Be("alpha".Length + "beta".Length + "gamma".Length);
        preview.ExistingFileCount.Should().Be(0);
        preview.HasConflicts.Should().BeFalse();
        preview.SnapshotId.Should().NotBeNullOrWhiteSpace();

        storageSpy.ChunkDownloads.Should().Be(0, "a preview reads the manifest only");
    }

    [Fact]
    public async Task PreviewRestoreAsync_ShouldDistinguishIdenticalFromDiffering()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["same.txt"] = "unchanged",
            ["different.txt"] = "snapshot-version",
            ["absent.txt"] = "only-in-snapshot"
        });

        var target = Path.Combine(_testRoot, "mixed-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "same.txt"), "unchanged");
        await File.WriteAllTextAsync(Path.Combine(target, "different.txt"), "local-edit-that-must-not-be-lost");

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        var preview = await restore.PreviewRestoreAsync(fixture.ManifestPath, target);

        preview.IdenticalFileCount.Should().Be(1);
        preview.DifferingFileCount.Should().Be(1);
        preview.ExistingFileCount.Should().Be(2);
        preview.HasConflicts.Should().BeTrue();

        var differing = preview.Entries.Single(entry => entry.Conflict == RestoreConflictKind.Differs);
        differing.RelativePath.Should().Be("different.txt");
        differing.ExistingSizeBytes.Should().Be("local-edit-that-must-not-be-lost".Length);
        differing.SizeBytes.Should().Be("snapshot-version".Length);
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithSkip_ShouldLeaveExistingFileByteForByteIntact()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["precious.txt"] = "snapshot-content",
            ["fresh.txt"] = "new-file"
        });

        var target = Path.Combine(_testRoot, "skip-target");
        Directory.CreateDirectory(target);

        var preciousPath = Path.Combine(target, "precious.txt");
        const string localContent = "the user's real work that must survive";
        await File.WriteAllTextAsync(preciousPath, localContent);
        var originalBytes = await File.ReadAllBytesAsync(preciousPath);

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        var outcome = await restore.RestoreFromBackupAsync(
            fixture.ManifestPath,
            target,
            new RestoreOptions { ConflictPolicy = RestoreConflictPolicy.Skip });

        (await File.ReadAllBytesAsync(preciousPath)).Should().Equal(originalBytes,
            "Skip must not modify the existing file at all");

        outcome.FilesSkipped.Should().Be(1);
        outcome.SkippedRelativePaths.Should().Contain("precious.txt");

        // The non-conflicting file is still restored.
        outcome.FilesRestored.Should().Be(1);
        (await File.ReadAllTextAsync(Path.Combine(target, "fresh.txt"))).Should().Be("new-file");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithKeepBoth_ShouldProduceBothFilesAndNotCollideOnRepeatRuns()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["notes.txt"] = "snapshot-notes"
        });

        var target = Path.Combine(_testRoot, "keepboth-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "notes.txt"), "local-notes");

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        var options = new RestoreOptions { ConflictPolicy = RestoreConflictPolicy.KeepBoth };

        var first = await restore.RestoreFromBackupAsync(fixture.ManifestPath, target, options);

        first.FilesKeptBoth.Should().Be(1);
        (await File.ReadAllTextAsync(Path.Combine(target, "notes.txt"))).Should().Be("local-notes");
        (await File.ReadAllTextAsync(Path.Combine(target, "notes (restored).txt"))).Should().Be("snapshot-notes");

        var second = await restore.RestoreFromBackupAsync(fixture.ManifestPath, target, options);

        second.FilesKeptBoth.Should().Be(1);
        File.Exists(Path.Combine(target, "notes (restored 2).txt")).Should().BeTrue(
            "a repeat run must not collide with the copy the first run wrote");
        (await File.ReadAllTextAsync(Path.Combine(target, "notes.txt"))).Should().Be("local-notes");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithOverwrite_ShouldReplaceExistingFile()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["data.txt"] = "snapshot-content"
        });

        var target = Path.Combine(_testRoot, "overwrite-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "data.txt"), "stale-content");

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        var outcome = await restore.RestoreFromBackupAsync(
            fixture.ManifestPath,
            target,
            new RestoreOptions { ConflictPolicy = RestoreConflictPolicy.Overwrite });

        (await File.ReadAllTextAsync(Path.Combine(target, "data.txt"))).Should().Be("snapshot-content");
        outcome.FilesOverwritten.Should().Be(1);
        outcome.FilesRestored.Should().Be(1);
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithFail_ShouldAbortBeforeWritingAnything()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["conflict.txt"] = "snapshot",
            ["clean.txt"] = "would-be-written"
        });

        var target = Path.Combine(_testRoot, "fail-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "conflict.txt"), "local");

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);

        var act = async () => await restore.RestoreFromBackupAsync(
            fixture.ManifestPath,
            target,
            new RestoreOptions { ConflictPolicy = RestoreConflictPolicy.Fail });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*would replace*");

        (await File.ReadAllTextAsync(Path.Combine(target, "conflict.txt"))).Should().Be("local");
        File.Exists(Path.Combine(target, "clean.txt")).Should().BeFalse(
            "Fail aborts before the write loop, so no file is created");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithDryRun_ShouldWriteNothing()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["one.txt"] = "1",
            ["two.txt"] = "2"
        });

        var target = Path.Combine(_testRoot, "dryrun-target");

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        var outcome = await restore.RestoreFromBackupAsync(
            fixture.ManifestPath,
            target,
            new RestoreOptions { DryRun = true });

        outcome.FilesRestored.Should().Be(0);
        Directory.Exists(target).Should().BeFalse(
            "a dry run must leave the filesystem exactly as it found it, including not creating the target");
    }

    [Fact]
    public async Task PreviewRestoreAsync_ShouldNotCreateTargetDirectory()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string> { ["a.txt"] = "a" });

        var target = Path.Combine(_testRoot, "never-created-target");

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        var preview = await restore.PreviewRestoreAsync(fixture.ManifestPath, target);

        preview.FileCount.Should().Be(1);
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldRestoreOnlySelectedPaths()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["keep.txt"] = "wanted",
            ["skip.txt"] = "unwanted",
            ["nested/also-keep.txt"] = "also-wanted"
        });

        var target = Path.Combine(_testRoot, "subset-target");

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        var outcome = await restore.RestoreFromBackupAsync(
            fixture.ManifestPath,
            target,
            new RestoreOptions
            {
                RelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "keep.txt",
                    "nested/also-keep.txt"
                }
            });

        outcome.FilesRestored.Should().Be(2);
        File.Exists(Path.Combine(target, "keep.txt")).Should().BeTrue();
        File.Exists(Path.Combine(target, "nested", "also-keep.txt")).Should().BeTrue();
        File.Exists(Path.Combine(target, "skip.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldRecordSelectedCountInTelemetry_NotWholeManifest()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["a.txt"] = "a",
            ["b.txt"] = "b",
            ["c.txt"] = "c",
            ["d.txt"] = "d"
        });

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        await restore.RestoreFromBackupAsync(
            fixture.ManifestPath,
            Path.Combine(_testRoot, "telemetry-target"),
            new RestoreOptions
            {
                RelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.txt", "b.txt" }
            });

        var restoreTelemetry = fixture.State.Telemetry.Restore;

        restoreTelemetry.FilesExpected.Should().Be(2,
            "a subset restore must not report a success ratio against files it was never asked to write");
        restoreTelemetry.FilesRestored.Should().Be(2);
        restoreTelemetry.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task PreviewRestoreAsync_ShouldReportFilteredOutCount()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["x.txt"] = "x",
            ["y.txt"] = "y",
            ["z.txt"] = "z"
        });

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        var preview = await restore.PreviewRestoreAsync(
            fixture.ManifestPath,
            Path.Combine(_testRoot, "filtered-target"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "y.txt" });

        preview.FileCount.Should().Be(1);
        preview.FilesFilteredOut.Should().Be(2);
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldStillRejectTraversal_DuringPreview()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string> { ["ok.txt"] = "ok" });

        var manifestLocalPath = Path.Combine(
            fixture.StorageDirectory,
            fixture.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        var manifest = ManifestIo.Read(manifestLocalPath);

        manifest!.Files[0].RelativePath = "../escape.txt";
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);
        await ManifestIo.WriteAsync(manifestLocalPath, manifest);

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);

        var act = async () => await restore.PreviewRestoreAsync(
            fixture.ManifestPath,
            Path.Combine(_testRoot, "traversal-preview"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resolves outside the restore target directory*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldRecordTelemetryExactlyOnce_OnSuccess()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string> { ["a.txt"] = "a" });

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        await restore.RestoreFromBackupAsync(
            fixture.ManifestPath,
            Path.Combine(_testRoot, "telemetry-once-target"));

        fixture.State.Telemetry.Restore.AttemptCount.Should().Be(1);
        fixture.State.Telemetry.Restore.SuccessCount.Should().Be(1);
        fixture.State.Telemetry.Restore.ValidationFailureCount.Should().Be(0);
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldRecordTelemetryExactlyOnce_OnFailure()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string> { ["a.txt"] = "a" });

        // Remove a chunk so the restore fails partway through fetching.
        var chunkFiles = Directory.GetFiles(
            Path.Combine(fixture.StorageDirectory, "chunks"), "*.chunk", SearchOption.AllDirectories);
        chunkFiles.Should().NotBeEmpty();
        File.Delete(chunkFiles[0]);

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);

        var act = async () => await restore.RestoreFromBackupAsync(
            fixture.ManifestPath,
            Path.Combine(_testRoot, "telemetry-fail-target"));

        await act.Should().ThrowAsync<FileNotFoundException>();

        fixture.State.Telemetry.Restore.AttemptCount.Should().Be(1, "a failure must be recorded once, not twice");
        fixture.State.Telemetry.Restore.SuccessCount.Should().Be(0);
        fixture.State.Telemetry.Restore.FailureCategoryCounts.Should().ContainKey("missing-artifact");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldNotRecordTelemetry_WhenCancelled()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string> { ["a.txt"] = "a" });

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);

        var act = async () => await restore.RestoreFromBackupAsync(
            fixture.ManifestPath,
            Path.Combine(_testRoot, "cancel-telemetry-target"),
            RestoreOptions.Default,
            null,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        fixture.State.Telemetry.Restore.AttemptCount.Should().Be(0,
            "a cancellation is not a restore failure and must not poison failure telemetry");
        fixture.State.Telemetry.Restore.FailureCategoryCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldLeaveNoPartialFile_WhenChunkIsMissing()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string> { ["a.txt"] = "a" });

        var chunkFiles = Directory.GetFiles(
            Path.Combine(fixture.StorageDirectory, "chunks"), "*.chunk", SearchOption.AllDirectories);
        File.Delete(chunkFiles[0]);

        var target = Path.Combine(_testRoot, "partial-cleanup-target");
        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);

        var act = async () => await restore.RestoreFromBackupAsync(fixture.ManifestPath, target);
        await act.Should().ThrowAsync<FileNotFoundException>();

        Directory.GetFiles(target, "*.restorepartial", SearchOption.AllDirectories)
            .Should().BeEmpty("the partial file must be cleaned up on the failure path");
        File.Exists(Path.Combine(target, "a.txt")).Should().BeFalse(
            "a file whose chunks could not be fetched must not appear at its real path");
    }

    [Fact]
    public async Task RestoreOptionsDefault_ShouldSkipConflicts_SoExistingWorkSurvives()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["precious.txt"] = "snapshot-content"
        });

        var target = Path.Combine(_testRoot, "default-policy-target");
        Directory.CreateDirectory(target);

        var preciousPath = Path.Combine(target, "precious.txt");
        const string localContent = "the user's real work that must survive";
        await File.WriteAllTextAsync(preciousPath, localContent);

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        var outcome = await restore.RestoreFromBackupAsync(fixture.ManifestPath, target, RestoreOptions.Default);

        // The default must not destroy user data: it matches the CLI's documented
        // "--conflict skip" default rather than the historical unconditional overwrite.
        (await File.ReadAllTextAsync(preciousPath)).Should().Be(localContent);
        outcome.FilesSkipped.Should().Be(1);
        outcome.FilesOverwritten.Should().Be(0);
    }

    [Fact]
    public async Task RestoreOptionsOverwrite_ShouldReplaceExistingFile()
    {
        var fixture = await CreateSnapshotAsync(new Dictionary<string, string>
        {
            ["precious.txt"] = "snapshot-content"
        });

        var target = Path.Combine(_testRoot, "explicit-overwrite-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "precious.txt"), "stale local copy");

        var restore = new Restore(_logger, fixture.Storage, null, fixture.State);
        var outcome = await restore.RestoreFromBackupAsync(fixture.ManifestPath, target, RestoreOptions.Overwrite);

        (await File.ReadAllTextAsync(Path.Combine(target, "precious.txt"))).Should().Be("snapshot-content");
        outcome.FilesOverwritten.Should().Be(1);
    }

    private async Task<SnapshotFixture> CreateSnapshotAsync(Dictionary<string, string> files)    {
        var sourceDirectory = Path.Combine(_testRoot, "source-" + Guid.NewGuid().ToString("N"));
        var storageDirectory = Path.Combine(_testRoot, "storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(storageDirectory);

        foreach (var (relativePath, content) in files)
        {
            var absolutePath = Path.Combine(sourceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllTextAsync(absolutePath, content);
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

        var backup = new Backup(_logger, state, new SizeAnalyzer(), configMock.Object, null);
        await backup.BackupDirectoryAsync(sourceDirectory);

        var manifestPath = state.GetPreviousBackupPath(Path.GetFullPath(sourceDirectory));
        manifestPath.Should().NotBeNullOrWhiteSpace();

        return new SnapshotFixture(storage, manifestPath!, storageDirectory, state);
    }

    private sealed record SnapshotFixture(
        LocalStorage Storage,
        string ManifestPath,
        string StorageDirectory,
        SystemState State);

    /// <summary>Counts chunk fetches so a preview can be shown to make none.</summary>
    private sealed class CountingStorage(LocalStorage inner) : DelegatingStorage(inner)
    {
        public int ChunkDownloads { get; private set; }

        public override Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
        {
            if (remotePath.Contains("/chunks/", StringComparison.OrdinalIgnoreCase)
                || remotePath.StartsWith("chunks/", StringComparison.OrdinalIgnoreCase))
            {
                ChunkDownloads++;
            }

            return Inner.DownloadAsync(remotePath, localPath, ct);
        }

        public override void Dispose() => Inner.Dispose();
    }
}
