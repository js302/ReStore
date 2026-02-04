using Moq;
using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.monitoring;
using Bogus;

namespace ReStore.Tests;

public class FuzzTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _sourceDir;
    private readonly string _backupDir;
    private readonly string _stateDir;
    private readonly Mock<ILogger> _loggerMock;
    private readonly Faker _faker;

    public FuzzTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreFuzz_" + Guid.NewGuid());
        _sourceDir = Path.Combine(_testRoot, "Source");
        _backupDir = Path.Combine(_testRoot, "Backups");
        _stateDir = Path.Combine(_testRoot, "State");

        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_backupDir);
        Directory.CreateDirectory(_stateDir);

        _loggerMock = new Mock<ILogger>();
        _faker = new Faker();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task Backup_ShouldHandleChaoticFileStructures()
    {
        // Arrange - Generate Chaos
        int fileCount = 50;
        var generatedFiles = new List<string>();

        for (int i = 0; i < fileCount; i++)
        {
            // Generate weird filenames (but valid for Windows)
            var fileName = _faker.System.FileName().Replace(":", "").Replace("?", "").Replace("*", "");
            var subDir = _faker.Random.Bool() ? _faker.Random.AlphaNumeric(5) : "";
            
            var dirPath = Path.Combine(_sourceDir, subDir);
            Directory.CreateDirectory(dirPath);
            
            var filePath = Path.Combine(dirPath, fileName);
            
            // Random content size (0 to 1MB)
            var content = _faker.Random.String2(_faker.Random.Int(0, 1000));
            await File.WriteAllTextAsync(filePath, content);
            generatedFiles.Add(filePath);
        }

        // Setup Config
        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.Retention)
            .Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        configMock.Setup(c => c.GlobalStorageType).Returns("local");
        configMock.Setup(c => c.SizeThresholdMB).Returns(100);
        configMock.Setup(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        configMock.Setup(c => c.ExcludedPatterns).Returns(new List<string>());
        configMock.Setup(c => c.ExcludedPaths).Returns(new List<string>());
        configMock.Setup(c => c.BackupType).Returns(BackupType.Full);
        configMock.Setup(c => c.WatchDirectories).Returns(new List<WatchDirectoryConfig>());
        configMock.Setup(c => c.MaxFileSizeMB).Returns(100); // Fix: Set max file size

        // Setup Storage
        var storage = new LocalStorage(_loggerMock.Object);
        await storage.InitializeAsync(new Dictionary<string, string> { { "path", _backupDir } });
        
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>()))
            .ReturnsAsync(storage);

        // Setup SystemState
        var state = new SystemState(_loggerMock.Object);
        state.SetStateFilePath(Path.Combine(_stateDir, "state.json"));

        var backup = new Backup(
            _loggerMock.Object,
            state,
            new SizeAnalyzer(),
            configMock.Object
        );

        // Act - Should not throw
        var exception = await Record.ExceptionAsync(() => backup.BackupDirectoryAsync(_sourceDir));

        // Assert
        exception.Should().BeNull();
        
        // Verify something was uploaded
        var backups = Directory.GetFiles(_backupDir, "*.zip", SearchOption.AllDirectories);
        backups.Should().NotBeEmpty();
    }
}
