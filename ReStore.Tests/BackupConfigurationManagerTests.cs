using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class BackupConfigurationManagerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger;
    private readonly Mock<IConfigManager> _configMock;

    public BackupConfigurationManagerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreBackupConfigurationManagerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);

        _logger = new TestLogger();
        _configMock = new Mock<IConfigManager>();
        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _testRoot, StorageType = null }
        ]);
        _configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        _configMock.SetupGet(c => c.ExcludedPatterns).Returns(["*.tmp"]);
        _configMock.SetupGet(c => c.MaxFileSizeMB).Returns(42);
        _configMock.SetupGet(c => c.BackupType).Returns(BackupType.Differential);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public void Constructor_ShouldInitializeFromConfigManager()
    {
        var manager = new BackupConfigurationManager(_logger, _configMock.Object);

        manager.Configuration.IncludePaths.Should().Contain(_testRoot);
        manager.Configuration.ExcludePatterns.Should().Contain("*.tmp");
        manager.Configuration.MaxFileSize.Should().Be(42 * 1024 * 1024);
        manager.Configuration.Type.Should().Be(BackupType.Differential);
    }

    [Fact]
    public void LoadConfiguration_ShouldReadExistingConfigurationFile()
    {
        var manager = new BackupConfigurationManager(_logger, _configMock.Object);
        var configPath = Path.Combine(_testRoot, "config", "backup-config.json");

        var savedConfig = new BackupConfiguration
        {
            IncludePaths = [Path.Combine(_testRoot, "include")],
            ExcludePaths = [Path.Combine(_testRoot, "exclude")],
            ExcludePatterns = ["*.log"],
            MaxFileSize = 123,
            Type = BackupType.Full
        };

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(savedConfig));

        manager.LoadConfiguration(configPath);

        manager.Configuration.IncludePaths.Should().ContainSingle().Which.Should().Be(savedConfig.IncludePaths[0]);
        manager.Configuration.ExcludePaths.Should().ContainSingle().Which.Should().Be(savedConfig.ExcludePaths[0]);
        manager.Configuration.ExcludePatterns.Should().ContainSingle().Which.Should().Be("*.log");
        manager.Configuration.MaxFileSize.Should().Be(123);
        manager.Configuration.Type.Should().Be(BackupType.Full);
    }

    [Fact]
    public void LoadConfiguration_ShouldCreateConfiguration_WhenMissing()
    {
        var manager = new BackupConfigurationManager(_logger, _configMock.Object);
        var configPath = Path.Combine(_testRoot, "config", "missing-config.json");

        File.Exists(configPath).Should().BeFalse();

        manager.LoadConfiguration(configPath);

        File.Exists(configPath).Should().BeTrue();
        _logger.Messages.Should().Contain(m => m.Contains("Created backup configuration from system settings"));
    }

    [Fact]
    public void LoadConfiguration_ShouldNotThrow_OnInvalidJson()
    {
        var manager = new BackupConfigurationManager(_logger, _configMock.Object);
        var configPath = Path.Combine(_testRoot, "config", "invalid-config.json");

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "{ invalid json }");

        var action = () => manager.LoadConfiguration(configPath);

        action.Should().NotThrow();
        _logger.Messages.Should().Contain(m => m.Contains("Error loading backup configuration"));
    }

    [Fact]
    public void SaveConfiguration_ShouldWriteIndentedJsonToDisk()
    {
        var manager = new BackupConfigurationManager(_logger, _configMock.Object);
        var configPath = Path.Combine(_testRoot, "save", "backup-config.json");

        manager.SaveConfiguration(configPath);

        File.Exists(configPath).Should().BeTrue();
        var content = File.ReadAllText(configPath);
        content.Should().Contain("IncludePaths");
        content.Should().Contain(Environment.NewLine);
        _logger.Messages.Should().Contain(m => m.Contains("Backup configuration saved successfully"));
    }

    [Fact]
    public void GetFilesToBackup_ShouldUseConfiguredIncludeAndExclusions()
    {
        var includeDir = Path.Combine(_testRoot, "include");
        Directory.CreateDirectory(includeDir);

        var includeFile = Path.Combine(includeDir, "keep.txt");
        var excludedFile = Path.Combine(includeDir, "skip.tmp");

        File.WriteAllText(includeFile, "ok");
        File.WriteAllText(excludedFile, "tmp");

        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = includeDir, StorageType = null }
        ]);

        var manager = new BackupConfigurationManager(_logger, _configMock.Object);

        var files = manager.GetFilesToBackup();

        files.Should().Contain(includeFile);
        files.Should().NotContain(excludedFile);
    }
}
