using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.monitoring;
using FluentAssertions;
using System.Text.Json;

namespace ReStore.Tests;

public class BackupTests : IDisposable
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly Mock<IConfigManager> _configMock;
    private readonly Mock<SystemState> _stateMock;
    private readonly Mock<SizeAnalyzer> _sizeAnalyzerMock;
    private readonly Mock<IStorage> _storageMock;
    private readonly string _testDir;

    public BackupTests()
    {
        _loggerMock = new Mock<ILogger>();
        _configMock = new Mock<IConfigManager>();
        _stateMock = new Mock<SystemState>(_loggerMock.Object);
        _sizeAnalyzerMock = new Mock<SizeAnalyzer>();
        _storageMock = new Mock<IStorage>();

        _configMock.Setup(c => c.Retention)
            .Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        _configMock.Setup(c => c.WatchDirectories)
            .Returns(new List<WatchDirectoryConfig>());
        _configMock.Setup(c => c.ExcludedPaths)
            .Returns(new List<string>());
        _configMock.Setup(c => c.ExcludedPatterns)
            .Returns(new List<string>());
        _configMock.Setup(c => c.MaxFileSizeMB)
            .Returns(100);
        _configMock.Setup(c => c.BackupType)
            .Returns(BackupType.Incremental);
        _configMock.Setup(c => c.SizeThresholdMB)
            .Returns(100);
        _configMock.Setup(c => c.Encryption)
            .Returns(new EncryptionConfig { Enabled = false });
        _configMock.Setup(c => c.GlobalStorageType)
            .Returns("local");
        _configMock.SetupGet(c => c.ChunkDiffing)
            .Returns(new ChunkDiffingConfig());

        _testDir = Path.Combine(Path.GetTempPath(), "ReStoreTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task BackupDirectoryAsync_ShouldUploadFiles_WhenFilesChanged()
    {
        var filePath = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "test content");

        var uploadedPaths = new List<string>();

        _storageMock.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, remotePath) => uploadedPaths.Add(remotePath))
            .Returns(Task.CompletedTask);

        _storageMock.Setup(s => s.ExistsAsync(It.IsAny<string>()))
            .ReturnsAsync(false);

        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>()))
            .ReturnsAsync(_storageMock.Object);

        _configMock.Setup(c => c.WatchDirectories)
            .Returns(new List<WatchDirectoryConfig>());

        _configMock.Setup(c => c.GlobalStorageType).Returns("local");
        _configMock.Setup(c => c.SizeThresholdMB).Returns(100);
        _configMock.Setup(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _configMock.Setup(c => c.ExcludedPatterns).Returns([]);
        _configMock.Setup(c => c.ExcludedPaths).Returns([]);
        _configMock.Setup(c => c.BackupType).Returns(BackupType.Incremental);

        _sizeAnalyzerMock.Setup(s => s.AnalyzeDirectoryAsync(It.IsAny<string>()))
            .ReturnsAsync((100, false));

        _stateMock.Setup(s => s.GetChangedFiles(It.IsAny<List<string>>(), It.IsAny<BackupType>()))
            .Returns(new List<string> { filePath });

        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        await backup.BackupDirectoryAsync(_testDir);

        _storageMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);
        _stateMock.Verify(s => s.SaveStateAsync(), Times.AtLeastOnce);
        uploadedPaths.Should().Contain(path => path.StartsWith("chunks/", StringComparison.OrdinalIgnoreCase));
        uploadedPaths.Should().Contain(path => path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase));
        uploadedPaths.Should().Contain(path => path.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BackupDirectoryAsync_ShouldNotUpdateMetadata_WhenUploadFails()
    {
        var filePath = Path.Combine(_testDir, "failure-case.txt");
        await File.WriteAllTextAsync(filePath, "test content");

        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>()))
            .ReturnsAsync(_storageMock.Object);

        _configMock.Setup(c => c.WatchDirectories)
            .Returns(new List<WatchDirectoryConfig>());

        _configMock.Setup(c => c.GlobalStorageType).Returns("local");
        _configMock.Setup(c => c.SizeThresholdMB).Returns(100);
        _configMock.Setup(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _configMock.Setup(c => c.ExcludedPatterns).Returns(new List<string>());
        _configMock.Setup(c => c.ExcludedPaths).Returns(new List<string>());
        _configMock.Setup(c => c.BackupType).Returns(BackupType.Incremental);

        _sizeAnalyzerMock.Setup(s => s.AnalyzeDirectoryAsync(It.IsAny<string>()))
            .ReturnsAsync((100, false));

        _stateMock.Setup(s => s.GetChangedFiles(It.IsAny<List<string>>(), It.IsAny<BackupType>()))
            .Returns(new List<string> { filePath });

        _storageMock.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("simulated upload failure"));

        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        var act = () => backup.BackupDirectoryAsync(_testDir);

        await Assert.ThrowsAsync<InvalidOperationException>(act);
        _stateMock.Verify(s => s.AddOrUpdateFileMetadataAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BackupDirectoryAsync_ShouldThrow_WhenSourceDirectoryIsEmpty()
    {
        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        var action = () => backup.BackupDirectoryAsync(" ");

        await Assert.ThrowsAsync<ArgumentException>(action);
        _configMock.Verify(c => c.CreateStorageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BackupDirectoryAsync_ShouldThrow_WhenSourceDirectoryMissing()
    {
        var missingDir = Path.Combine(_testDir, "missing-folder");

        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        var action = () => backup.BackupDirectoryAsync(missingDir);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(action);
    }

    [Fact]
    public async Task BackupFilesAsync_ShouldThrow_WhenFilesArgumentIsNull()
    {
        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        var action = () => backup.BackupFilesAsync(null!, _testDir);

        await Assert.ThrowsAsync<ArgumentNullException>(action);
    }

    [Fact]
    public async Task BackupFilesAsync_ShouldReturnWithoutCreatingStorage_WhenNoFilesProvided()
    {
        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        await backup.BackupFilesAsync([], _testDir);

        _configMock.Verify(c => c.CreateStorageAsync(It.IsAny<string>()), Times.Never);
        _stateMock.Verify(s => s.SaveStateAsync(), Times.Never);
    }

    [Fact]
    public async Task BackupDirectoryAsync_ShouldUseWatchDirectoryStorageType_WhenConfigured()
    {
        var filePath = Path.Combine(_testDir, "watched.txt");
        await File.WriteAllTextAsync(filePath, "watched content");

        _configMock.Setup(c => c.WatchDirectories)
            .Returns(
            [
                new WatchDirectoryConfig
                {
                    Path = _testDir,
                    StorageType = "s3"
                }
            ]);

        _configMock.Setup(c => c.CreateStorageAsync("s3"))
            .ReturnsAsync(_storageMock.Object);

        _sizeAnalyzerMock.Setup(s => s.AnalyzeDirectoryAsync(It.IsAny<string>()))
            .ReturnsAsync((100, false));

        _stateMock.Setup(s => s.GetChangedFiles(It.IsAny<List<string>>(), BackupType.Incremental))
            .Returns([filePath]);

        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        await backup.BackupDirectoryAsync(_testDir);

        _configMock.Verify(c => c.CreateStorageAsync("s3"), Times.Once);
    }

    [Fact]
    public async Task BackupDirectoryAsync_ShouldSaveStateWithoutUploading_WhenNoFilesNeedBackup()
    {
        var filePath = Path.Combine(_testDir, "unchanged.txt");
        await File.WriteAllTextAsync(filePath, "unchanged");

        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>()))
            .ReturnsAsync(_storageMock.Object);

        _sizeAnalyzerMock.Setup(s => s.AnalyzeDirectoryAsync(It.IsAny<string>()))
            .ReturnsAsync((100, false));

        _stateMock.Setup(s => s.GetChangedFiles(It.IsAny<List<string>>(), BackupType.Incremental))
            .Returns([]);

        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        await backup.BackupDirectoryAsync(_testDir);

        _storageMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _stateMock.Verify(s => s.SaveStateAsync(), Times.Once);
    }

    [Fact]
    public async Task BackupFilesAsync_ShouldThrow_WhenBaseDirectoryIsEmpty()
    {
        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        var action = () => backup.BackupFilesAsync([Path.Combine(_testDir, "file.txt")], " ");

        await Assert.ThrowsAsync<ArgumentException>(action);
    }

    [Fact]
    public async Task BackupFilesAsync_ShouldUpdateDeletedFiles_AndCommitSnapshotForDeleteEvents()
    {
        var deletedFile = Path.Combine(_testDir, "deleted.txt");

        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>()))
            .ReturnsAsync(_storageMock.Object);

        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        await backup.BackupFilesAsync([deletedFile], _testDir);

        _stateMock.Verify(s => s.AddOrUpdateFileMetadataAsync(deletedFile), Times.Once);
        _stateMock.Verify(s => s.SaveStateAsync(), Times.Once);
        _storageMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task BackupFilesAsync_ShouldThrow_WhenEncryptionEnabledButPasswordProviderMissing()
    {
        var filePath = Path.Combine(_testDir, "secret.txt");
        await File.WriteAllTextAsync(filePath, "secret content");

        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>()))
            .ReturnsAsync(_storageMock.Object);
        _configMock.Setup(c => c.Encryption)
            .Returns(new EncryptionConfig
            {
                Enabled = true,
                Salt = Convert.ToBase64String(EncryptionService.GenerateSalt())
            });

        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        var action = () => backup.BackupFilesAsync([filePath], _testDir);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no password provider is available*");

        _storageMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BackupDirectoryAsync_ShouldContinue_WhenPreviousManifestIsCorrupted()
    {
        var sourceDir = Path.Combine(_testDir, "corrupt-source");
        var storageDir = Path.Combine(_testDir, "corrupt-storage");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(storageDir);

        var sourceFile = Path.Combine(sourceDir, "sample.txt");
        await File.WriteAllTextAsync(sourceFile, "v1");

        var logger = new TestLogger();
        var storage = new LocalStorage(logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDir });

        var configMock = CreateLocalSnapshotConfigMock(storage);
        var state = new SystemState(logger);
        state.SetStateFilePath(Path.Combine(_testDir, "corrupt-state.json"));

        var backup = new Backup(logger, state, new SizeAnalyzer(), configMock.Object);
        await backup.BackupDirectoryAsync(sourceDir);

        var firstManifestPath = state.GetPreviousBackupPath(sourceDir);
        firstManifestPath.Should().NotBeNullOrWhiteSpace();

        var firstManifestLocalPath = Path.Combine(storageDir, firstManifestPath!.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(firstManifestLocalPath, "{not-valid-json");

        await File.WriteAllTextAsync(sourceFile, "v2");

        var action = () => backup.BackupDirectoryAsync(sourceDir);

        await action.Should().NotThrowAsync();
        state.GetBackupsForGroup(sourceDir).Should().HaveCount(2);
    }

    [Fact]
    public async Task BackupDirectoryAsync_ShouldIgnorePreviousManifest_WhenHeadPointsToDifferentGroupManifest()
    {
        var sourceADir = Path.Combine(_testDir, "group-a");
        var sourceBDir = Path.Combine(_testDir, "group-b");
        var storageDir = Path.Combine(_testDir, "cross-group-storage");
        Directory.CreateDirectory(sourceADir);
        Directory.CreateDirectory(sourceBDir);
        Directory.CreateDirectory(storageDir);

        await File.WriteAllTextAsync(Path.Combine(sourceADir, "common.txt"), "group-a-content");
        await File.WriteAllTextAsync(Path.Combine(sourceBDir, "common.txt"), "group-b-content");

        var logger = new TestLogger();
        var storage = new LocalStorage(logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDir });

        var configMock = CreateLocalSnapshotConfigMock(storage);
        var state = new SystemState(logger);
        state.SetStateFilePath(Path.Combine(_testDir, "cross-group-state.json"));

        var backup = new Backup(logger, state, new SizeAnalyzer(), configMock.Object);
        await backup.BackupDirectoryAsync(sourceADir);
        await backup.BackupDirectoryAsync(sourceBDir);

        var sourceBManifestPath = state.GetPreviousBackupPath(sourceBDir);
        sourceBManifestPath.Should().NotBeNullOrWhiteSpace();

        var sourceBManifestLocalPath = Path.Combine(storageDir, sourceBManifestPath!.Replace('/', Path.DirectorySeparatorChar));
        var sourceBManifest = JsonSerializer.Deserialize<SnapshotManifest>(await File.ReadAllTextAsync(sourceBManifestLocalPath));
        sourceBManifest.Should().NotBeNull();

        var sourceAHeadPath = SnapshotStoragePaths.GetHeadPath(sourceADir);
        var sourceAHeadLocalPath = Path.Combine(storageDir, sourceAHeadPath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(sourceAHeadLocalPath, $"{sourceBManifestPath}\n{sourceBManifest!.RootHash}\n");

        await File.WriteAllTextAsync(Path.Combine(sourceADir, "new-file.txt"), "group-a-new-content");

        await backup.BackupDirectoryAsync(sourceADir);

        var latestSourceAManifestPath = state.GetPreviousBackupPath(sourceADir);
        latestSourceAManifestPath.Should().NotBeNullOrWhiteSpace();

        var restoreDirectory = Path.Combine(_testDir, "cross-group-restore");
        var restore = new Restore(logger, storage);
        await restore.RestoreFromBackupAsync(latestSourceAManifestPath!, restoreDirectory);

        var restoredCommonContent = await File.ReadAllTextAsync(Path.Combine(restoreDirectory, "common.txt"));
        restoredCommonContent.Should().Be("group-a-content");
    }

    private static Mock<IConfigManager> CreateLocalSnapshotConfigMock(LocalStorage storage)
    {
        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(config => config.Retention).Returns(new RetentionConfig
        {
            Enabled = false,
            KeepLastPerDirectory = 10,
            MaxAgeDays = 30
        });
        configMock.SetupGet(config => config.GlobalStorageType).Returns("local");
        configMock.SetupGet(config => config.SizeThresholdMB).Returns(100);
        configMock.SetupGet(config => config.ExcludedPatterns).Returns([]);
        configMock.SetupGet(config => config.ExcludedPaths).Returns([]);
        configMock.SetupGet(config => config.BackupType).Returns(BackupType.ChunkSnapshot);
        configMock.SetupGet(config => config.WatchDirectories).Returns([]);
        configMock.SetupGet(config => config.MaxFileSizeMB).Returns(100);
        configMock.SetupGet(config => config.ChunkDiffing).Returns(new ChunkDiffingConfig());
        configMock.SetupGet(config => config.Encryption).Returns(new EncryptionConfig
        {
            Enabled = false
        });
        configMock.Setup(config => config.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);
        return configMock;
    }
}
