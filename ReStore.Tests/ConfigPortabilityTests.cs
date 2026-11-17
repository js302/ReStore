using FluentAssertions;
using ReStore.Core.src.utils;
using System.Text.Json.Nodes;

namespace ReStore.Tests;

[Collection("GlobalState")]
public class ConfigPortabilityTests : IDisposable
{
    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStorePortabilityTests_" + Guid.NewGuid().ToString("N"));
    private readonly string _watch;
    private readonly string _storage;

    public ConfigPortabilityTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot(_root);
        _watch = Path.Combine(_root, "watch");
        _storage = Path.Combine(_root, "storage");
        Directory.CreateDirectory(_watch);
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot(_previousRoot);
        try { Directory.Delete(_root, true); } catch { }
    }

    private JsonObject BaseConfig()
    {
        // The shipped example carries every section the loader requires; then point it at temp locations.
        ConfigInitializer.EnsureConfigurationSetup();
        var root = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        root["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = _watch });
        root["globalStorageType"] = "local";
        root["excludedPaths"] = new JsonArray();
        root["storageSources"] = new JsonObject
        {
            ["local"] = new JsonObject { ["path"] = _storage },
            ["s3"] = new JsonObject
            {
                ["options"] = new JsonObject
                {
                    ["bucket"] = "my-bucket",
                    ["region"] = "us-east-1",
                    ["accessKeyId"] = "AKIA123456",
                    ["secretAccessKey"] = "SUPERSECRET"
                }
            }
        };
        return root;
    }

    private async Task<ConfigManager> LoadCurrentAsync(JsonObject config)
    {
        File.WriteAllText(AppPaths.ConfigFile, config.ToJsonString());
        var manager = new ConfigManager(new TestLogger());
        await manager.LoadAsync();
        return manager;
    }

    private string WriteImportFile(JsonObject config, JsonObject? appSettings = null)
    {
        var path = Path.Combine(_root, "import_" + Guid.NewGuid().ToString("N") + ".json");
        var document = new JsonObject { ["restoreSettingsExport"] = 1, ["config"] = config };
        if (appSettings != null)
        {
            document["appSettings"] = appSettings;
        }

        File.WriteAllText(path, document.ToJsonString());
        return path;
    }

    [Fact]
    public void BuildExport_RemovesEverySecretOption_ForEveryProviderKey()
    {
        var options = new JsonObject { ["bucket"] = "b" };
        foreach (var key in SecretReferences.SecretOptionNames)
        {
            options[key] = "plain-" + key;
            options[key.ToUpperInvariant() == key ? key + "_x" : key.ToUpperInvariant()] = "dup";
        }

        var config = new JsonObject { ["storageSources"] = new JsonObject { ["any"] = new JsonObject { ["options"] = options } } };

        var export = ConfigPortability.BuildExport(config.ToJsonString());

        export.Should().NotContain("plain-").And.NotContain("dup");
        var exportedOptions = JsonNode.Parse(export)!["config"]!["storageSources"]!["any"]!["options"]!.AsObject();
        exportedOptions["bucket"]!.GetValue<string>().Should().Be("b");
        foreach (var key in SecretReferences.SecretOptionNames)
        {
            exportedOptions.Any(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase)).Should().BeFalse(key);
        }
    }

    [Fact]
    public void BuildExport_RemovesDpapiBlobs_ButKeepsEnvReferences()
    {
        var config = new JsonObject
        {
            ["storageSources"] = new JsonObject
            {
                ["s3"] = new JsonObject
                {
                    ["options"] = new JsonObject
                    {
                        ["secretAccessKey"] = "env:AWS_SECRET",
                        ["sessionToken"] = "dpapi:AAAA==",
                        ["customOption"] = "dpapi:BBBB=="
                    }
                }
            }
        };

        var export = JsonNode.Parse(ConfigPortability.BuildExport(config.ToJsonString()))!;

        var options = export["config"]!["storageSources"]!["s3"]!["options"]!.AsObject();
        options["secretAccessKey"]!.GetValue<string>().Should().Be("env:AWS_SECRET");
        options.ContainsKey("sessionToken").Should().BeFalse();
        options.ContainsKey("customOption").Should().BeFalse();
    }

    [Fact]
    public void BuildExport_IncludesOnlyKnownAppPreferences()
    {
        var export = JsonNode.Parse(ConfigPortability.BuildExport(
            "{}",
            "{\"MinimizeToTray\":true,\"MainWindowPlacement\":{\"Left\":1},\"DefaultStorage\":\"x\"}",
            "{\"Preference\":2}"))!;

        export["appSettings"]!.AsObject().Select(o => o.Key).Should().Equal("MinimizeToTray");
        export["theme"]!["Preference"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Import_Merge_KeepsCurrentSecretsSourcesAndDirectories_AndAppliesImportedValues()
    {
        var current = BaseConfig();
        var manager = await LoadCurrentAsync(current);

        var imported = BaseConfig();
        imported["maxFileSizeMB"] = 42;
        imported["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = _watch }, new JsonObject { ["path"] = Path.Combine(_root, "watch") + "2" });
        Directory.CreateDirectory(Path.Combine(_root, "watch2"));
        imported["storageSources"]!["s3"]!["options"]!.AsObject().Remove("secretAccessKey");
        imported["storageSources"]!["s3"]!["options"]!["bucket"] = "other-bucket";
        imported["storageSources"]!.AsObject().Remove("local");

        var result = await ConfigPortability.ImportAsync(manager, WriteImportFile(imported), ConfigImportMode.Merge);

        result.Applied.Should().BeTrue(string.Join("; ", result.Errors));
        manager.MaxFileSizeMB.Should().Be(42);
        manager.StorageSources.Should().ContainKey("local");
        manager.StorageSources["s3"].Options["bucket"].Should().Be("other-bucket");
        manager.StorageSources["s3"].Options["secretAccessKey"].Should().Be("SUPERSECRET");
        manager.WatchDirectories.Should().HaveCount(2);
        Directory.EnumerateFiles(Path.Combine(_root, "backups"), "config.pre-import.*").Should().HaveCount(1);
    }

    [Fact]
    public async Task Import_Replace_DropsMissingSources_ButKeepsSecretsOfMatchingProviders()
    {
        var manager = await LoadCurrentAsync(BaseConfig());

        var imported = BaseConfig();
        imported["storageSources"]!.AsObject().Remove("local");
        imported["globalStorageType"] = "s3";
        imported["storageSources"]!["s3"]!["options"]!.AsObject().Remove("secretAccessKey");

        var result = await ConfigPortability.ImportAsync(manager, WriteImportFile(imported), ConfigImportMode.Replace);

        result.Applied.Should().BeTrue(string.Join("; ", result.Errors));
        manager.StorageSources.Keys.Should().Equal("s3");
        manager.StorageSources["s3"].Options["secretAccessKey"].Should().Be("SUPERSECRET");
        manager.GlobalStorageType.Should().Be("s3");
        result.Notes.Should().Contain(n => n.Contains("s3.secretAccessKey") && !n.Contains("SUPERSECRET"));
    }

    [Fact]
    public async Task Import_Replace_DoesNotInventSecretsForNewProviders()
    {
        var manager = await LoadCurrentAsync(BaseConfig());
        var imported = BaseConfig();
        imported["storageSources"]!.AsObject().Remove("s3");
        imported["storageSources"]!["azure"] = new JsonObject { ["options"] = new JsonObject { ["accountName"] = "acct" } };

        var result = await ConfigPortability.ImportAsync(manager, WriteImportFile(imported), ConfigImportMode.Replace);

        result.Applied.Should().BeTrue(string.Join("; ", result.Errors));
        manager.StorageSources["azure"].Options.Keys.Should().NotContain("connectionString");
        manager.StorageSources.Should().NotContainKey("s3");
    }

    [Fact]
    public async Task Import_ValidationFailure_LeavesConfigUntouched()
    {
        var manager = await LoadCurrentAsync(BaseConfig());
        var before = File.ReadAllText(AppPaths.ConfigFile);

        var imported = BaseConfig();
        imported["logging"] = new JsonObject { ["level"] = "Nonsense" };

        var result = await ConfigPortability.ImportAsync(manager, WriteImportFile(imported), ConfigImportMode.Merge);

        result.Applied.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("logging.level"));
        File.ReadAllText(AppPaths.ConfigFile).Should().Be(before);
        Directory.Exists(Path.Combine(_root, "backups")).Should().BeFalse();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"hello\":1}")]
    public async Task Import_MalformedFile_IsRejected_WithoutChanges(string content)
    {
        var manager = await LoadCurrentAsync(BaseConfig());
        var before = File.ReadAllText(AppPaths.ConfigFile);
        var file = Path.Combine(_root, "bad.json");
        File.WriteAllText(file, content);

        var result = await ConfigPortability.ImportAsync(manager, file, ConfigImportMode.Merge);

        result.Applied.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        File.ReadAllText(AppPaths.ConfigFile).Should().Be(before);
    }

    [Fact]
    public async Task Import_MissingFile_IsRejected()
    {
        var manager = await LoadCurrentAsync(BaseConfig());

        var result = await ConfigPortability.ImportAsync(manager, Path.Combine(_root, "nope.json"), ConfigImportMode.Merge);

        result.Applied.Should().BeFalse();
    }

    [Fact]
    public async Task Import_AppliesAppPreferences_WithoutTouchingOtherKeys()
    {
        var manager = await LoadCurrentAsync(BaseConfig());
        File.WriteAllText(AppPaths.AppSettingsFile, "{\"MinimizeToTray\":false,\"MainWindowPlacement\":{\"Left\":5}}");

        var result = await ConfigPortability.ImportAsync(manager,
            WriteImportFile(BaseConfig(), new JsonObject { ["MinimizeToTray"] = true, ["Evil"] = "x" }), ConfigImportMode.Merge);

        result.Applied.Should().BeTrue(string.Join("; ", result.Errors));
        ConfigPortability.ChangedAppPreferences(result).Should().BeTrue();
        var saved = JsonNode.Parse(File.ReadAllText(AppPaths.AppSettingsFile))!.AsObject();
        saved["MinimizeToTray"]!.GetValue<bool>().Should().BeTrue();
        saved["MainWindowPlacement"].Should().NotBeNull();
        saved.ContainsKey("Evil").Should().BeFalse();
    }

    [Fact]
    public async Task ExportThenImport_RoundTripsThroughTheManager()
    {
        var manager = await LoadCurrentAsync(BaseConfig());
        var export = await ConfigPortability.ExportAsync(manager);
        export.Should().NotContain("SUPERSECRET");
        var file = Path.Combine(_root, "export.json");
        File.WriteAllText(file, export);

        var result = await ConfigPortability.ImportAsync(manager, file, ConfigImportMode.Replace);

        result.Applied.Should().BeTrue(string.Join("; ", result.Errors));
        manager.StorageSources["s3"].Options["secretAccessKey"].Should().Be("SUPERSECRET");
    }

    [Fact]
    public void Diagnostics_RedactsSecretsInConfigAndLogLines()
    {
        var config = BaseConfig().ToJsonString();
        var lines = new[]
        {
            "[x] password=hunter2 and secretAccessKey: \"SUPERSECRET\"",
            "Authorization: Bearer abc.def-ghi",
            "connect https://user:p4ss@example.com/path blob dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA",
            "DefaultEndpointsProtocol=https;AccountKey=Zm9vYmFy;EndpointSuffix=core"
        };

        var report = DiagnosticsReport.Build("1.2.3", config, lines);

        report.Should().Contain("ReStore: 1.2.3").And.Contain("OS:").And.Contain(".NET:");
        report.Should().NotContain("SUPERSECRET").And.NotContain("hunter2").And.NotContain("abc.def-ghi")
            .And.NotContain("p4ss").And.NotContain("AQAAANCMnd8").And.NotContain("Zm9vYmFy");
        report.Should().Contain("my-bucket").And.Contain("AKIA123456");
    }

    [Fact]
    public void ReadLogTail_ReturnsOnlyTheLastLines_AndToleratesMissingFile()
    {
        var log = Path.Combine(_root, "t.log");
        File.WriteAllLines(log, Enumerable.Range(1, 500).Select(i => "line " + i));

        var tail = DiagnosticsReport.ReadLogTail(log, 200);

        tail.Should().HaveCount(200);
        tail[0].Should().Be("line 301");
        tail[^1].Should().Be("line 500");
        DiagnosticsReport.ReadLogTail(Path.Combine(_root, "none.log"), 200).Should().BeEmpty();
    }

    [Fact]
    public void BuildExport_RemovesCredentialedUrlsAndSignedQueries_ButKeepsAccessKeyId()
    {
        var config = new JsonObject
        {
            ["storageSources"] = new JsonObject
            {
                ["x"] = new JsonObject
                {
                    ["options"] = new JsonObject
                    {
                        ["accessKeyId"] = "AKIA1",
                        ["endpoint"] = "https://user:pw@host.example/path",
                        ["containerUrl"] = "https://a.blob.core.windows.net/c?sv=2022&se=2030&sp=rw&sig=abc%3D",
                        ["presigned"] = "https://b.s3.amazonaws.com/k?X-Amz-Signature=deadbeef",
                        ["plainUrl"] = "https://host.example/path",
                        ["token"] = JsonValue.Create(12345)
                    }
                }
            }
        };

        var options = JsonNode.Parse(ConfigPortability.BuildExport(config.ToJsonString()))!["config"]!["storageSources"]!["x"]!["options"]!.AsObject();

        options.Select(o => o.Key).Should().BeEquivalentTo("accessKeyId", "plainUrl");
    }

    [Fact]
    public void RedactConfigJson_MasksNonStringValuesUnderSecretKeys()
    {
        var json = "{\"storageSources\":{\"x\":{\"options\":{\"password\":12345,\"token\":{\"a\":\"b\"},\"region\":\"eu\"}}}}";

        var redacted = ConfigSecretsCommands.RedactConfigJson(json);

        redacted.Should().NotContain("12345").And.NotContain("\"a\"").And.Contain("eu");
    }

    [Fact]
    public void Diagnostics_RedactsPresignedAndSasQueryParameters()
    {
        var text = "GET https://a.blob.core.windows.net/c/b?sv=2022-11-02&se=2030-01-01T00%3A00Z&sp=rw&sig=SECRETSIG%3D&x=1 and https://s3/k?X-Amz-Credential=AKIAXYZ%2F2026&X-Amz-Signature=cafebabe&X-Amz-Security-Token=TOKEN123&keep=ok";

        var redacted = DiagnosticsReport.RedactText(text);

        redacted.Should().NotContain("SECRETSIG").And.NotContain("cafebabe").And.NotContain("TOKEN123").And.NotContain("AKIAXYZ")
            .And.NotContain("2022-11-02").And.NotContain("2030-01-01").And.NotContain("sp=rw");
        redacted.Should().Contain("keep=ok").And.Contain("x=1");
    }
}
