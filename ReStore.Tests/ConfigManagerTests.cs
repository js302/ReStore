using FluentAssertions;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;
using System.Reflection;
using System.Text.Json;

namespace ReStore.Tests;

public class ConfigManagerTests
{
  private readonly ConfigManager _configManager = new(new TestLogger());

  [Fact]
  public async Task CreateStorageAsync_ShouldThrow_WhenConfigNotLoaded()
  {
    var action = () => _configManager.CreateStorageAsync("local");

    await action.Should().ThrowAsync<InvalidOperationException>()
        .WithMessage("*Configuration not loaded*");
  }

  [Fact]
  public async Task CreateStorageAsync_ShouldThrow_WhenStorageTypeMissing()
  {
    SetPrivateField(_configManager, "_isLoaded", true);
    SetProperty(_configManager, nameof(ConfigManager.StorageSources), new Dictionary<string, StorageConfig>());

    var action = () => _configManager.CreateStorageAsync("local");

    await action.Should().ThrowAsync<ArgumentException>()
        .WithMessage("*not found in configuration*");
  }

  [Fact]
  public async Task CreateStorageAsync_ShouldInjectPathOption_WhenMissing()
  {
    var tempStoragePath = Path.Combine(Path.GetTempPath(), "ReStoreConfigManagerStorage_" + Guid.NewGuid().ToString("N"));

    var storageSources = new Dictionary<string, StorageConfig>
    {
      ["local"] = new()
      {
        Path = tempStoragePath,
        Options = new Dictionary<string, string>()
      }
    };

    SetPrivateField(_configManager, "_isLoaded", true);
    SetProperty(_configManager, nameof(ConfigManager.StorageSources), storageSources);

    using var storage = await _configManager.CreateStorageAsync("local");

    storage.Should().BeOfType<LocalStorage>();
    storageSources["local"].Options.Should().ContainKey("path");
    storageSources["local"].Options["path"].Should().Be(tempStoragePath);
  }

  [Fact]
  public void LoadWatchDirectories_ShouldSupportStringAndObjectEntries()
  {
    var root = ParseRoot("""
{
  "watchDirectories": [
    "%TEMP%\\watch-a",
    { "path": "%TEMP%\\watch-b", "storageType": "local" }
  ]
}
""");

    InvokePrivate(_configManager, "LoadWatchDirectories", root);

    _configManager.WatchDirectories.Should().HaveCount(2);
    _configManager.WatchDirectories[0].StorageType.Should().BeNull();
    _configManager.WatchDirectories[0].Path.Should().NotContain("%TEMP%");
    _configManager.WatchDirectories[1].StorageType.Should().Be("local");
    _configManager.WatchDirectories[1].Path.Should().NotContain("%TEMP%");
  }

  [Fact]
  public void LoadStorageSettings_ShouldExpandPaths_AndDefaultGlobalStorageType()
  {
    var root = ParseRoot("""
{
  "storageSources": {
    "local": {
      "path": "%TEMP%\\restore-storage",
      "options": {
        "path": "%TEMP%\\restore-storage",
        "bucketName": "demo"
      }
    }
  }
}
""");

    InvokePrivate(_configManager, "LoadStorageSettings", root);

    _configManager.GlobalStorageType.Should().Be("local");
    _configManager.StorageSources.Should().ContainKey("local");
    _configManager.StorageSources["local"].Path.Should().NotContain("%TEMP%");
    _configManager.StorageSources["local"].Options["path"].Should().NotContain("%TEMP%");
    _configManager.StorageSources["local"].Options["bucketName"].Should().Be("demo");
  }

  [Fact]
  public void LoadExclusions_ShouldFallbackToDefaults_WhenPropertiesMissing()
  {
    var root = ParseRoot("{}");

    InvokePrivate(_configManager, "LoadExclusions", root);

    _configManager.ExcludedPatterns.Should().Contain("*.tmp");
    _configManager.ExcludedPatterns.Should().Contain("desktop.ini");
    _configManager.ExcludedPaths.Should().NotBeEmpty();
  }

  [Fact]
  public void LoadBackupTypeAndLimits_ShouldParseBackupTypeAndMaxFileSize()
  {
    var root = ParseRoot("""
{
  "backupType": "Differential",
  "maxFileSizeMB": 42
}
""");

    InvokePrivate(_configManager, "LoadBackupTypeAndLimits", root);

    _configManager.BackupType.Should().Be(BackupType.Differential);
    _configManager.MaxFileSizeMB.Should().Be(42);
  }

  [Fact]
  public void LoadSystemBackupSettings_ShouldReadComponentOverrides()
  {
    var root = ParseRoot("""
{
  "systemBackup": {
    "enabled": true,
    "includePrograms": true,
    "includeEnvironmentVariables": false,
    "includeWindowsSettings": true,
    "backupInterval": "12:00:00",
    "excludeSystemPrograms": ["A", "B"],
    "storageType": "s3",
    "programsStorageType": "dropbox",
    "environmentStorageType": "local",
    "settingsStorageType": "azure"
  }
}
""");

    InvokePrivate(_configManager, "LoadSystemBackupSettings", root);

    _configManager.SystemBackup.Enabled.Should().BeTrue();
    _configManager.SystemBackup.IncludeEnvironmentVariables.Should().BeFalse();
    _configManager.SystemBackup.BackupInterval.Should().Be(TimeSpan.FromHours(12));
    _configManager.SystemBackup.ExcludeSystemPrograms.Should().Contain(["A", "B"]);
    _configManager.SystemBackup.StorageType.Should().Be("s3");
    _configManager.SystemBackup.ProgramsStorageType.Should().Be("dropbox");
    _configManager.SystemBackup.EnvironmentStorageType.Should().Be("local");
    _configManager.SystemBackup.SettingsStorageType.Should().Be("azure");
  }

  [Fact]
  public void LoadEncryptionAndRetentionSettings_ShouldPopulateValues()
  {
    var root = ParseRoot("""
{
  "encryption": {
    "enabled": true,
    "salt": "abc",
    "keyDerivationIterations": 250000,
    "verificationToken": "token"
  },
  "retention": {
    "enabled": true,
    "keepLastPerDirectory": 7,
    "maxAgeDays": 14
  }
}
""");

    InvokePrivate(_configManager, "LoadEncryptionSettings", root);
    InvokePrivate(_configManager, "LoadRetentionSettings", root);

    _configManager.Encryption.Enabled.Should().BeTrue();
    _configManager.Encryption.Salt.Should().Be("abc");
    _configManager.Encryption.KeyDerivationIterations.Should().Be(250000);
    _configManager.Encryption.VerificationToken.Should().Be("token");

    _configManager.Retention.Enabled.Should().BeTrue();
    _configManager.Retention.KeepLastPerDirectory.Should().Be(7);
    _configManager.Retention.MaxAgeDays.Should().Be(14);
  }

  [Fact]
  public void LoadExclusions_ShouldExpandConfiguredPaths()
  {
    var root = ParseRoot("""
    {
      "excludedPatterns": ["*.cache"],
      "excludedPaths": ["%TEMP%\\restore-cache"]
    }
    """);

    InvokePrivate(_configManager, "LoadExclusions", root);

    _configManager.ExcludedPatterns.Should().ContainSingle().Which.Should().Be("*.cache");
    _configManager.ExcludedPaths.Should().ContainSingle();
    _configManager.ExcludedPaths[0].Should().NotContain("%TEMP%");
    _configManager.ExcludedPaths[0].Should().Contain("restore-cache");
  }

  [Fact]
  public void LoadSystemBackupSettings_ShouldUseDefaults_WhenSectionMissing()
  {
    var root = ParseRoot("{}");

    InvokePrivate(_configManager, "LoadSystemBackupSettings", root);

    _configManager.SystemBackup.Enabled.Should().BeFalse();
    _configManager.SystemBackup.IncludePrograms.Should().BeFalse();
    _configManager.SystemBackup.IncludeEnvironmentVariables.Should().BeFalse();
    _configManager.SystemBackup.IncludeWindowsSettings.Should().BeFalse();
    _configManager.SystemBackup.ExcludeSystemPrograms.Should().Contain(program => program.Contains("Microsoft Visual C++"));
  }

  [Fact]
  public async Task SaveAsync_ShouldWriteConfiguredValues_ToSpecifiedPath()
  {
    var tempDir = Path.Combine(Path.GetTempPath(), "ReStoreConfigSave_" + Guid.NewGuid().ToString("N"));
    var configPath = Path.Combine(tempDir, "config.json");

    try
    {
      SetProperty(_configManager, nameof(ConfigManager.WatchDirectories), new List<WatchDirectoryConfig>
          {
            new() { Path = @"C:\\Data", StorageType = "local" }
          });
      SetProperty(_configManager, nameof(ConfigManager.GlobalStorageType), "local");
      SetProperty(_configManager, nameof(ConfigManager.BackupInterval), TimeSpan.FromMinutes(15));
      SetProperty(_configManager, nameof(ConfigManager.SizeThresholdMB), 123L);
      SetProperty(_configManager, nameof(ConfigManager.MaxFileSizeMB), 45);
      SetProperty(_configManager, nameof(ConfigManager.BackupType), BackupType.Differential);
      SetProperty(_configManager, nameof(ConfigManager.ExcludedPatterns), new List<string> { "*.tmp" });
      SetProperty(_configManager, nameof(ConfigManager.ExcludedPaths), new List<string> { @"C:\\Temp" });
      SetProperty(_configManager, nameof(ConfigManager.Retention), new RetentionConfig { Enabled = true, KeepLastPerDirectory = 3, MaxAgeDays = 9 });
      SetProperty(_configManager, nameof(ConfigManager.SystemBackup), new SystemBackupConfig
      {
        Enabled = true,
        IncludePrograms = true,
        IncludeEnvironmentVariables = false,
        IncludeWindowsSettings = true,
        BackupInterval = TimeSpan.FromHours(6),
        ExcludeSystemPrograms = ["Demo"],
        StorageType = "local",
        ProgramsStorageType = "s3",
        EnvironmentStorageType = "azure",
        SettingsStorageType = "dropbox"
      });
      SetProperty(_configManager, nameof(ConfigManager.Encryption), new EncryptionConfig
      {
        Enabled = true,
        Salt = "salt",
        KeyDerivationIterations = 250000,
        VerificationToken = "token"
      });
      SetProperty(_configManager, nameof(ConfigManager.StorageSources), new Dictionary<string, StorageConfig>
      {
        ["local"] = new()
        {
          Path = @"C:\\Backups",
          Options = new Dictionary<string, string> { ["path"] = @"C:\\Backups" }
        }
      });

      await _configManager.SaveAsync(configPath);

      File.Exists(configPath).Should().BeTrue();

      using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
      var root = doc.RootElement;

      root.GetProperty("globalStorageType").GetString().Should().Be("local");
      root.GetProperty("backupInterval").GetString().Should().Be("00:15:00");
      root.GetProperty("sizeThresholdMB").GetInt64().Should().Be(123);
      root.GetProperty("maxFileSizeMB").GetInt32().Should().Be(45);
      root.GetProperty("backupType").GetString().Should().Be("Differential");
      var watchDirectory = root.GetProperty("watchDirectories").EnumerateArray().Should().ContainSingle().Subject;
      watchDirectory.GetProperty("path").GetString().Should().Be(@"C:\\Data");
      watchDirectory.GetProperty("storageType").GetString().Should().Be("local");
      root.GetProperty("retention").GetProperty("keepLastPerDirectory").GetInt32().Should().Be(3);
      root.GetProperty("systemBackup").GetProperty("enabled").GetBoolean().Should().BeTrue();
      root.GetProperty("systemBackup").GetProperty("backupInterval").GetString().Should().Be("06:00:00");
      root.GetProperty("encryption").GetProperty("verificationToken").GetString().Should().Be("token");
      var storageSource = root.GetProperty("storageSources").GetProperty("local");
      storageSource.GetProperty("path").GetString().Should().Be(@"C:\\Backups");
      storageSource.GetProperty("options").GetProperty("path").GetString().Should().Be(@"C:\\Backups");
    }
    finally
    {
      if (Directory.Exists(tempDir))
      {
        Directory.Delete(tempDir, true);
      }
    }
  }

  private static JsonElement ParseRoot(string json)
  {
    using var doc = JsonDocument.Parse(json);
    return doc.RootElement.Clone();
  }

  private static void InvokePrivate(object instance, string methodName, params object[] args)
  {
    var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
    method.Should().NotBeNull();
    method!.Invoke(instance, args);
  }

  private static void SetPrivateField(object instance, string fieldName, object value)
  {
    var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
    field.Should().NotBeNull();
    field!.SetValue(instance, value);
  }

  private static void SetProperty(object instance, string propertyName, object value)
  {
    var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    property.Should().NotBeNull();
    property!.SetValue(instance, value);
  }
}
