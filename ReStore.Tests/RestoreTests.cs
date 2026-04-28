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
        storage.Setup(s => s.DownloadAsync("snapshots/missing.manifest.json", It.IsAny<string>()))
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
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(await File.ReadAllTextAsync(manifestPath));
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
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(await File.ReadAllTextAsync(manifestPath));
        manifest.Should().NotBeNull();
        manifest!.RootHash = "deadbeef";
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

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
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(await File.ReadAllTextAsync(manifestPath));
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
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(await File.ReadAllTextAsync(manifestPath));
        manifest.Should().NotBeNull();
        manifest!.Files.Should().NotBeEmpty();

        manifest.Files[0].RelativePath = "../escape.txt";
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

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
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(await File.ReadAllTextAsync(manifestPath));
        manifest.Should().NotBeNull();

        var firstChunk = manifest!.Files.SelectMany(file => file.Chunks).First();
        firstChunk.ChunkId = "bad/chunk-id";
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

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
        configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
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

        var latestManifest = JsonSerializer.Deserialize<SnapshotManifest>(await File.ReadAllTextAsync(latestManifestLocalPath));
        var previousManifest = JsonSerializer.Deserialize<SnapshotManifest>(await File.ReadAllTextAsync(previousManifestLocalPath));

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

    private async Task<(LocalStorage Storage, string RemotePath, string StorageRoot)> CreateSnapshotBackupAsync(bool encrypted, string password = "")
    {
        var sourceDir = Path.Combine(_testRoot, "source-" + Guid.NewGuid().ToString("N"));
        var storageDir = Path.Combine(_testRoot, "storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(storageDir);

        var sourceFile = Path.Combine(sourceDir, "data.txt");
        await File.WriteAllTextAsync(sourceFile, "secret content");

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
        configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
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
