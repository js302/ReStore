using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>watchDirectories[].schedule, systemBackup.schedule and backupWindow: loading, saving, unknown members and validation.</summary>
[Collection("GlobalState")]
public class ConfigScheduleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreConfigSchedule_" + Guid.NewGuid().ToString("N"));
    private readonly string _watch;
    private readonly string _configPath;

    public ConfigScheduleTests()
    {
        _watch = Path.Combine(_root, "watch");
        Directory.CreateDirectory(_watch);
        _configPath = Path.Combine(_root, "config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private JsonObject BaseConfig()
    {
        var root = ConfigSchema.LoadExample();
        root["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = _watch, ["storageType"] = null });
        root["storageSources"] = new JsonObject { ["local"] = new JsonObject { ["path"] = Path.Combine(_root, "storage"), ["options"] = new JsonObject() } };
        root["globalStorageType"] = "local";
        return root;
    }

    private async Task<ConfigManager> LoadAsync(JsonObject root)
    {
        File.WriteAllText(_configPath, root.ToJsonString());
        var manager = new ConfigManager(new TestLogger(), _configPath);
        await manager.LoadAsync();
        return manager;
    }

    private JsonObject Saved() => JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();

    // ---- old configuration -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AConfigWithoutTheNewFields_LoadsWithNoSchedulesAndNoWindow_AndSavesWithoutThem()
    {
        var root = BaseConfig();
        root.Remove("backupWindow");
        var manager = await LoadAsync(root);

        manager.WatchDirectories[0].Schedule.Should().BeNull();
        manager.SystemBackup.Schedule.Should().BeNull();
        manager.BackupWindow.Should().BeNull();

        await manager.SaveAsync();

        var saved = Saved();
        saved.ContainsKey("backupWindow").Should().BeFalse();
        saved["systemBackup"]!.AsObject().ContainsKey("schedule").Should().BeFalse("optional fields are written only when set");
        saved["watchDirectories"]![0]!.AsObject().ContainsKey("schedule").Should().BeFalse();
        saved["backupInterval"]!.GetValue<string>().Should().Be("01:00:00");
    }

    [Fact]
    public async Task TheShippedExample_HasADisabledWindow_ThatChangesNothing()
    {
        var manager = await LoadAsync(BaseConfig());

        manager.BackupWindow.Should().NotBeNull();
        manager.BackupWindow!.Enabled.Should().BeFalse();
        ReStore.Core.src.monitoring.BackupWindow.FromConfig(manager.BackupWindow, out _).Should().BeNull("a disabled window applies nothing");
    }

    // ---- round trips -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheNewFields_LoadAndRoundTrip_WithUnknownMembersKept()
    {
        var root = BaseConfig();
        root["watchDirectories"] = new JsonArray(
            new JsonObject { ["path"] = _watch, ["storageType"] = null, ["schedule"] = "weekdays 18:30", ["futureFlag"] = true },
            new JsonObject { ["path"] = _root, ["storageType"] = null });
        root["systemBackup"]!["schedule"] = "weekly sun 03:00";
        root["backupWindow"] = new JsonObject { ["enabled"] = true, ["start"] = "23:00", ["end"] = "05:30", ["days"] = "mon-fri", ["note"] = "kept" };
        root["topLevelUnknown"] = new JsonObject { ["x"] = 1 };
        var manager = await LoadAsync(root);

        manager.WatchDirectories[0].Schedule.Should().Be("weekdays 18:30");
        manager.WatchDirectories[1].Schedule.Should().BeNull();
        manager.SystemBackup.Schedule.Should().Be("weekly sun 03:00");
        manager.BackupWindow.Should().BeEquivalentTo(new { Enabled = true, Start = "23:00", End = "05:30", Days = "mon-fri" });

        await manager.SaveAsync();

        var saved = Saved();
        saved["watchDirectories"]![0]!["schedule"]!.GetValue<string>().Should().Be("weekdays 18:30");
        saved["watchDirectories"]![0]!["futureFlag"]!.GetValue<bool>().Should().BeTrue();
        saved["watchDirectories"]![1]!.AsObject().ContainsKey("schedule").Should().BeFalse();
        saved["systemBackup"]!["schedule"]!.GetValue<string>().Should().Be("weekly sun 03:00");
        saved["backupWindow"]!["start"]!.GetValue<string>().Should().Be("23:00");
        saved["backupWindow"]!["end"]!.GetValue<string>().Should().Be("05:30");
        saved["backupWindow"]!["days"]!.GetValue<string>().Should().Be("mon-fri");
        saved["backupWindow"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        saved["backupWindow"]!["note"]!.GetValue<string>().Should().Be("kept");
        saved["topLevelUnknown"]!["x"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task ClearingTheFields_RemovesThemFromTheFile_InsteadOfRestoringThemFromTheOldFile()
    {
        var root = BaseConfig();
        root["watchDirectories"]![0]!["schedule"] = "daily 02:00";
        root["systemBackup"]!["schedule"] = "daily 03:00";
        root["backupWindow"] = new JsonObject { ["enabled"] = true, ["start"] = "22:00", ["end"] = "06:00", ["days"] = "all" };
        var manager = await LoadAsync(root);

        manager.WatchDirectories[0].Schedule = null;
        manager.SystemBackup.Schedule = null;
        manager.BackupWindow = null;
        await manager.SaveAsync();

        var saved = Saved();
        saved["watchDirectories"]![0]!.AsObject().ContainsKey("schedule").Should().BeFalse();
        saved["systemBackup"]!.AsObject().ContainsKey("schedule").Should().BeFalse();
        saved.ContainsKey("backupWindow").Should().BeFalse();

        var reloaded = new ConfigManager(new TestLogger(), _configPath);
        await reloaded.LoadAsync();
        reloaded.WatchDirectories[0].Schedule.Should().BeNull();
        reloaded.BackupWindow.Should().BeNull();
    }

    [Fact]
    public async Task AWindowWithoutAnEnabledFlag_IsEnabled_AndMissingTimesGetTheDefaults()
    {
        var root = BaseConfig();
        root["backupWindow"] = new JsonObject { ["start"] = "20:00" };
        var manager = await LoadAsync(root);

        manager.BackupWindow!.Enabled.Should().BeTrue();
        manager.BackupWindow.Start.Should().Be("20:00");
        manager.BackupWindow.End.Should().Be("06:00");
        manager.BackupWindow.Days.Should().Be("all");
    }

    [Fact]
    public async Task ABlankSystemSchedule_IsTreatedAsNone()
    {
        var root = BaseConfig();
        root["systemBackup"]!["schedule"] = "  ";
        var manager = await LoadAsync(root);

        manager.SystemBackup.Schedule.Should().BeNull();
    }

    [Fact]
    public async Task ASystemScheduleOfNull_Loads()
    {
        var root = BaseConfig();
        root["systemBackup"]!["schedule"] = null;
        var manager = await LoadAsync(root);

        manager.SystemBackup.Schedule.Should().BeNull();
    }

    // ---- validation ------------------------------------------------------------------------------------------------

    private Mock<IConfigManager> ValidatorConfig(string? folderSchedule = null, string? systemSchedule = null, BackupWindowConfig? window = null)
    {
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.WatchDirectories).Returns([new WatchDirectoryConfig { Path = _watch, Schedule = folderSchedule }]);
        config.SetupGet(c => c.GlobalStorageType).Returns("local");
        config.SetupGet(c => c.BackupInterval).Returns(TimeSpan.FromHours(1));
        config.SetupGet(c => c.SizeThresholdMB).Returns(500);
        config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        config.SetupGet(c => c.BackupType).Returns(BackupType.Incremental);
        config.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig { Schedule = systemSchedule });
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig());
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        config.SetupGet(c => c.ExcludedPatterns).Returns([]);
        config.SetupGet(c => c.ExcludedPaths).Returns([]);
        config.SetupGet(c => c.BackupWindow).Returns(window);
        config.SetupGet(c => c.StorageSources).Returns(new Dictionary<string, StorageConfig>
        {
            ["local"] = new() { Path = Path.Combine(_root, "storage"), Options = [] }
        });
        return config;
    }

    private static ConfigValidationResult Validate(Mock<IConfigManager> config) => new ConfigValidator(new TestLogger()).ValidateConfiguration(config.Object);

    [Fact]
    public void ValidSchedulesAndWindow_Pass()
    {
        var result = Validate(ValidatorConfig("weekdays 18:30", "off", new BackupWindowConfig { Start = "22:00", End = "06:00", Days = "mon-fri" }));

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void NoSchedulesAndNoWindow_AreFine()
    {
        Validate(ValidatorConfig()).Errors.Should().BeEmpty();
    }

    [Fact]
    public void AnInvalidFolderSchedule_IsAnError_WithTheParsersMessage()
    {
        var result = Validate(ValidatorConfig(folderSchedule: "daily 2pm"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should()
            .Contain(_watch).And.Contain("invalid schedule 'daily 2pm'").And.Contain("expected HH:mm at 'daily 2pm'");
    }

    [Fact]
    public void AnInvalidSystemSchedule_IsAnError()
    {
        var result = Validate(ValidatorConfig(systemSchedule: "every 1m"));

        result.Errors.Should().ContainSingle().Which.Should().Contain("systemBackup.schedule").And.Contain("minimum is 5m");
    }

    [Theory]
    [InlineData("25:00", "06:00", "all", "backup window start")]
    [InlineData("22:00", "6am", "all", "backup window end")]
    [InlineData("22:00", "06:00", "someday", "backup window days")]
    public void AnInvalidWindow_IsAnError(string start, string end, string days, string expected)
    {
        var result = Validate(ValidatorConfig(window: new BackupWindowConfig { Start = start, End = end, Days = days }));

        result.Errors.Should().ContainSingle().Which.Should().Contain("backupWindow is invalid").And.Contain(expected);
    }

    [Fact]
    public void ASchedule_OnAnEmptyWatchEntry_IsNotValidated()
    {
        var config = ValidatorConfig();
        config.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watch },
            new WatchDirectoryConfig { Path = "", Schedule = "garbage" }
        ]);

        Validate(config).Errors.Should().BeEmpty();
    }
}
