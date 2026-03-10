using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Core.src.storage;
using ReStore.Core.src.monitoring;
using FluentAssertions;

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

        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        await backup.BackupDirectoryAsync(_testDir);

        _storageMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _stateMock.Verify(s => s.SaveStateAsync(), Times.AtLeastOnce);
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
    public async Task BackupFilesAsync_ShouldUpdateDeletedFilesAndSkipUpload_WhenFilesDisappearBeforeArchive()
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
        _storageMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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
}
