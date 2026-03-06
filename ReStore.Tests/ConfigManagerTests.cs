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
