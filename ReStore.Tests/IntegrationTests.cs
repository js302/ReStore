using Moq;
using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.monitoring;

namespace ReStore.Tests;

public class IntegrationTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _sourceDir;
    private readonly string _backupDir;
    private readonly string _restoreDir;
    private readonly string _stateDir;
    private readonly Mock<ILogger> _loggerMock;

    public IntegrationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreIntegration_" + Guid.NewGuid());
        _sourceDir = Path.Combine(_testRoot, "Source");
        _backupDir = Path.Combine(_testRoot, "Backups");
        _restoreDir = Path.Combine(_testRoot, "Restore");
        _stateDir = Path.Combine(_testRoot, "State");

        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_backupDir);
        Directory.CreateDirectory(_restoreDir);
        Directory.CreateDirectory(_stateDir);

        _loggerMock = new Mock<ILogger>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task FullBackupAndRestore_ShouldPreserveFiles()
    {
        // Arrange
        var file1 = Path.Combine(_sourceDir, "file1.txt");
        var file2 = Path.Combine(_sourceDir, "subdir", "file2.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file2)!);

        await File.WriteAllTextAsync(file1, "Content 1");
        await File.WriteAllTextAsync(file2, "Content 2");

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
        configMock.Setup(c => c.MaxFileSizeMB).Returns(100); // Fix: Set max file size to avoid filtering

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

        // Act - Backup
        await backup.BackupDirectoryAsync(_sourceDir);

        // Verify Backup Created
        var backups = Directory.GetFiles(_backupDir, "*.zip", SearchOption.AllDirectories);
        backups.Should().HaveCount(1);
        var backupFile = backups[0];

        // Act - Restore
        var relativeBackupPath = Path.GetRelativePath(_backupDir, backupFile).Replace('\\', '/');

        var restore = new Restore(
            _loggerMock.Object,
            storage
        );

        await restore.RestoreFromBackupAsync(relativeBackupPath, _restoreDir);

        // Assert
        File.Exists(Path.Combine(_restoreDir, "file1.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_restoreDir, "subdir", "file2.txt")).Should().BeTrue();
        
        (await File.ReadAllTextAsync(Path.Combine(_restoreDir, "file1.txt"))).Should().Be("Content 1");
        (await File.ReadAllTextAsync(Path.Combine(_restoreDir, "subdir", "file2.txt"))).Should().Be("Content 2");
    }

    [Fact]
    public async Task IncrementalBackup_ShouldOnlyBackupChangedFiles()
    {
        // Arrange
        var file1 = Path.Combine(_sourceDir, "file1.txt");
        await File.WriteAllTextAsync(file1, "Content 1");

        // Setup Config
        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.Retention)
            .Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        configMock.Setup(c => c.GlobalStorageType).Returns("local");
        configMock.Setup(c => c.SizeThresholdMB).Returns(100);
        configMock.Setup(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        configMock.Setup(c => c.ExcludedPatterns).Returns(new List<string>());
        configMock.Setup(c => c.ExcludedPaths).Returns(new List<string>());
        configMock.Setup(c => c.BackupType).Returns(BackupType.Incremental);
        configMock.Setup(c => c.WatchDirectories).Returns(new List<WatchDirectoryConfig>());
        configMock.Setup(c => c.MaxFileSizeMB).Returns(100);

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

        // Act 1 - Initial Backup
        await backup.BackupDirectoryAsync(_sourceDir);
        var initialBackups = Directory.GetFiles(_backupDir, "*.zip", SearchOption.AllDirectories);
        initialBackups.Should().HaveCount(1);

        // Act 2 - No Changes
        await Task.Delay(1100); // Ensure timestamp changes for filename uniqueness
        await backup.BackupDirectoryAsync(_sourceDir);
        var secondBackups = Directory.GetFiles(_backupDir, "*.zip", SearchOption.AllDirectories);
        secondBackups.Should().HaveCount(1, "No new backup should be created if nothing changed");

        // Act 3 - Modify File
        await Task.Delay(1100); // Ensure timestamp changes for filename uniqueness and file modification time
        await File.WriteAllTextAsync(file1, "Content 1 Modified");
        await backup.BackupDirectoryAsync(_sourceDir);
        
        var finalBackups = Directory.GetFiles(_backupDir, "*.zip", SearchOption.AllDirectories);
        finalBackups.Should().HaveCount(2, "A new backup should be created for the changed file");
    }

    [Fact]
    public async Task EncryptedBackupAndRestore_ShouldWork()
    {
        // Arrange
        var file1 = Path.Combine(_sourceDir, "secret.txt");
        await File.WriteAllTextAsync(file1, "Top Secret Content");

        var password = "TestPassword123!";
        var salt = EncryptionService.GenerateSalt();

        // Setup Config
        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.Retention)
            .Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        configMock.Setup(c => c.GlobalStorageType).Returns("local");
        configMock.Setup(c => c.SizeThresholdMB).Returns(100);
        configMock.Setup(c => c.Encryption).Returns(new EncryptionConfig 
        { 
            Enabled = true,
            Salt = Convert.ToBase64String(salt),
            KeyDerivationIterations = 1000
        });
        configMock.Setup(c => c.ExcludedPatterns).Returns(new List<string>());
        configMock.Setup(c => c.ExcludedPaths).Returns(new List<string>());
        configMock.Setup(c => c.BackupType).Returns(BackupType.Full);
        configMock.Setup(c => c.WatchDirectories).Returns(new List<WatchDirectoryConfig>());
        configMock.Setup(c => c.MaxFileSizeMB).Returns(100);

        // Setup Password Provider
        var passwordMock = new Mock<IPasswordProvider>();
        passwordMock.Setup(p => p.GetPasswordAsync()).ReturnsAsync(password);

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
            configMock.Object,
            passwordMock.Object
        );

        // Act - Backup
        await backup.BackupDirectoryAsync(_sourceDir);

        // Verify Encrypted Files Exist
        var encryptedFiles = Directory.GetFiles(_backupDir, "*.enc", SearchOption.AllDirectories);
        encryptedFiles.Should().HaveCount(1);
        var metaFiles = Directory.GetFiles(_backupDir, "*.meta", SearchOption.AllDirectories);
        metaFiles.Should().HaveCount(1);

        // Act - Restore        
        var relativeBackupPath = $"backups/{Path.GetFileName(_sourceDir)}/{Path.GetFileName(encryptedFiles[0])}";

        var restore = new Restore(
            _loggerMock.Object,
            storage,
            passwordMock.Object
        );

        await restore.RestoreFromBackupAsync(relativeBackupPath, _restoreDir);

        // Assert
        var restoredFile = Path.Combine(_restoreDir, "secret.txt");
        File.Exists(restoredFile).Should().BeTrue();
        (await File.ReadAllTextAsync(restoredFile)).Should().Be("Top Secret Content");
    }
}
