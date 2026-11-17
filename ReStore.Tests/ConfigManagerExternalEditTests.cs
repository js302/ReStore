using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>I4(a): a ConfigManager whose copy is stale (the CLI or another process edited config.json) must merge, not overwrite.</summary>
public class ConfigManagerExternalEditTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ReStoreExternalEdit_" + Guid.NewGuid().ToString("N"));
    private readonly string _watched;
    private string ConfigPath => Path.Combine(_directory, "config.json");

    public ConfigManagerExternalEditTests()
    {
        Directory.CreateDirectory(_directory);
        _watched = Path.Combine(_directory, "watched");
        Directory.CreateDirectory(_watched);
        var root = ConfigSchema.LoadExample();
        root["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = _watched, ["storageType"] = null });
        root["globalStorageType"] = "local";
        root["storageSources"] = new JsonObject { ["local"] = new JsonObject { ["path"] = Path.Combine(_directory, "storage"), ["options"] = new JsonObject() } };
        File.WriteAllText(ConfigPath, root.ToJsonString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private async Task<(ConfigManager Manager, TestLogger Logger)> LoadAsync()
    {
        var logger = new TestLogger();
        var manager = new ConfigManager(logger, ConfigPath);
        await manager.LoadAsync();
        return (manager, logger);
    }

    private JsonObject Disk() => (JsonObject)JsonNode.Parse(File.ReadAllText(ConfigPath))!;

    /// <summary>What the CLI does: its own manager, an edit of the JSON document, a write.</summary>
    private async Task CliEditAsync(Action<JsonObject> edit)
    {
        var (cli, _) = await LoadAsync();
        var result = await ConfigEditor.ApplyAsync(cli, edit);
        result.Applied.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task StaleSave_KeepsAScheduleSetByTheCli_AndTheGuiChange()
    {
        var (gui, _) = await LoadAsync();
        await CliEditAsync(root =>
        {
            ((JsonObject)root["watchDirectories"]![0]!)["schedule"] = "daily 02:00";
            root["backupWindow"] = new JsonObject { ["enabled"] = true, ["start"] = "22:00", ["end"] = "06:00", ["days"] = "all" };
        });

        gui.Retention.KeepDaily = 9;
        await gui.SaveAsync();

        var disk = Disk();
        disk["retention"]!["keepDaily"]!.GetValue<int>().Should().Be(9, "the GUI change is saved");
        disk["watchDirectories"]![0]!["schedule"]!.GetValue<string>().Should().Be("daily 02:00", "the CLI change survives an unrelated GUI save");
        disk["backupWindow"]!["start"]!.GetValue<string>().Should().Be("22:00");
        gui.WatchDirectories[0].Schedule.Should().Be("daily 02:00", "the in-memory model now shows what is on disk");
        gui.BackupWindow.Should().NotBeNull();

        gui.Retention.KeepWeekly = 3;
        await gui.SaveAsync();
        Disk()["watchDirectories"]![0]!["schedule"]!.GetValue<string>().Should().Be("daily 02:00", "a second save does not revert it either");
    }

    [Fact]
    public async Task StaleSave_KeepsAChangedPasswordToken_EvenWhenTheStaleCopyChangedItsOwn()
    {
        var (gui, _) = await LoadAsync();
        await CliEditAsync(root => root["encryption"] = new JsonObject
        {
            ["enabled"] = true, ["salt"] = "bmV3c2FsdA==", ["keyDerivationIterations"] = 5000, ["verificationToken"] = "bmV3LXRva2Vu"
        });

        gui.Encryption.VerificationToken = "c3RhbGUtdG9rZW4=";
        gui.Encryption.Salt = "c3RhbGU=";
        gui.BackupInterval = TimeSpan.FromHours(3);
        await gui.SaveAsync();

        var encryption = Disk()["encryption"]!;
        encryption["verificationToken"]!.GetValue<string>().Should().Be("bmV3LXRva2Vu");
        encryption["salt"]!.GetValue<string>().Should().Be("bmV3c2FsdA==");
        encryption["enabled"]!.GetValue<bool>().Should().BeTrue();
        encryption["keyDerivationIterations"]!.GetValue<int>().Should().Be(5000);
        Disk()["backupInterval"]!.GetValue<string>().Should().Be("03:00:00");
        gui.Encryption.VerificationToken.Should().Be("bmV3LXRva2Vu");
    }

    [Fact]
    public async Task StaleSave_WithAnUnrelatedGuiChange_DoesNotTouchEncryptionItNeverChanged()
    {
        var (gui, _) = await LoadAsync();
        await CliEditAsync(root => root["encryption"] = new JsonObject
        {
            ["enabled"] = true, ["salt"] = "bmV3c2FsdA==", ["keyDerivationIterations"] = 5000, ["verificationToken"] = "dDI="
        });

        gui.Notifications.Enabled = false;
        await gui.SaveAsync();

        Disk()["encryption"]!["verificationToken"]!.GetValue<string>().Should().Be("dDI=");
        Disk()["notifications"]!["enabled"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task SameKeyChangedByBoth_TheNewerGuiEditWins_AndAWarningIsLogged()
    {
        var (gui, logger) = await LoadAsync();
        await CliEditAsync(root => root["retention"]!["keepDaily"] = 5);

        gui.Retention.KeepDaily = 7;
        await gui.SaveAsync();

        Disk()["retention"]!["keepDaily"]!.GetValue<int>().Should().Be(7);
        logger.Messages.Should().Contain(m => m.Contains("[Warning]") && m.Contains("retention.keepDaily"));
    }

    [Fact]
    public async Task WatchFolderAddedByGui_AndScheduleSetByCli_BothSurvive()
    {
        var (gui, _) = await LoadAsync();
        await CliEditAsync(root => ((JsonObject)root["watchDirectories"]![0]!)["schedule"] = "weekdays 06:30");

        var second = Path.Combine(_directory, "second");
        Directory.CreateDirectory(second);
        gui.WatchDirectories.Add(new WatchDirectoryConfig { Path = second });
        await gui.SaveAsync();

        var folders = Disk()["watchDirectories"]!.AsArray();
        folders.Should().HaveCount(2);
        folders[0]!["schedule"]!.GetValue<string>().Should().Be("weekdays 06:30");
        folders.Select(f => f!["path"]!.GetValue<string>()).Should().Contain(second);
    }

    [Fact]
    public async Task FolderRemovedByCli_IsNotBroughtBackByAStaleSave()
    {
        var (gui, _) = await LoadAsync();
        Directory.CreateDirectory(Path.Combine(_directory, "other-folder"));
        await CliEditAsync(root => root["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = Path.Combine(_directory, "other-folder") }));

        gui.Retention.KeepDaily = 4;
        await gui.SaveAsync();

        Disk()["watchDirectories"]!.AsArray().Select(f => Path.GetFileName(f!["path"]!.GetValue<string>())).Should().Equal("other-folder");
        gui.WatchDirectories.Should().ContainSingle();
    }

    [Fact]
    public async Task UnknownMembersAddedExternally_AreKept()
    {
        var (gui, _) = await LoadAsync();
        await CliEditAsync(root => root["futureFeature"] = new JsonObject { ["x"] = 1 });

        gui.Retention.KeepDaily = 2;
        await gui.SaveAsync();

        Disk()["futureFeature"]!["x"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task UnchangedFile_IsWrittenAsBefore_AndHasChangedOnDiskIsFalse()
    {
        var (gui, logger) = await LoadAsync();
        (await gui.HasChangedOnDiskAsync()).Should().BeFalse();

        gui.Retention.KeepDaily = 6;
        await gui.SaveAsync();

        Disk()["retention"]!["keepDaily"]!.GetValue<int>().Should().Be(6);
        (await gui.HasChangedOnDiskAsync()).Should().BeFalse("our own save is not an external change");
        logger.Messages.Should().NotContain(m => m.Contains("changed outside ReStore"));
    }

    [Fact]
    public async Task HasChangedOnDisk_IsTrueAfterAnExternalEdit_AndFalseAfterReload()
    {
        var (gui, _) = await LoadAsync();
        await CliEditAsync(root => root["retention"]!["keepDaily"] = 8);

        (await gui.HasChangedOnDiskAsync()).Should().BeTrue();
        await gui.LoadAsync();
        (await gui.HasChangedOnDiskAsync()).Should().BeFalse();
        gui.Retention.KeepDaily.Should().Be(8);
    }

    [Fact]
    public async Task ExternalFileThatIsNotJson_IsNotOverwrittenBySave()
    {
        var (gui, _) = await LoadAsync();
        File.WriteAllText(ConfigPath, "{ this is not json");

        gui.Retention.KeepDaily = 3;
        var act = () => gui.SaveAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed outside ReStore*");
        File.ReadAllText(ConfigPath).Should().Be("{ this is not json");
    }

    [Fact]
    public async Task StorageLayoutChangedByCli_IsKeptByAStaleSave()
    {
        var (gui, _) = await LoadAsync();
        await CliEditAsync(root =>
        {
            root["storageSources"] = new JsonObject { ["other"] = new JsonObject { ["path"] = Path.Combine(_directory, "o"), ["options"] = new JsonObject() } };
            root["globalStorageType"] = "other";
        });

        gui.Retention.KeepDaily = 1;
        await gui.SaveAsync();

        Disk()["globalStorageType"]!.GetValue<string>().Should().Be("other");
        Disk()["storageSources"]!.AsObject().Select(p => p.Key).Should().Equal("other");
        gui.GlobalStorageType.Should().Be("other");
    }

    [Fact]
    public async Task EveryWriter_UsesUniqueTempNames_AndLeavesNoneBehind()
    {
        var (gui, _) = await LoadAsync();
        for (var i = 0; i < 3; i++)
        {
            gui.Retention.KeepDaily = i;
            await gui.SaveAsync();
        }

        Directory.GetFiles(_directory, "*.tmp").Should().BeEmpty();
        var a = SharedFile.UniqueTempPath(ConfigPath);
        var b = SharedFile.UniqueTempPath(ConfigPath);
        a.Should().NotBe(b).And.StartWith(ConfigPath + ".").And.EndWith(".tmp").And.Contain("." + Environment.ProcessId + ".");
    }

    [Fact]
    public void AtomicWriters_RemoveTheirTempFile_WhenTheSwapFails()
    {
        // The destination is a directory, so the move fails after the temp file was written.
        var blocked = Path.Combine(_directory, "blocked.json");
        Directory.CreateDirectory(blocked);

        var act = () => SharedFile.WriteAllTextAtomic(blocked, "{}");

        act.Should().Throw<Exception>();
        Directory.GetFiles(_directory, "blocked.json.*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task ConfigSave_ThatFails_LeavesNoTempFile()
    {
        var (gui, _) = await LoadAsync();
        File.Delete(ConfigPath);
        Directory.CreateDirectory(ConfigPath);   // config.json is now a directory: the swap cannot succeed

        var act = () => gui.SaveAsync();

        await act.Should().ThrowAsync<Exception>();
        Directory.GetFiles(_directory, "config.json.*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Merger_ThreeWay_UnitCases()
    {
        JsonObject J(string text) => (JsonObject)JsonNode.Parse(text)!;
        var conflicts = new List<string>();

        var merged = ConfigMerger.Merge(
            J("""{"a":1,"b":{"x":1,"y":1},"l":[1]}"""),
            J("""{"a":2,"b":{"x":1,"y":1},"l":[1]}"""),
            J("""{"a":1,"b":{"x":5,"y":1},"l":[1,2],"n":true}"""),
            conflicts);

        merged["a"]!.GetValue<int>().Should().Be(2);
        merged["b"]!["x"]!.GetValue<int>().Should().Be(5);
        merged["l"]!.AsArray().Should().HaveCount(2);
        merged["n"]!.GetValue<bool>().Should().BeTrue();
        conflicts.Should().BeEmpty();

        var both = ConfigMerger.Merge(J("""{"a":1,"l":[1]}"""), J("""{"a":2,"l":[3]}"""), J("""{"a":3,"l":[2]}"""), conflicts);
        both["a"]!.GetValue<int>().Should().Be(2);
        conflicts.Should().BeEquivalentTo(["a", "l"]);
    }
}
