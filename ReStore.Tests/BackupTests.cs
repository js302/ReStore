using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Core.src.storage;
using ReStore.Core.src.monitoring;

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
        // Arrange
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

        // Mock GetChangedFiles to return our file
        _stateMock.Setup(s => s.GetChangedFiles(It.IsAny<List<string>>(), It.IsAny<BackupType>()))
            .Returns(new List<string> { filePath });

        var backup = new Backup(
            _loggerMock.Object,
            _stateMock.Object,
            _sizeAnalyzerMock.Object,
            _configMock.Object
        );

        // Act
        await backup.BackupDirectoryAsync(_testDir);

        // Assert
        _storageMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _stateMock.Verify(s => s.SaveStateAsync(), Times.AtLeastOnce);
    }
}
