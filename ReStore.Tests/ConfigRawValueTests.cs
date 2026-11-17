using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Raw (unexpanded) values and unknown members must survive a load/save round trip (H18).</summary>
[Collection("GlobalState")]
public class ConfigRawValueTests : IDisposable
{
    private const string SampleConfig = """
    {
      "configSchemaVersion": 4,
      "futureTopLevel": { "a": 1 },
      "watchDirectories": [
        "%USERPROFILE%\\Docs",
        { "path": "%TEMP%\\watch", "storageType": "s3", "futureWatchKey": true }
      ],
      "globalStorageType": "s3",
      "backupInterval": "01:00:00",
      "sizeThresholdMB": 500,
      "retention": { "enabled": true, "keepLastPerDirectory": 5, "maxAgeDays": 10, "futureRetention": "x" },
      "storageSources": {
        "local": { "path": "%TEMP%\\store", "options": { "path": "%TEMP%\\store" } },
        "s3": {
          "path": "s3://bucket",
          "futureSourceKey": 7,
          "options": { "accessKeyId": "AKIA", "secretAccessKey": "%MY_SECRET%", "hiddenOption": "keep" }
        }
      },
      "excludedPatterns": ["*.tmp"],
      "excludedPaths": ["%TEMP%\\cache"]
    }
    """;

    private readonly ConfigManager _manager = new(new TestLogger());
    private readonly string _configPath;

    public ConfigRawValueTests()
    {
        _configPath = _manager.GetConfigFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        File.WriteAllText(_configPath, SampleConfig);
        Environment.SetEnvironmentVariable("MY_SECRET", "super-secret-value");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MY_SECRET", null);
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Fact]
    public async Task Save_WritesRawValuesBack_NotExpandedSecrets()
    {
        await _manager.LoadAsync();
        _manager.StorageSources["s3"].Options["secretAccessKey"].Should().Be("%MY_SECRET%");
        _manager.StorageSources["s3"].Expanded().Options["secretAccessKey"].Should().Be("super-secret-value");

        await _manager.SaveAsync();

        var text = await File.ReadAllTextAsync(_configPath);
        text.Should().NotContain("super-secret-value");
        var root = JsonNode.Parse(text)!;
        root["storageSources"]!["s3"]!["options"]!["secretAccessKey"]!.GetValue<string>().Should().Be("%MY_SECRET%");
        root["storageSources"]!["local"]!["path"]!.GetValue<string>().Should().Be("%TEMP%\\store");
        root["watchDirectories"]![0]!["path"]!.GetValue<string>().Should().Be("%USERPROFILE%\\Docs");
        root["watchDirectories"]![1]!["path"]!.GetValue<string>().Should().Be("%TEMP%\\watch");
        root["excludedPaths"]![0]!.GetValue<string>().Should().Be("%TEMP%\\cache");
    }

    [Fact]
    public async Task Save_PreservesUnknownMembers_AtEveryLevel()
    {
        await _manager.LoadAsync();
        _manager.Retention.KeepLastPerDirectory = 9;

        await _manager.SaveAsync();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!;
        root["futureTopLevel"]!["a"]!.GetValue<int>().Should().Be(1);
        root["retention"]!["futureRetention"]!.GetValue<string>().Should().Be("x");
        root["retention"]!["keepLastPerDirectory"]!.GetValue<int>().Should().Be(9);
        root["storageSources"]!["s3"]!["futureSourceKey"]!.GetValue<int>().Should().Be(7);
        root["watchDirectories"]![1]!["futureWatchKey"]!.GetValue<bool>().Should().BeTrue();
        root["globalStorageType"]!.GetValue<string>().Should().Be("s3");
    }

    [Fact]
    public async Task Save_DoesNotResurrectRemovedStorageSourcesOrWatchDirectories()
    {
        await _manager.LoadAsync();
        _manager.StorageSources.Remove("local");
        _manager.WatchDirectories.RemoveAt(0);

        await _manager.SaveAsync();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!;
        root["storageSources"]!.AsObject().ContainsKey("local").Should().BeFalse();
        root["watchDirectories"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Save_ThenLoad_IsStable()
    {
        await _manager.LoadAsync();
        await _manager.SaveAsync();
        var first = await File.ReadAllTextAsync(_configPath);

        var second = new ConfigManager(new TestLogger());
        await second.LoadAsync();
        await second.SaveAsync();

        (await File.ReadAllTextAsync(_configPath)).Should().Be(first);
    }

    [Fact]
    public async Task CreateStorage_UsesExpandedCopy_WithoutMutatingModel()
    {
        await _manager.LoadAsync();
        var model = _manager.StorageSources["local"];

        var provider = ConfigManager.BuildProviderConfig(model);

        provider.Options["path"].Should().NotContain("%");
        model.Options["path"].Should().StartWith("%TEMP%");
    }

    [Fact]
    public void MergeOptions_KeepsUnmentionedKeys_AndOverwritesUpdated()
    {
        var existing = new Dictionary<string, string> { ["a"] = "1", ["hidden"] = "keep", ["b"] = "old" };

        var merged = StorageConfig.MergeOptions(existing, new Dictionary<string, string> { ["b"] = "new", ["c"] = "3", ["blankNew"] = "", ["a"] = "" });

        merged.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["a"] = "", ["hidden"] = "keep", ["b"] = "new", ["c"] = "3"
        });
        existing["b"].Should().Be("old", "the input is not mutated");
    }

    [Fact]
    public async Task CreateStorage_FromUnsavedConfig_ExpandsWithoutTouchingModel()
    {
        await _manager.LoadAsync();
        var dir = Path.Combine(Path.GetTempPath(), "ReStoreUnsaved_" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("RESTORE_UNSAVED_DIR", dir);
        try
        {
            var unsaved = new StorageConfig { Options = new() { ["path"] = "%RESTORE_UNSAVED_DIR%" } };

            using var storage = await _manager.CreateStorageAsync("local", unsaved);

            storage.Should().BeOfType<ReStore.Core.src.storage.local.LocalStorage>();
            unsaved.Options["path"].Should().Be("%RESTORE_UNSAVED_DIR%");
            _manager.StorageSources["local"].Options["path"].Should().StartWith("%TEMP%");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESTORE_UNSAVED_DIR", null);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Validator_ExpandsEnvReferencesBeforeCheckingKeyFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ReStoreKey_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "id_rsa"), "x");
        Environment.SetEnvironmentVariable("RESTORE_KEY_DIR", dir);
        try
        {
            var cfg = new ConfigManager(new TestLogger());
            typeof(ConfigManager).GetProperty(nameof(ConfigManager.StorageSources))!.SetValue(cfg, new Dictionary<string, StorageConfig>
            {
                ["sftp"] = new() { Path = "sftp", Options = new() { ["host"] = "h", ["username"] = "u", ["privateKeyPath"] = "%RESTORE_KEY_DIR%/id_rsa" } }
            });

            var result = cfg.ValidateConfiguration();

            result.Warnings.Should().NotContain(w => w.Contains("private key file not found"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESTORE_KEY_DIR", null);
            Directory.Delete(dir, true);
        }
    }
}
