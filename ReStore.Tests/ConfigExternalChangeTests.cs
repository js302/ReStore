using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>I4(b)/(c): detecting an external config.json edit, deciding what the app does, and reloading only what changed.</summary>
public class ConfigExternalChangeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ReStoreExternalChange_" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_directory, "config.json");

    public ConfigExternalChangeTests()
    {
        Directory.CreateDirectory(_directory);
        var watched = Path.Combine(_directory, "watched");
        Directory.CreateDirectory(watched);
        var root = ConfigSchema.LoadExample();
        root["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = watched, ["storageType"] = null });
        root["globalStorageType"] = "local";
        root["storageSources"] = new JsonObject { ["local"] = new JsonObject { ["path"] = Path.Combine(_directory, "storage"), ["options"] = new JsonObject() } };
        File.WriteAllText(ConfigPath, root.ToJsonString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private async Task<ConfigManager> LoadAsync()
    {
        var manager = new ConfigManager(new TestLogger(), ConfigPath);
        await manager.LoadAsync();
        return manager;
    }

    private async Task ExternalEditAsync(Action<JsonObject> edit)
    {
        var cli = await LoadAsync();
        (await ConfigEditor.ApplyAsync(cli, edit)).Applied.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, false, ExternalConfigAction.None)]
    [InlineData(false, true, ExternalConfigAction.None)]
    [InlineData(true, false, ExternalConfigAction.ReloadSilently)]
    [InlineData(true, true, ExternalConfigAction.AskUser)]
    public void Policy_ReloadsSilently_OnlyWhenSettingsHasNoUnsavedEdits(bool changed, bool dirty, ExternalConfigAction expected)
    {
        ExternalConfigChangePolicy.Decide(changed, dirty).Should().Be(expected);
    }

    [Fact]
    public async Task Reload_Unchanged_DoesNothing()
    {
        var manager = await LoadAsync();

        (await ConfigReloader.ReloadIfChangedAsync(manager)).Outcome.Should().Be(ConfigReloadOutcome.Unchanged);
    }

    [Fact]
    public async Task Reload_AScheduleChange_AsksForARestart_AnUnrelatedChange_DoesNot()
    {
        var manager = await LoadAsync();

        await ExternalEditAsync(root => root["retention"]!["keepDaily"] = 12);
        (await ConfigReloader.ReloadIfChangedAsync(manager)).Outcome.Should().Be(ConfigReloadOutcome.Reloaded);
        manager.Retention.KeepDaily.Should().Be(12);

        await ExternalEditAsync(root => ((JsonObject)root["watchDirectories"]![0]!)["schedule"] = "daily 02:00");
        (await ConfigReloader.ReloadIfChangedAsync(manager)).Outcome.Should().Be(ConfigReloadOutcome.ReloadedRestartNeeded);
        manager.WatchDirectories[0].Schedule.Should().Be("daily 02:00");

        (await ConfigReloader.ReloadIfChangedAsync(manager)).Outcome.Should().Be(ConfigReloadOutcome.Unchanged, "the new file is what the manager has now");
    }

    [Fact]
    public async Task Reload_ABrokenFile_KeepsTheRunningConfiguration()
    {
        var manager = await LoadAsync();
        var folder = manager.WatchDirectories[0].Path;
        File.WriteAllText(ConfigPath, "{ half written");

        var result = await ConfigReloader.ReloadIfChangedAsync(manager);

        result.Outcome.Should().Be(ConfigReloadOutcome.Invalid);
        result.Error.Should().NotBeNullOrEmpty();
        manager.WatchDirectories[0].Path.Should().Be(folder);
    }

    [Fact]
    public async Task Reload_AnInvalidButParsableFile_IsRefused()
    {
        var manager = await LoadAsync();
        var disk = (JsonObject)JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        disk["globalStorageType"] = "nowhere";
        File.WriteAllText(ConfigPath, disk.ToJsonString());

        (await ConfigReloader.ReloadIfChangedAsync(manager)).Outcome.Should().Be(ConfigReloadOutcome.Invalid);
        manager.GlobalStorageType.Should().Be("local");
    }

    [Fact]
    public async Task Monitor_RaisesOnceForABurstOfWrites_AndNotForOtherFiles()
    {
        using var monitor = new ConfigChangeMonitor(ConfigPath, TimeSpan.FromMilliseconds(150));
        var count = 0;
        var raised = new TaskCompletionSource();
        monitor.Changed += () =>
        {
            Interlocked.Increment(ref count);
            raised.TrySetResult();
        };

        File.WriteAllText(Path.Combine(_directory, "other.txt"), "x");
        for (var i = 0; i < 4; i++)
        {
            File.AppendAllText(ConfigPath, " ");
            await Task.Delay(20);
        }

        await raised.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);

        count.Should().Be(1);
    }

    [Fact]
    public async Task Monitor_PokeRaisesChanged_AndWaitForChangeCompletes()
    {
        using var monitor = new ConfigChangeMonitor(ConfigPath, TimeSpan.FromMilliseconds(50));
        var wait = monitor.WaitForChangeAsync(CancellationToken.None);

        monitor.Poke();

        await wait.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OwnSave_IsNotAnExternalChange_EvenThoughTheMonitorSeesTheFile()
    {
        var manager = await LoadAsync();
        manager.Retention.KeepDaily = 3;
        await manager.SaveAsync();

        (await ConfigReloader.ReloadIfChangedAsync(manager)).Outcome.Should().Be(ConfigReloadOutcome.Unchanged);
    }

    private sealed class Probe
    {
        public bool Dirty;
        public int Applied;
        public int Reloads;
        public List<bool> Pending { get; } = [];
        public ConfigExternalChangeCoordinator Coordinator { get; }

        public Probe(ConfigManager manager)
        {
            Coordinator = new ConfigExternalChangeCoordinator(manager, () => Dirty, () => { Applied++; return Task.CompletedTask; }, new TestLogger());
            Coordinator.PendingChanged += Pending.Add;
            Coordinator.Reloaded += () => Reloads++;
        }
    }

    [Fact]
    public async Task Coordinator_NothingChanged_DoesNothing()
    {
        var probe = new Probe(await LoadAsync());

        await probe.Coordinator.CheckAsync();

        probe.Applied.Should().Be(0);
        probe.Reloads.Should().Be(0);
        probe.Coordinator.Pending.Should().BeFalse();
    }

    [Fact]
    public async Task Coordinator_ExternalChange_WithCleanSettings_ReloadsSilently_AndAppliesProtection()
    {
        var manager = await LoadAsync();
        var probe = new Probe(manager);
        await ExternalEditAsync(root => ((JsonObject)root["watchDirectories"]![0]!)["schedule"] = "daily 02:00");

        await probe.Coordinator.CheckAsync();

        manager.WatchDirectories[0].Schedule.Should().Be("daily 02:00");
        probe.Applied.Should().Be(1);
        probe.Reloads.Should().Be(1);
        probe.Coordinator.Pending.Should().BeFalse();
    }

    [Fact]
    public async Task Coordinator_ExternalChange_WithUnsavedEdits_AsksInsteadOfReloading_ThenReloadOrKeep()
    {
        var manager = await LoadAsync();
        var probe = new Probe(manager) { Dirty = true };
        await ExternalEditAsync(root => root["retention"]!["keepDaily"] = 21);

        await probe.Coordinator.CheckAsync();
        await probe.Coordinator.CheckAsync();

        probe.Coordinator.Pending.Should().BeTrue();
        probe.Pending.Should().Equal(true);
        manager.Retention.KeepDaily.Should().NotBe(21, "the edits in Settings are never replaced unasked");
        probe.Reloads.Should().Be(0);

        await probe.Coordinator.KeepMyEditsAsync();
        probe.Coordinator.Pending.Should().BeFalse();
        await probe.Coordinator.CheckAsync();
        probe.Coordinator.Pending.Should().BeFalse("the banner stays away for a version of the file the user already decided about");

        // Saving afterwards merges the outside change instead of reverting it.
        manager.Notifications.Enabled = false;
        await manager.SaveAsync();
        manager.Retention.KeepDaily.Should().Be(21);

        // A newer external edit asks again; Reload takes it.
        await ExternalEditAsync(root => root["retention"]!["keepDaily"] = 33);
        await probe.Coordinator.CheckAsync();
        probe.Coordinator.Pending.Should().BeTrue();
        probe.Dirty = false;
        await probe.Coordinator.ReloadNowAsync();
        manager.Retention.KeepDaily.Should().Be(33);
        probe.Coordinator.Pending.Should().BeFalse();
        probe.Reloads.Should().Be(1);
        probe.Pending.Should().Equal(true, false, true, false);
    }

    [Fact]
    public async Task Coordinator_OwnSave_IsNotReported()
    {
        var manager = await LoadAsync();
        var probe = new Probe(manager) { Dirty = true };
        manager.Retention.KeepDaily = 2;
        await manager.SaveAsync();

        await probe.Coordinator.CheckAsync();

        probe.Coordinator.Pending.Should().BeFalse();
    }
}
