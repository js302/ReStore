using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;
using System.Text.Json;

namespace ReStore.Tests;

public class RestoreTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger;

    public RestoreTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreRestoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _logger = new TestLogger();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenBackupPathIsEmpty()
    {
        var storage = new Mock<IStorage>();
        var restore = new Restore(_logger, storage.Object);

        var action = () => restore.RestoreFromBackupAsync(" ", Path.Combine(_testRoot, "restore"));

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Backup path cannot be null or empty*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenTargetDirectoryIsEmpty()
    {
        var storage = new Mock<IStorage>();
        var restore = new Restore(_logger, storage.Object);

        var action = () => restore.RestoreFromBackupAsync("backups/sample.zip", " ");

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Target directory cannot be null or empty*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldPropagateFileNotFound_WhenDownloadFails()
    {
        var storage = new Mock<IStorage>();
        storage.Setup(s => s.DownloadAsync("snapshots/missing.manifest.json", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("missing backup"));

        var restore = new Restore(_logger, storage.Object);

        var action = () => restore.RestoreFromBackupAsync("snapshots/missing.manifest.json", Path.Combine(_testRoot, "restore"));

        await action.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenReferencedChunkIsMissing()
    {
        var (storage, remotePath, storageRoot) = await CreateSnapshotBackupAsync(encrypted: false);

        var manifestPath = Path.Combine(storageRoot, remotePath.Replace('/', Path.DirectorySeparatorChar));
        var manifest = ManifestIo.Read(manifestPath);
        manifest.Should().NotBeNull();
        manifest!.Files.Should().NotBeEmpty();

        var firstChunkId = manifest.Files.SelectMany(file => file.Chunks).Select(chunk => chunk.ChunkId).First();
        var missingChunkPath = Path.Combine(
            storageRoot,
            SnapshotStoragePaths.GetChunkPath(firstChunkId, manifest.ChunkStorageNamespace).Replace('/', Path.DirectorySeparatorChar));
        File.Exists(missingChunkPath).Should().BeTrue();
        File.Delete(missingChunkPath);

        var restore = new Restore(_logger, storage);

        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-missing-chunk"));

        await action.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenEncryptedSnapshotHasNoPasswordProvider()
    {
        var (storage, remotePath, _) = await CreateSnapshotBackupAsync(encrypted: true, password: "CorrectPassword123!");
        var restore = new Restore(_logger, storage);

        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-no-provider"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no password provider available*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenPasswordProviderReturnsEmptyPasswordForEncryptedSnapshot()
    {
        var (storage, remotePath, _) = await CreateSnapshotBackupAsync(encrypted: true, password: "CorrectPassword123!");
        var passwordProvider = new Mock<IPasswordProvider>();
        passwordProvider.Setup(p => p.GetPasswordAsync()).ReturnsAsync((string?)null);

        var restore = new Restore(_logger, storage, passwordProvider.Object);

        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-empty-password"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Password required to decrypt snapshot*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldClearPassword_WhenSnapshotDecryptionFails()
    {
        var (storage, remotePath, _) = await CreateSnapshotBackupAsync(encrypted: true, password: "CorrectPassword123!");
        var passwordProvider = new Mock<IPasswordProvider>();
        passwordProvider.Setup(p => p.GetPasswordAsync()).ReturnsAsync("WrongPassword!");

        var restore = new Restore(_logger, storage, passwordProvider.Object);

        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-wrong-password"));

        await action.Should().ThrowAsync<System.Security.Cryptography.CryptographicException>();

        passwordProvider.Verify(p => p.ClearPassword(), Times.Once);
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenManifestHashIsCorrupted()
    {
        var (storage, remotePath, storageRoot) = await CreateSnapshotBackupAsync(encrypted: false);

        var manifestPath = Path.Combine(storageRoot, remotePath.Replace('/', Path.DirectorySeparatorChar));
        var manifest = ManifestIo.Read(manifestPath);
        manifest.Should().NotBeNull();
        manifest!.RootHash = "deadbeef";
        await ManifestIo.WriteAsync(manifestPath, manifest);

        var restore = new Restore(_logger, storage);
        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-corrupt-manifest"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Manifest integrity check failed*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenHeadRootHashDoesNotMatchManifest()
    {
        var (storage, remotePath, storageRoot) = await CreateSnapshotBackupAsync(encrypted: false);

        var manifestPath = Path.Combine(storageRoot, remotePath.Replace('/', Path.DirectorySeparatorChar));
        var manifest = ManifestIo.Read(manifestPath);
        manifest.Should().NotBeNull();

        var headPath = SnapshotStoragePaths.GetHeadPath(manifest!.Group);
        var localHeadPath = Path.Combine(storageRoot, headPath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(localHeadPath, $"{remotePath}\ninvalid-head-root\n");

        var restore = new Restore(_logger, storage);
        var action = () => restore.RestoreFromBackupAsync(headPath, Path.Combine(_testRoot, "restore-head-root-mismatch"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*HEAD root hash mismatch*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldReject_PathTraversalEntriesInManifest()
    {
        var (storage, remotePath, storageRoot) = await CreateSnapshotBackupAsync(encrypted: false);

        var manifestPath = Path.Combine(storageRoot, remotePath.Replace('/', Path.DirectorySeparatorChar));
        var manifest = ManifestIo.Read(manifestPath);
        manifest.Should().NotBeNull();
        manifest!.Files.Should().NotBeEmpty();

        manifest.Files[0].RelativePath = "../escape.txt";
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);
        await ManifestIo.WriteAsync(manifestPath, manifest);

        var restoreDirectory = Path.Combine(_testRoot, "restore-traversal");
        var escapedPath = Path.GetFullPath(Path.Combine(restoreDirectory, "..", "escape.txt"));

        var restore = new Restore(_logger, storage);
        var action = () => restore.RestoreFromBackupAsync(remotePath, restoreDirectory);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resolves outside the restore target directory*");
        File.Exists(escapedPath).Should().BeFalse();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldReject_InvalidChunkIdentifiers()
    {
        var (storage, remotePath, storageRoot) = await CreateSnapshotBackupAsync(encrypted: false);

        var manifestPath = Path.Combine(storageRoot, remotePath.Replace('/', Path.DirectorySeparatorChar));
        var manifest = ManifestIo.Read(manifestPath);
        manifest.Should().NotBeNull();

        var firstChunk = manifest!.Files.SelectMany(file => file.Chunks).First();
        firstChunk.ChunkId = "bad/chunk-id";
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);
        await ManifestIo.WriteAsync(manifestPath, manifest);

        var restore = new Restore(_logger, storage);
        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-invalid-chunk-id"));

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Chunk id*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldRestoreLatestSnapshot_AfterEncryptionPasswordRotation()
    {
        var sourceDir = Path.Combine(_testRoot, "rotation-source");
        var storageDir = Path.Combine(_testRoot, "rotation-storage");
        var restoreDir = Path.Combine(_testRoot, "rotation-restore");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(storageDir);

        var changingFile = Path.Combine(sourceDir, "alpha.txt");
        var stableFile = Path.Combine(sourceDir, "beta.txt");
        await File.WriteAllTextAsync(changingFile, "alpha-v1");
        await File.WriteAllTextAsync(stableFile, "beta-v1");

        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDir });

        var encryptionConfig = new EncryptionConfig
        {
            Enabled = true,
            Salt = Convert.ToBase64String(EncryptionService.GenerateSalt()),
            KeyDerivationIterations = 1000
        };

        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        configMock.SetupGet(c => c.GlobalStorageType).Returns("local");
        configMock.SetupGet(c => c.SizeThresholdMB).Returns(100);
        configMock.SetupGet(c => c.ExcludedPatterns).Returns([]);
        configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        configMock.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        configMock.SetupGet(c => c.WatchDirectories).Returns([]);
        configMock.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { ManifestVersion = 2 }); // legacy (v2) encrypted behaviour
        configMock.SetupGet(c => c.Encryption).Returns(() => encryptionConfig);
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_testRoot, "rotation-state.json"));

        var firstBackup = new Backup(_logger, state, new SizeAnalyzer(), configMock.Object, new StaticPasswordProvider("Password-One-123!"));
        await firstBackup.BackupDirectoryAsync(sourceDir);

        await File.WriteAllTextAsync(changingFile, "alpha-v2");

        var secondBackup = new Backup(_logger, state, new SizeAnalyzer(), configMock.Object, new StaticPasswordProvider("Password-Two-456!"));
        await secondBackup.BackupDirectoryAsync(sourceDir);

        var manifests = state.GetBackupsForGroup(sourceDir)
            .OrderByDescending(entry => entry.Timestamp)
            .Take(2)
            .ToList();

        manifests.Should().HaveCount(2);

        var latestManifestPath = manifests[0].Path;
        var previousManifestPath = manifests[1].Path;

        var latestManifestLocalPath = Path.Combine(storageDir, latestManifestPath.Replace('/', Path.DirectorySeparatorChar));
        var previousManifestLocalPath = Path.Combine(storageDir, previousManifestPath.Replace('/', Path.DirectorySeparatorChar));

        var latestManifest = ManifestIo.Read(latestManifestLocalPath);
        var previousManifest = ManifestIo.Read(previousManifestLocalPath);

        latestManifest.Should().NotBeNull();
        previousManifest.Should().NotBeNull();
        latestManifest!.ChunkStorageNamespace.Should().NotBeNullOrWhiteSpace();
        previousManifest!.ChunkStorageNamespace.Should().NotBeNullOrWhiteSpace();
        latestManifest.ChunkStorageNamespace.Should().NotBe(previousManifest.ChunkStorageNamespace);

        var restore = new Restore(_logger, storage, new StaticPasswordProvider("Password-Two-456!"));
        await restore.RestoreFromBackupAsync(latestManifestPath, restoreDir);

        (await File.ReadAllTextAsync(Path.Combine(restoreDir, "alpha.txt"))).Should().Be("alpha-v2");
        (await File.ReadAllTextAsync(Path.Combine(restoreDir, "beta.txt"))).Should().Be("beta-v1");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldNotAccumulateChunkTempFiles()
    {
        var (storage, remotePath, _) = await CreateSnapshotBackupAsync(encrypted: false, multiChunk: true);
        var probe = new ChunkTempProbingStorage(storage);
        var scratch = Path.Combine(_testRoot, "scratch-" + Guid.NewGuid().ToString("N"));
        var restoreDir = Path.Combine(_testRoot, "restore-temp-probe");

        await new Restore(_logger, probe, tempRoot: scratch).RestoreFromBackupAsync(remotePath, restoreDir);

        probe.Downloads.Should().BeGreaterThan(2);
        probe.PeakChunkFileCount.Should().BeLessThanOrEqualTo(1);
        new FileInfo(Path.Combine(restoreDir, "big.bin")).Length.Should().Be(2 * 1024 * 1024);
        Directory.GetFileSystemEntries(scratch).Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldBoundChunkTempFilesByMaxConcurrency_WhenDownloadingInParallel()
    {
        var (storage, remotePath, _) = await CreateSnapshotBackupAsync(encrypted: false, multiChunk: true);
        var probe = new ChunkTempProbingStorage(storage, maxConcurrency: 3);
        var scratch = Path.Combine(_testRoot, "scratch-" + Guid.NewGuid().ToString("N"));
        var restoreDir = Path.Combine(_testRoot, "restore-temp-probe-parallel");

        await new Restore(_logger, probe, tempRoot: scratch).RestoreFromBackupAsync(remotePath, restoreDir);

        probe.Downloads.Should().BeGreaterThan(3);
        probe.PeakChunkFileCount.Should().BeLessThanOrEqualTo(3);
        Directory.GetFileSystemEntries(scratch).Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldReportWrongPassword_WhenDownloadingInParallel()
    {
        // Decryption happens in manifest order even though downloads run ahead, so the first chunk's failure is
        // always "wrong password" and no more than a window of downloads was started.
        var (storage, remotePath, _) = await CreateSnapshotBackupAsync(encrypted: true, password: "CorrectPassword123!", multiChunk: true);
        var passwordProvider = new Mock<IPasswordProvider>();
        passwordProvider.Setup(p => p.GetPasswordAsync()).ReturnsAsync("WrongPassword!");
        var probe = new ChunkTempProbingStorage(storage, maxConcurrency: 4);

        var restore = new Restore(_logger, probe, passwordProvider.Object);
        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-wrong-parallel"));

        await action.Should().ThrowAsync<System.Security.Cryptography.CryptographicException>()
            .WithMessage("*Wrong password*");
        passwordProvider.Verify(p => p.ClearPassword(), Times.Once);
        probe.Downloads.Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldStopAtFirstChunkWithOneWrongPasswordError()
    {
        var (storage, remotePath, _) = await CreateSnapshotBackupAsync(encrypted: true, password: "CorrectPassword123!", multiChunk: true);
        var passwordProvider = new Mock<IPasswordProvider>();
        passwordProvider.Setup(p => p.GetPasswordAsync()).ReturnsAsync("WrongPassword!");
        var probe = new ChunkTempProbingStorage(storage);

        var restore = new Restore(_logger, probe, passwordProvider.Object);
        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-wrong-multi"));

        await action.Should().ThrowAsync<System.Security.Cryptography.CryptographicException>()
            .WithMessage("*Wrong password*");
        passwordProvider.Verify(p => p.ClearPassword(), Times.Once);
        probe.Downloads.Should().Be(1);
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldReportCorruptedChunk_NotWrongPassword_WhenLaterChunkIsTampered()
    {
        var (storage, remotePath, root) = await CreateSnapshotBackupAsync(encrypted: true, password: "CorrectPassword123!", multiChunk: true);
        // Tamper the LAST chunk of big.bin: whatever order files are restored in, at least one chunk
        // (big.bin's first) decrypts before it, so this must be reported as corruption, not a wrong password.
        var manifestFile = Directory.GetFiles(root, "*.manifest.json", SearchOption.AllDirectories).Single();
        var manifest = ManifestIo.Read(manifestFile)!;
        var big = manifest.Files.Single(file => file.RelativePath.EndsWith("big.bin", StringComparison.OrdinalIgnoreCase));
        big.Chunks.Count.Should().BeGreaterThan(1);
        var victim = Path.Combine(
            root,
            SnapshotStoragePaths.GetChunkPath(big.Chunks[^1].ChunkId, manifest.ChunkStorageNamespace).Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(victim);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(victim, bytes);

        var passwordProvider = new Mock<IPasswordProvider>();
        passwordProvider.Setup(p => p.GetPasswordAsync()).ReturnsAsync("CorrectPassword123!");
        var restore = new Restore(_logger, storage, passwordProvider.Object);

        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-tampered"));

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Corrupted chunk*");
        passwordProvider.Verify(p => p.ClearPassword(), Times.Never);
    }

    /// <summary>Counts chunk downloads and the chunk temp files staged at once. Sequential (MaxConcurrency 1) unless told otherwise.</summary>
    private sealed class ChunkTempProbingStorage(IStorage inner, int maxConcurrency = 1) : DelegatingStorage(inner)
    {
        private readonly Lock _gate = new();
        private int _peak;
        private int _downloads;

        public int PeakChunkFileCount { get { lock (_gate) { return _peak; } } }
        public int Downloads { get { lock (_gate) { return _downloads; } } }

        public override int MaxConcurrency => maxConcurrency;

        public override async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
        {
            await Inner.DownloadAsync(remotePath, localPath, ct);

            if (!localPath.EndsWith(".chunk", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (_gate)
            {
                _downloads++;
                var staged = Directory.GetFiles(Path.GetDirectoryName(localPath)!, "*.chunk").Length;
                _peak = Math.Max(_peak, staged);
            }
        }

        public override void Dispose() => Inner.Dispose();
    }

    private async Task<(LocalStorage Storage, string RemotePath, string StorageRoot)> CreateSnapshotBackupAsync(bool encrypted, string password = "", bool multiChunk = false)
    {
        var sourceDir = Path.Combine(_testRoot, "source-" + Guid.NewGuid().ToString("N"));
        var storageDir = Path.Combine(_testRoot, "storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(storageDir);

        var sourceFile = Path.Combine(sourceDir, "data.txt");
        await File.WriteAllTextAsync(sourceFile, "secret content");
        if (multiChunk)
        {
            await File.WriteAllBytesAsync(Path.Combine(sourceDir, "big.bin"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        }

        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDir });

        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        configMock.SetupGet(c => c.GlobalStorageType).Returns("local");
        configMock.SetupGet(c => c.SizeThresholdMB).Returns(100);
        configMock.SetupGet(c => c.ExcludedPatterns).Returns([]);
        configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        configMock.SetupGet(c => c.BackupType).Returns(BackupType.Full);
        configMock.SetupGet(c => c.WatchDirectories).Returns([]);
        configMock.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { ManifestVersion = 2 }); // legacy (v2) encrypted behaviour
        configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig
        {
            Enabled = encrypted,
            Salt = encrypted ? Convert.ToBase64String(EncryptionService.GenerateSalt()) : null,
            KeyDerivationIterations = 1000
        });
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_testRoot, "state-" + Guid.NewGuid().ToString("N") + ".json"));

        IPasswordProvider? backupPasswordProvider = encrypted ? new StaticPasswordProvider(password) : null;
        var backup = new Backup(_logger, state, new SizeAnalyzer(), configMock.Object, backupPasswordProvider);
        await backup.BackupDirectoryAsync(sourceDir);

        var remotePath = state.GetPreviousBackupPath(sourceDir);
        remotePath.Should().NotBeNullOrWhiteSpace();

        return (storage, remotePath!, storageDir);
    }
}
