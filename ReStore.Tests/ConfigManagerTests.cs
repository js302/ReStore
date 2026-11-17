using FluentAssertions;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;
using System.Reflection;
using System.Text.Json;

namespace ReStore.Tests;

[Collection("GlobalState")]
public class ConfigManagerTests
{
  private readonly ConfigManager _configManager = new(new TestLogger());

  [Theory]
  [InlineData("{ \"logging\": { \"level\": \"Debug\" } }", LogLevel.Debug)]
  [InlineData("{ \"logging\": { \"level\": \"warning\" } }", LogLevel.Warning)]
  [InlineData("{ }", LogLevel.Info)]
  [InlineData("{ \"logging\": { \"level\": \"Nonsense\" } }", LogLevel.Info)]
  public void LoadLoggingSettings_ShouldApplyMinimumLevelToTheLogger(string json, LogLevel expected)
  {
    using var logger = new Logger();
    var manager = new ConfigManager(logger);
    using var document = JsonDocument.Parse(json);

    var method = typeof(ConfigManager).GetMethod("LoadLoggingSettings", BindingFlags.Instance | BindingFlags.NonPublic);
    method.Should().NotBeNull();
    method!.Invoke(manager, [document.RootElement]);

    logger.MinimumLevel.Should().Be(expected);
  }

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
    storageSources["local"].Options.Should().NotContainKey("path", "the model is not mutated with expanded values");
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
    _configManager.WatchDirectories[0].Path.Should().StartWith("%TEMP%", "the model keeps the raw value");
    _configManager.WatchDirectories[0].ExpandedPath.Should().NotContain("%TEMP%");
    _configManager.WatchDirectories[1].StorageType.Should().Be("local");
    _configManager.WatchDirectories[1].Path.Should().StartWith("%TEMP%");
    _configManager.WatchDirectories[1].ExpandedPath.Should().NotContain("%TEMP%");
  }

  [Fact]
  public void LoadStorageSettings_ShouldKeepRawPaths_AndDefaultGlobalStorageType()
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
    _configManager.StorageSources["local"].Path.Should().StartWith("%TEMP%");
    _configManager.StorageSources["local"].Options["path"].Should().StartWith("%TEMP%");
    _configManager.StorageSources["local"].Expanded().Path.Should().NotContain("%TEMP%");
    _configManager.StorageSources["local"].Expanded().Options["path"].Should().NotContain("%TEMP%");
    _configManager.StorageSources["local"].Options["bucketName"].Should().Be("demo");
  }

  [Fact]
  public void SetGlobalStorageType_ShouldUpdateValue_WhenProviderIsConfigured()
  {
    InvokePrivate(_configManager, "LoadStorageSettings", ParseRoot("""
{
  "storageSources": {
    "local": { "path": "%TEMP%\\restore-storage", "options": {} },
    "s3": { "path": "", "options": { "bucketName": "demo" } }
  },
  "globalStorageType": "local"
}
"""));

    _configManager.SetGlobalStorageType("s3");

    _configManager.GlobalStorageType.Should().Be("s3");
  }

  [Fact]
  public void SetGlobalStorageType_ShouldThrow_WhenProviderIsNotConfigured()
  {
    InvokePrivate(_configManager, "LoadStorageSettings", ParseRoot("""
{
  "storageSources": {
    "local": { "path": "%TEMP%\\restore-storage", "options": {} }
  },
  "globalStorageType": "local"
}
"""));

    var act = () => _configManager.SetGlobalStorageType("gdrive");

    act.Should().Throw<ArgumentException>();
    _configManager.GlobalStorageType.Should().Be("local", "a rejected change must not be applied");
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void SetGlobalStorageType_ShouldThrow_WhenProviderIsBlank(string storageType)
  {
    var act = () => _configManager.SetGlobalStorageType(storageType);

    act.Should().Throw<ArgumentException>();
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

    _configManager.BackupType.Should().Be(BackupType.ChunkSnapshot);
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
  public void LoadSystemBackupSettings_DevTools_IsOffForOlderConfigs_AndReadWhenPresent()
  {
    InvokePrivate(_configManager, "LoadSystemBackupSettings", ParseRoot("""{ "systemBackup": { "enabled": true, "includePrograms": true } }"""));
    _configManager.SystemBackup.IncludeDevTools.Should().BeFalse();
    _configManager.SystemBackup.IncludeSshConfig.Should().BeFalse();

    InvokePrivate(_configManager, "LoadSystemBackupSettings", ParseRoot("""
{ "systemBackup": { "includeDevTools": true, "includeSshConfig": true, "devToolsStorageType": "s3" } }
"""));
    _configManager.SystemBackup.IncludeDevTools.Should().BeTrue();
    _configManager.SystemBackup.IncludeSshConfig.Should().BeTrue();
    _configManager.SystemBackup.DevToolsStorageType.Should().Be("s3");
  }

  [Fact]
  public void LoadEncryptionRetentionAndChunkSettings_ShouldPopulateValues()
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
  },
  "chunkDiffing": {
    "manifestVersion": 3,
    "minChunkSizeKB": 16,
    "targetChunkSizeKB": 64,
    "maxChunkSizeKB": 256,
    "rollingHashWindowSize": 32,
    "maxChunksPerFile": 10000,
    "maxFilesPerSnapshot": 5000
  }
}
""");

    InvokePrivate(_configManager, "LoadEncryptionSettings", root);
    InvokePrivate(_configManager, "LoadRetentionSettings", root);
    InvokePrivate(_configManager, "LoadChunkDiffingSettings", root);

    _configManager.Encryption.Enabled.Should().BeTrue();
    _configManager.Encryption.Salt.Should().Be("abc");
    _configManager.Encryption.KeyDerivationIterations.Should().Be(250000);
    _configManager.Encryption.VerificationToken.Should().Be("token");

    _configManager.Retention.Enabled.Should().BeTrue();
    _configManager.Retention.KeepLastPerDirectory.Should().Be(7);
    _configManager.Retention.MaxAgeDays.Should().Be(14);

    _configManager.ChunkDiffing.ManifestVersion.Should().Be(3);
    _configManager.ChunkDiffing.MinChunkSizeKB.Should().Be(16);
    _configManager.ChunkDiffing.TargetChunkSizeKB.Should().Be(64);
    _configManager.ChunkDiffing.MaxChunkSizeKB.Should().Be(256);
    _configManager.ChunkDiffing.RollingHashWindowSize.Should().Be(32);
    _configManager.ChunkDiffing.MaxChunksPerFile.Should().Be(10000);
    _configManager.ChunkDiffing.MaxFilesPerSnapshot.Should().Be(5000);
  }

  [Fact]
  public void LoadExclusions_ShouldKeepRawConfiguredPaths()
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
    _configManager.ExcludedPaths[0].Should().StartWith("%TEMP%");
    ConfigManager.ExpandConfiguredPath(_configManager.ExcludedPaths[0]).Should().NotContain("%TEMP%").And.Contain("restore-cache");
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

  [Theory]
  [InlineData("{ }", true, true, false)]
  [InlineData("{ \"telemetry\": { \"enabled\": false }, \"updates\": { \"checkAutomatically\": false, \"includePrereleases\": true } }", false, false, true)]
  [InlineData("{ \"telemetry\": { \"enabled\": \"no\" }, \"updates\": 3 }", true, true, false)]
  public void LoadTelemetryAndUpdatesSettings_ReadsValuesAndDefaultsInvalidOnes(string json, bool telemetry, bool check, bool prereleases)
  {
    var manager = new ConfigManager(new TestLogger());
    using var document = JsonDocument.Parse(json);

    typeof(ConfigManager).GetMethod("LoadTelemetrySettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, [document.RootElement]);
    typeof(ConfigManager).GetMethod("LoadUpdatesSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, [document.RootElement]);

    manager.Telemetry.Enabled.Should().Be(telemetry);
    manager.Updates.CheckAutomatically.Should().Be(check);
    manager.Updates.IncludePrereleases.Should().Be(prereleases);
  }

  [Fact]
  public async Task SaveAsync_ShouldPersistTelemetryAndUpdatesSettings()
  {
    var tempDir = Path.Combine(Path.GetTempPath(), "ReStoreConfigTelemetry_" + Guid.NewGuid().ToString("N"));
    var configPath = Path.Combine(tempDir, "config.json");

    try
    {
      _configManager.Telemetry.Enabled.Should().BeTrue("telemetry is on by default");
      _configManager.Telemetry.Enabled = false;
      _configManager.Updates.CheckAutomatically = false;
      _configManager.Updates.IncludePrereleases = true;

      await _configManager.SaveAsync(configPath);

      using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
      doc.RootElement.GetProperty("telemetry").GetProperty("enabled").GetBoolean().Should().BeFalse();
      doc.RootElement.GetProperty("updates").GetProperty("checkAutomatically").GetBoolean().Should().BeFalse();
      doc.RootElement.GetProperty("updates").GetProperty("includePrereleases").GetBoolean().Should().BeTrue();
    }
    finally
    {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
  }

  [Fact]
  public async Task SaveAsync_ShouldPersistProtectionAutoStart()
  {
    var tempDir = Path.Combine(Path.GetTempPath(), "ReStoreConfigProtection_" + Guid.NewGuid().ToString("N"));
    var configPath = Path.Combine(tempDir, "config.json");

    try
    {
      _configManager.Protection.AutoStart.Should().BeTrue("auto-start is on unless the user turns it off");
      _configManager.Protection.AutoStart = false;

      await _configManager.SaveAsync(configPath);

      using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
      doc.RootElement.GetProperty("protection").GetProperty("autoStart").GetBoolean().Should().BeFalse();
    }
    finally
    {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
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
      SetProperty(_configManager, nameof(ConfigManager.BackupType), BackupType.ChunkSnapshot);
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
      SetProperty(_configManager, nameof(ConfigManager.ChunkDiffing), new ChunkDiffingConfig
      {
        ManifestVersion = 2,
        MinChunkSizeKB = 16,
        TargetChunkSizeKB = 64,
        MaxChunkSizeKB = 128,
        RollingHashWindowSize = 32,
        MaxChunksPerFile = 1234,
        MaxFilesPerSnapshot = 4321
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

      root.GetProperty("configSchemaVersion").GetInt32().Should().Be(ConfigSchemaManager.CURRENT_CONFIG_SCHEMA_VERSION);
      root.GetProperty("globalStorageType").GetString().Should().Be("local");
      root.GetProperty("backupInterval").GetString().Should().Be("00:15:00");
      root.GetProperty("sizeThresholdMB").GetInt64().Should().Be(123);
      root.GetProperty("maxFileSizeMB").GetInt32().Should().Be(45);
      root.GetProperty("backupType").GetString().Should().Be("ChunkSnapshot");
      var watchDirectory = root.GetProperty("watchDirectories").EnumerateArray().Should().ContainSingle().Subject;
      watchDirectory.GetProperty("path").GetString().Should().Be(@"C:\\Data");
      watchDirectory.GetProperty("storageType").GetString().Should().Be("local");
      root.GetProperty("retention").GetProperty("keepLastPerDirectory").GetInt32().Should().Be(3);
      root.GetProperty("systemBackup").GetProperty("enabled").GetBoolean().Should().BeTrue();
      root.GetProperty("systemBackup").GetProperty("backupInterval").GetString().Should().Be("06:00:00");
      root.GetProperty("encryption").GetProperty("verificationToken").GetString().Should().Be("token");
      var chunkDiffing = root.GetProperty("chunkDiffing");
      chunkDiffing.GetProperty("manifestVersion").GetInt32().Should().Be(2);
      chunkDiffing.GetProperty("targetChunkSizeKB").GetInt32().Should().Be(64);
      chunkDiffing.GetProperty("maxChunksPerFile").GetInt32().Should().Be(1234);
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
