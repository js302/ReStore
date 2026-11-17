using ReStore.Core;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

internal sealed class PlanFakeStore : IEnvironmentVariableStore
{
    public Dictionary<(string, EnvironmentVariableTarget), StoredEnvironmentValue> Values { get; } = [];
    public HashSet<EnvironmentVariableTarget> Unwritable { get; } = [];
    public List<string> Writes { get; } = [];

    public StoredEnvironmentValue? Get(string name, EnvironmentVariableTarget target)
        => Values.TryGetValue((name.ToUpperInvariant(), target), out var v) ? v : null;

    public void Set(string name, string value, EnvironmentVariableTarget target, string kind)
    {
        if (Unwritable.Contains(target)) throw new UnauthorizedAccessException("Access denied");
        Values[(name.ToUpperInvariant(), target)] = new StoredEnvironmentValue(value, kind);
        Writes.Add($"set:{name}");
    }

    public void Remove(string name, EnvironmentVariableTarget target)
    {
        Values.Remove((name.ToUpperInvariant(), target));
        Writes.Add($"remove:{name}");
    }

    public void NotifyChanged() { }

    public void Seed(string name, EnvironmentVariableTarget target, string value, string kind = "String")
        => Values[(name.ToUpperInvariant(), target)] = new StoredEnvironmentValue(value, kind);
}

internal sealed class PlanFakeServices
{
    public PlanFakeStore Store { get; } = new();
    public List<InstalledProgram> Installed { get; } = [];
    public int InstalledQueries { get; private set; }

    /// <summary>Current registry exports by key; a missing key means "does not exist".</summary>
    public Dictionary<string, string> Registry { get; } = [];
    public List<RegistryImportPlan> Imports { get; } = [];
    public List<string> ImportedTexts { get; } = [];
    public List<IReadOnlyList<InstalledProgram>> Installs { get; } = [];
    public List<string> Order { get; } = [];
    public bool ExportThrows { get; set; }
    public IDevToolsMachine? DevTools { get; set; }

    public SystemRestoreServices Build() => new()
    {
        Environment = new OrderedStore(this),
        Programs = new Programs(this),
        RegistryExporter = new Exporter(this),
        RegistryImporter = new Importer(this),
        Installer = new Installer(this),
        DevTools = DevTools
    };

    private sealed class OrderedStore(PlanFakeServices owner) : IEnvironmentVariableStore
    {
        public StoredEnvironmentValue? Get(string name, EnvironmentVariableTarget target) => owner.Store.Get(name, target);
        public void Set(string name, string value, EnvironmentVariableTarget target, string kind) { owner.Order.Add("env"); owner.Store.Set(name, value, target, kind); }
        public void Remove(string name, EnvironmentVariableTarget target) { owner.Order.Add("env"); owner.Store.Remove(name, target); }
        public void NotifyChanged() { }
    }

    private sealed class Programs(PlanFakeServices owner) : IInstalledProgramSource
    {
        public Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken)
        {
            owner.InstalledQueries++;
            return Task.FromResult<IReadOnlyList<InstalledProgram>>([.. owner.Installed]);
        }
    }

    private sealed class Exporter(PlanFakeServices owner) : IRegistryKeyExporter
    {
        public Task<bool> ExportKeyAsync(string registryKey, string destinationFile, CancellationToken cancellationToken)
        {
            if (owner.ExportThrows) throw new IOException("reg export failed");
            if (!owner.Registry.TryGetValue(registryKey, out var text)) return Task.FromResult(false);
            File.WriteAllText(destinationFile, text);
            return Task.FromResult(true);
        }
    }

    private sealed class Importer(PlanFakeServices owner) : IRegistryImporter
    {
        public Task<RegistryImportResult> ImportAsync(RegistryImportPlan plan, CancellationToken cancellationToken)
        {
            owner.Order.Add("settings");
            owner.Imports.Add(plan);
            foreach (var item in plan.Items) owner.ImportedTexts.Add(File.ReadAllText(item.FilePath));
            return Task.FromResult(new RegistryImportResult { Imported = plan.Items.Count });
        }
    }

    private sealed class Installer(PlanFakeServices owner) : IProgramInstaller
    {
        public Task<ProgramInstallSummary> InstallAsync(IReadOnlyList<InstalledProgram> programs, CancellationToken cancellationToken)
        {
            owner.Order.Add("programs");
            owner.Installs.Add(programs);
            return Task.FromResult(new ProgramInstallSummary(programs.Count, 0));
        }
    }
}

[Collection("GlobalState")]
public class SystemRestorePlannerTests : IDisposable
{
    private const string Mouse = @"HKEY_CURRENT_USER\Control Panel\Mouse";
    private const string Power = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStorePlan_" + Guid.NewGuid().ToString("N"));
    private readonly string _previousRoot = AppPaths.Root;
    private readonly PlanFakeServices _fakes = new();
    private readonly TestLogger _logger = new();

    public SystemRestorePlannerTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot(_root);
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot(_previousRoot);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string Extract(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private SystemRestorePlanner Planner() => new(_fakes.Build(), _logger, Path.Combine(_root, "scratch"));

    private static void WriteEnv(string dir, params EnvironmentVariableEntry[] variables)
        => File.WriteAllText(Path.Combine(dir, "environment_variables.json"), JsonSerializer.Serialize(new { variables }, JsonDefaults.SystemBackup));

    private static EnvironmentVariableEntry Var(string name, string value, EnvironmentVariableTarget target = EnvironmentVariableTarget.User, string? kind = null)
        => new() { Name = name, Value = value, Target = target, Kind = kind };

    private static string Reg(string key, string body)
        => $"Windows Registry Editor Version 5.00\r\n\r\n[{key}]\r\n{body}\r\n";

    private static void WriteSettings(string dir, params (string key, string file, string text)[] entries)
    {
        foreach (var (_, file, text) in entries) File.WriteAllText(Path.Combine(dir, file), text);
        var manifest = new WindowsSettingsExport
        {
            ExportedCategories = ["x"],
            ExportedFiles = entries.ToDictionary(e => e.key, e => e.file)
        };
        File.WriteAllText(Path.Combine(dir, "settings_manifest.json"), JsonSerializer.Serialize(manifest, JsonDefaults.SystemBackup));
    }

    private static void WritePrograms(string dir, params InstalledProgram[] programs)
        => File.WriteAllText(Path.Combine(dir, "installed_programs.json"), JsonSerializer.Serialize(new { programs }, JsonDefaults.SystemBackup));

    [Fact]
    public async Task Environment_ClassifiesAddChangeUnchangedAndSystemManaged()
    {
        _fakes.Store.Seed("SAME", EnvironmentVariableTarget.User, "v");
        _fakes.Store.Seed("DIFF", EnvironmentVariableTarget.User, "old");
        var dir = Extract("env");
        WriteEnv(dir,
            Var("NEW_ONE", "n"), Var("SAME", "v"), Var("DIFF", "new"),
            Var("COMPUTERNAME", "x"), Var("MACHINE_ONE", "m", EnvironmentVariableTarget.Machine));

        var plan = await Planner().BuildPlanAsync("environment", dir);

        plan.Actions.Select(a => (a.Name, a.Change)).Should().Equal(
            ("NEW_ONE", PlannedChange.Add), ("SAME", PlannedChange.Unchanged), ("DIFF", PlannedChange.Change),
            ("COMPUTERNAME", PlannedChange.Skipped), ("MACHINE_ONE", PlannedChange.Add));
        var diff = plan.Actions.Single(a => a.Name == "DIFF");
        (diff.OldValue, diff.NewValue).Should().Be(("old", "new"));
        plan.Actions.Single(a => a.Name == "MACHINE_ONE").NeedsAdmin.Should().BeTrue();
        plan.Actions.Single(a => a.Name == "NEW_ONE").NeedsAdmin.Should().BeFalse();
        plan.ChangeCount.Should().Be(3);
        plan.NeedsAdminCount.Should().Be(1);
    }

    [Fact]
    public async Task Environment_PathPlansTheMergeResult_AndIsUnchangedWhenAlreadyContained()
    {
        _fakes.Store.Seed("PATH", EnvironmentVariableTarget.User, @"C:\a;C:\b", "ExpandString");
        var dir = Extract("path");
        WriteEnv(dir, Var("PATH", @"C:\b;C:\c", kind: "String"));

        var plan = await Planner().BuildPlanAsync("environment", dir);
        var action = plan.Actions.Should().ContainSingle().Subject;
        action.Change.Should().Be(PlannedChange.Change);
        action.OldValue.Should().Be(@"C:\a;C:\b");
        action.NewValue.Should().Be(@"C:\a;C:\b;C:\c");
        action.Detail.Should().Contain("merged");

        WriteEnv(dir, Var("PATH", @"c:\B"));
        (await Planner().BuildPlanAsync("environment", dir)).Actions.Single().Change.Should().Be(PlannedChange.Unchanged);
    }

    [Fact]
    public async Task Environment_DifferentKindOnly_IsAChange()
    {
        _fakes.Store.Seed("X", EnvironmentVariableTarget.User, "%A%", "String");
        var dir = Extract("kind");
        WriteEnv(dir, Var("X", "%A%", kind: "ExpandString"));

        (await Planner().BuildPlanAsync("environment", dir)).Actions.Single().Change.Should().Be(PlannedChange.Change);
    }

    [Fact]
    public async Task Programs_PresentAbsentAndUnavailable_AndMachineOnlyQueriedWhenNeeded()
    {
        _fakes.Installed.Add(new InstalledProgram { Name = "Git", WingetId = "Git.Git" });
        _fakes.Installed.Add(new InstalledProgram { Name = "7-Zip 23", WingetId = "7zip.7zip" });
        var dir = Extract("programs");
        WritePrograms(dir,
            new InstalledProgram { Name = "Git", WingetId = "Git.Git", IsWingetAvailable = true },
            new InstalledProgram { Name = "Seven Zip", WingetId = "7zip.7zip", IsWingetAvailable = true },
            new InstalledProgram { Name = "Node", WingetId = "OpenJS.NodeJS", IsWingetAvailable = true, Version = "20.1" },
            new InstalledProgram { Name = "Legacy Tool" },
            new InstalledProgram { Name = "Bad", WingetId = "bad id;calc", IsWingetAvailable = true });

        var plan = await Planner().BuildPlanAsync("programs", dir);

        plan.Actions.Select(a => (a.Name, a.Change)).Should().Equal(
            ("Git", PlannedChange.AlreadyInstalled), ("Seven Zip", PlannedChange.AlreadyInstalled), ("Node", PlannedChange.Install),
            ("Legacy Tool", PlannedChange.Unavailable), ("Bad", PlannedChange.Unavailable));
        plan.Actions.Single(a => a.Name == "Node").NewValue.Should().Be("20.1");
        _fakes.InstalledQueries.Should().Be(1);

        WritePrograms(dir);
        (await Planner().BuildPlanAsync("programs", dir)).Actions.Should().BeEmpty();
        _fakes.InstalledQueries.Should().Be(1, "an empty program list needs no machine query");
    }

    [Fact]
    public async Task Settings_DiffsAgainstCurrentExport_AndFlagsMachineKeys()
    {
        const string body = "\"Speed\"=\"1\"";
        var dir = Extract("settings");
        WriteSettings(dir,
            (Mouse, "Mouse.reg", Reg(Mouse, body)),
            (Power, "Power.reg", Reg(Power, "\"X\"=dword:00000001")));
        _fakes.Registry[Mouse] = Reg(Mouse, "\"Other\"=\"2\"\r\n" + body);

        var plan = await Planner().BuildPlanAsync("settings", dir);

        var mouse = plan.Actions.Single(a => a.Name == Mouse);
        mouse.Change.Should().Be(PlannedChange.Unchanged, "every backed-up line already exists");
        mouse.Scope.Should().Be("HKCU");
        var power = plan.Actions.Single(a => a.Name == Power);
        power.Change.Should().Be(PlannedChange.Import, "the key is absent on this machine");
        (power.Scope, power.NeedsAdmin).Should().Be(("HKLM", true));
        power.Keys.Should().Equal(Power);

        _fakes.Registry[Mouse] = Reg(Mouse, "\"Speed\"=\"9\"");
        (await Planner().BuildPlanAsync("settings", dir)).Actions.Single(a => a.Name == Mouse).Change.Should().Be(PlannedChange.Import);
        Directory.Exists(Path.Combine(_root, "scratch")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_root, "scratch")).Should().BeEmpty("scratch exports are deleted");
    }

    [Fact]
    public async Task Settings_DisallowedKeysAreRejectedNotPlanned()
    {
        const string run = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run";
        var dir = Extract("evil");
        WriteSettings(dir, (run, "Run.reg", Reg(run, "\"x\"=\"calc.exe\"")));

        var plan = await Planner().BuildPlanAsync("settings", dir);

        plan.Actions.Should().BeEmpty();
        plan.Rejected.Should().ContainSingle().Which.Should().Contain("allow-list");
    }

    [Fact]
    public void Select_FiltersToChangesOnly_AndOrdersEnvironmentThenSettingsThenPrograms()
    {
        var plan = new SystemRestorePlan("mixed");
        PlannedAction Make(string id, string component, PlannedChange change) => new() { Id = id, Component = component, Change = change, Name = id };
        plan.Actions.Add(Make("p", "programs", PlannedChange.Install));
        plan.Actions.Add(Make("s", "settings", PlannedChange.Import));
        plan.Actions.Add(Make("e1", "environment", PlannedChange.Add));
        plan.Actions.Add(Make("e2", "environment", PlannedChange.Change));
        plan.Actions.Add(Make("same", "environment", PlannedChange.Unchanged));

        plan.Select(["p", "s", "e1", "e2", "same", "nope"]).Select(a => a.Id).Should().Equal("e1", "e2", "s", "p");
        plan.Select(["e2"]).Select(a => a.Id).Should().Equal("e2");
        plan.Select([]).Should().BeEmpty();
        plan.DefaultSelection().Should().BeEquivalentTo(["p", "s", "e1", "e2"]);
    }

    [Fact]
    public async Task Formatter_ListsChangesWithOldAndNewValues()
    {
        _fakes.Store.Seed("DIFF", EnvironmentVariableTarget.User, "old");
        var dir = Extract("fmt");
        WriteEnv(dir, Var("DIFF", "new"), Var("M", "m", EnvironmentVariableTarget.Machine));

        var text = SystemRestorePlanFormatter.Format(await Planner().BuildPlanAsync("environment", dir), reveal: true);

        text.Should().Contain("2 change(s)").And.Contain("1 need administrator rights")
            .And.Contain("old: old").And.Contain("new: new").And.Contain("[needs admin]");
    }

    // ---- undo points + applier ----

    private async Task<(SystemRestorePlan plan, string dir)> MixedPlanAsync()
    {
        var dir = Extract("mixed-env");
        WriteEnv(dir, Var("PATH", @"C:\new"), Var("FRESH", "f"));
        var plan = await Planner().BuildPlanAsync("environment", dir);

        var settingsDir = Extract("mixed-settings");
        WriteSettings(settingsDir, (Mouse, "Mouse.reg", Reg(Mouse, "\"Speed\"=\"1\"")));
        var settings = await Planner().BuildPlanAsync("settings", settingsDir);
        plan.Actions.AddRange(settings.Actions);

        var programsDir = Extract("mixed-programs");
        WritePrograms(programsDir, new InstalledProgram { Name = "Node", WingetId = "OpenJS.NodeJS", IsWingetAvailable = true });
        plan.Actions.AddRange((await Planner().BuildPlanAsync("programs", programsDir)).Actions);
        return (plan, dir);
    }

    [Fact]
    public async Task Undo_SnapshotRecordsCurrentStateOfExactlyTheSelectedItems()
    {
        _fakes.Store.Seed("PATH", EnvironmentVariableTarget.User, @"C:\old", "ExpandString");
        _fakes.Store.Seed("UNTOUCHED", EnvironmentVariableTarget.User, "keep");
        _fakes.Registry[Mouse] = Reg(Mouse, "\"Speed\"=\"7\"");
        _fakes.Installed.Add(new InstalledProgram { Name = "Git", WingetId = "Git.Git" });
        var (plan, _) = await MixedPlanAsync();
        var selected = plan.Select(plan.DefaultSelection());
        var result = new SystemRestoreResult("mixed");

        var undoDir = await new SystemRestoreApplier(_fakes.Build(), _logger).ApplyAsync(plan, selected, "system_backups/x.zip", result, null);

        undoDir.Should().NotBeNull().And.StartWith(AppPaths.RestoreUndoDirectory);
        var manifest = JsonSerializer.Deserialize<UndoManifest>(File.ReadAllText(Path.Combine(undoDir!, "undo_manifest.json")), JsonDefaults.SystemBackup)!;
        manifest.SourceBackup.Should().Be("system_backups/x.zip");
        manifest.Environment.Select(e => e.Name).Should().BeEquivalentTo("PATH", "FRESH");
        manifest.Environment.Single(e => e.Name == "PATH").Should().BeEquivalentTo(
            new UndoEnvironmentEntry { Name = "PATH", Target = EnvironmentVariableTarget.User, Existed = true, Value = @"C:\old", Kind = "ExpandString" });
        manifest.Environment.Single(e => e.Name == "FRESH").Existed.Should().BeFalse();
        var setting = manifest.Settings.Should().ContainSingle().Subject;
        File.ReadAllText(Path.Combine(undoDir!, setting.File!)).Should().Contain("\"Speed\"=\"7\"");
        manifest.PlannedInstalls.Should().Equal("Node (OpenJS.NodeJS)");
        File.ReadAllText(Path.Combine(undoDir!, manifest.InstalledProgramsFile!)).Should().Contain("Git.Git");
        File.ReadAllText(Path.Combine(undoDir!, "undo_manifest.json")).Should().NotContain("UNTOUCHED");
        result.UndoPointFolder.Should().Be(undoDir);
    }

    [Fact]
    public async Task Apply_RunsOnlySelectedRows_InOrder_AfterSavingTheUndoPoint()
    {
        _fakes.Store.Seed("PATH", EnvironmentVariableTarget.User, @"C:\old");
        var (plan, _) = await MixedPlanAsync();
        var selected = plan.Select(plan.Changes.Where(a => a.Name != "FRESH").Select(a => a.Id));
        var result = new SystemRestoreResult("mixed");

        await new SystemRestoreApplier(_fakes.Build(), _logger).ApplyAsync(plan, selected, null, result, null);

        _fakes.Order.Should().Equal("env", "settings", "programs");
        _fakes.Store.Get("FRESH", EnvironmentVariableTarget.User).Should().BeNull("the row was opted out");
        _fakes.Store.Get("PATH", EnvironmentVariableTarget.User)!.Value.Should().Be(@"C:\old;C:\new");
        _fakes.Installs.Single().Select(p => p.WingetId).Should().Equal("OpenJS.NodeJS");
        result.Succeeded.Should().Be(3);
        result.Failed.Should().Be(0);
    }

    [Fact]
    public async Task Apply_WithNothingSelected_ChangesNothingAndSavesNoUndoPoint()
    {
        var (plan, _) = await MixedPlanAsync();

        var undoDir = await new SystemRestoreApplier(_fakes.Build(), _logger).ApplyAsync(plan, [], null, new SystemRestoreResult("x"), null);

        undoDir.Should().BeNull();
        _fakes.Order.Should().BeEmpty();
        Directory.Exists(AppPaths.RestoreUndoDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Undo_RoundTrip_RestoresExactPreviousValues_ListsInstalls_AndNeverUninstalls()
    {
        _fakes.Store.Seed("PATH", EnvironmentVariableTarget.User, @"C:\old", "ExpandString");
        _fakes.Registry[Mouse] = Reg(Mouse, "\"Speed\"=\"7\"");
        var (plan, _) = await MixedPlanAsync();
        var applier = new SystemRestoreApplier(_fakes.Build(), _logger);
        var undoDir = (await applier.ApplyAsync(plan, plan.Select(plan.DefaultSelection()), null, new SystemRestoreResult("m"), null))!;
        _fakes.Store.Get("PATH", EnvironmentVariableTarget.User)!.Value.Should().Be(@"C:\old;C:\new");
        _fakes.Store.Get("FRESH", EnvironmentVariableTarget.User).Should().NotBeNull();
        _fakes.Imports.Clear();
        _fakes.ImportedTexts.Clear();
        _fakes.Installs.Clear();

        var undo = await new SystemRestoreUndo(_fakes.Build(), _logger).UndoAsync(undoDir);

        _fakes.Store.Get("PATH", EnvironmentVariableTarget.User).Should().Be(new StoredEnvironmentValue(@"C:\old", "ExpandString"));
        _fakes.Store.Get("FRESH", EnvironmentVariableTarget.User).Should().BeNull();
        _fakes.Imports.Should().ContainSingle();
        _fakes.ImportedTexts.Single().Should().Contain("\"Speed\"=\"7\"");
        _fakes.Installs.Should().BeEmpty("programs are never uninstalled or reinstalled by undo");
        undo.Failed.Should().Be(0);
        undo.Restored.Should().Be(3);
        undo.InstalledPrograms.Should().Equal("Node (OpenJS.NodeJS)");
        undo.Messages.Should().Contain(m => m.Contains("OpenJS.NodeJS"));
    }

    [Fact]
    public async Task Undo_RefusesSavedRegFileThatLeavesTheAllowList_AndListsPoints()
    {
        _fakes.Registry[Mouse] = Reg(Mouse, "\"Speed\"=\"7\"");
        var dir = Extract("undo-evil");
        WriteSettings(dir, (Mouse, "Mouse.reg", Reg(Mouse, "\"Speed\"=\"1\"")));
        var plan = await Planner().BuildPlanAsync("settings", dir);
        var undoDir = (await new SystemRestoreApplier(_fakes.Build(), _logger)
            .ApplyAsync(plan, plan.Select(plan.DefaultSelection()), null, new SystemRestoreResult("s"), null))!;

        SystemRestoreUndo.List().Should().ContainSingle().Which.Path.Should().Be(undoDir);

        File.WriteAllText(Path.Combine(undoDir, "settings-01.reg"),
            Reg(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "\"x\"=\"calc.exe\""));
        _fakes.Imports.Clear();

        var undo = await new SystemRestoreUndo(_fakes.Build(), _logger).UndoAsync(undoDir);

        undo.Failed.Should().Be(1);
        _fakes.Imports.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_AbortsBeforeAnyChange_WhenRegistryExportFails_AndLeavesNoUndoPoint()
    {
        _fakes.Store.Seed("PATH", EnvironmentVariableTarget.User, @"C:\old");
        var (plan, _) = await MixedPlanAsync();
        _fakes.ExportThrows = true;

        var act = () => new SystemRestoreApplier(_fakes.Build(), _logger).ApplyAsync(plan, plan.Select(plan.DefaultSelection()), null, new SystemRestoreResult("m"), null);

        await act.Should().ThrowAsync<IOException>();
        _fakes.Order.Should().BeEmpty();
        _fakes.Store.Writes.Should().BeEmpty();
        _fakes.Store.Get("PATH", EnvironmentVariableTarget.User)!.Value.Should().Be(@"C:\old");
        SystemRestoreUndo.List().Should().BeEmpty();
        Directory.GetDirectories(AppPaths.RestoreUndoDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task Planner_TreatsUnreadableCurrentKeyAsImport_NotAsAbsentOrUnchanged()
    {
        var dir = Extract("unreadable");
        WriteSettings(dir, (Mouse, "Mouse.reg", Reg(Mouse, "\"Speed\"=\"1\"")));
        _fakes.ExportThrows = true;

        var plan = await Planner().BuildPlanAsync("settings", dir);

        plan.Actions.Single().Change.Should().Be(PlannedChange.Import);
        plan.Actions.Single().Detail.Should().Contain("unreadable");
    }

    [Fact]
    public async Task Undo_OnlyAcceptsDirectChildrenOfTheUndoRoot()
    {
        _fakes.Store.Seed("A", EnvironmentVariableTarget.User, "old");
        var dir = Extract("undo-src");
        WriteEnv(dir, Var("A", "new"));
        var plan = await Planner().BuildPlanAsync("environment", dir);
        var undoDir = (await new SystemRestoreApplier(_fakes.Build(), _logger)
            .ApplyAsync(plan, plan.Select(plan.DefaultSelection()), null, new SystemRestoreResult("e"), null))!;
        var undo = new SystemRestoreUndo(_fakes.Build(), _logger);

        // A valid-looking copy outside the undo root.
        var outside = Path.Combine(_root, "outside-copy");
        Directory.CreateDirectory(outside);
        File.Copy(Path.Combine(undoDir, "undo_manifest.json"), Path.Combine(outside, "undo_manifest.json"));
        await ((Func<Task>)(() => undo.UndoAsync(outside))).Should().ThrowAsync<InvalidDataException>();
        await ((Func<Task>)(() => undo.UndoAsync(Path.Combine(undoDir, "..", "..", "outside-copy")))).Should().ThrowAsync<InvalidDataException>();
        await ((Func<Task>)(() => undo.UndoAsync(AppPaths.RestoreUndoDirectory))).Should().ThrowAsync<InvalidDataException>();
        await ((Func<Task>)(() => undo.UndoAsync(Path.Combine(undoDir, "sub")))).Should().ThrowAsync<InvalidDataException>();
        Action del = () => SystemRestoreUndo.Delete(outside);
        del.Should().Throw<InvalidDataException>();
        Directory.Exists(outside).Should().BeTrue();

        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(AppPaths.RestoreUndoDirectory, "linked");
            Directory.CreateSymbolicLink(link, outside);
            await ((Func<Task>)(() => undo.UndoAsync(link))).Should().ThrowAsync<InvalidDataException>();
        }

        (await undo.UndoAsync(undoDir + Path.DirectorySeparatorChar)).Restored.Should().Be(1);
    }

    [Fact]
    public async Task Undo_RejectsOutOfRangeManifestValues()
    {
        _fakes.Store.Seed("A", EnvironmentVariableTarget.User, "old");
        var dir = Extract("undo-bounds");
        WriteEnv(dir, Var("A", "new"));
        var plan = await Planner().BuildPlanAsync("environment", dir);
        var undoDir = (await new SystemRestoreApplier(_fakes.Build(), _logger)
            .ApplyAsync(plan, plan.Select(plan.DefaultSelection()), null, new SystemRestoreResult("e"), null))!;
        var manifestPath = Path.Combine(undoDir, "undo_manifest.json");
        var manifest = JsonSerializer.Deserialize<UndoManifest>(File.ReadAllText(manifestPath), JsonDefaults.SystemBackup)!;
        manifest.Environment[0].Kind = "Binary";
        manifest.Environment.Add(new UndoEnvironmentEntry { Name = "B", Target = EnvironmentVariableTarget.Process, Existed = true, Value = "x", Kind = "String" });
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonDefaults.SystemBackup));
        _fakes.Store.Writes.Clear();

        var undo = await new SystemRestoreUndo(_fakes.Build(), _logger).UndoAsync(undoDir);

        undo.Failed.Should().Be(2);
        _fakes.Store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_RemovesOnlyTheUndoPoint()
    {
        _fakes.Store.Seed("A", EnvironmentVariableTarget.User, "old");
        var dir = Extract("undo-del");
        WriteEnv(dir, Var("A", "new"));
        var plan = await Planner().BuildPlanAsync("environment", dir);
        var undoDir = (await new SystemRestoreApplier(_fakes.Build(), _logger)
            .ApplyAsync(plan, plan.Select(plan.DefaultSelection()), null, new SystemRestoreResult("e"), null))!;

        SystemRestoreUndo.Delete(undoDir);

        Directory.Exists(undoDir).Should().BeFalse();
        SystemRestoreUndo.List().Should().BeEmpty();
    }

    [Fact]
    public async Task Undo_RejectsFolderWithoutManifest()
    {
        var empty = Path.Combine(AppPaths.RestoreUndoDirectory, "empty");
        Directory.CreateDirectory(empty);
        var act = () => new SystemRestoreUndo(_fakes.Build(), _logger).UndoAsync(empty);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    // ---- pipeline ----

    private (SystemBackupManager manager, Mock<IStorage> storage) PipelineFor(string payloadName, Action<string> fill)
    {
        var payload = Extract(payloadName);
        fill(payload);
        var zip = Path.Combine(_root, payloadName + ".zip");
        ZipFile.CreateFromDirectory(payload, zip);
        var storage = new Mock<IStorage>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, dest, _) => { File.Copy(zip, dest, true); return Task.CompletedTask; });
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.GlobalStorageType).Returns("g");
        config.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig());
        var manager = new SystemBackupManager(_logger, config.Object, new Mock<SystemState>(_logger).Object) { RestoreServices = _fakes.Build() };
        return (manager, storage);
    }

    [Fact]
    public async Task Pipeline_PlanOnly_ReturnsPlanAndChangesNothing()
    {
        var (manager, storage) = PipelineFor("pipe-plan", d => WriteEnv(d, Var("FRESH", "f")));

        var result = await manager.RestoreSystemAsync(storage.Object, "system_environment", "system_backups/environment/x.zip",
            new SystemRestoreOptions { PlanOnly = true, Confirm = _ => throw new InvalidOperationException("must not prompt") });

        result.Plan!.ChangeCount.Should().Be(1);
        result.ScriptsFolder.Should().BeNull();
        Directory.Exists(Path.Combine(AppPaths.Root, "restore-scripts")).Should().BeFalse("--plan leaves no durable copy");
        _fakes.Store.Writes.Should().BeEmpty();
        result.UndoPointFolder.Should().BeNull();
        Directory.Exists(AppPaths.RestoreUndoDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Pipeline_SelectActions_AppliesOnlyCheckedRows_AndCreatesUndoPoint()
    {
        var (manager, storage) = PipelineFor("pipe-select", d => WriteEnv(d, Var("A", "1"), Var("B", "2")));
        SystemRestorePlan? shown = null;
        var options = new SystemRestoreOptions
        {
            SelectActions = plan => { shown = plan; return Task.FromResult<IReadOnlyCollection<string>?>([plan.Actions.Single(a => a.Name == "B").Id]); }
        };

        var result = await manager.RestoreSystemAsync(storage.Object, "system_environment", "system_backups/environment/x.zip", options);

        shown!.ChangeCount.Should().Be(2);
        _fakes.Store.Get("A", EnvironmentVariableTarget.User).Should().BeNull();
        _fakes.Store.Get("B", EnvironmentVariableTarget.User)!.Value.Should().Be("2");
        result.Succeeded.Should().Be(1);
        result.Skipped.Should().Be(1);
        Directory.Exists(result.UndoPointFolder).Should().BeTrue();
    }

    [Fact]
    public async Task Pipeline_PreviewCancelled_ChangesNothing()
    {
        var (manager, storage) = PipelineFor("pipe-cancel", d => WriteEnv(d, Var("A", "1")));
        var options = new SystemRestoreOptions { SelectActions = _ => Task.FromResult<IReadOnlyCollection<string>?>(null) };

        var result = await manager.RestoreSystemAsync(storage.Object, "system_environment", "system_backups/environment/x.zip", options);

        _fakes.Store.Writes.Should().BeEmpty();
        result.Messages.Should().Contain(m => m.Contains("cancelled"));
        result.UndoPointFolder.Should().BeNull();
    }

    [Fact]
    public async Task Pipeline_LegacyCliPath_StillRestoresEnvironment_WithoutPrompt_ButNotPrograms()
    {
        var (envManager, envStorage) = PipelineFor("pipe-legacy-env", d => WriteEnv(d, Var("A", "1")));
        var envResult = await envManager.RestoreSystemAsync(envStorage.Object, "system_environment", "system_backups/environment/x.zip");
        envResult.Succeeded.Should().Be(1);
        envResult.UndoPointFolder.Should().NotBeNull();

        var (progManager, progStorage) = PipelineFor("pipe-legacy-prog",
            d => WritePrograms(d, new InstalledProgram { Name = "Node", WingetId = "OpenJS.NodeJS", IsWingetAvailable = true }));
        var progResult = await progManager.RestoreSystemAsync(progStorage.Object, "system_programs", "system_backups/programs/x.zip");
        progResult.Succeeded.Should().Be(0);
        _fakes.Installs.Should().BeEmpty();
    }
}

public class SystemRestoreCliParsingTests
{
    [Theory]
    [InlineData(new[] { "system-restore", "x.zip", "environment", "--plan" }, true)]
    [InlineData(new[] { "system-undo", @"C:\u\restore-undo\2026" }, true)]
    [InlineData(new[] { "system-undo" }, false)]
    [InlineData(new[] { "system-undo", "--list" }, true)]
    [InlineData(new[] { "system-undo", "--delete", "d" }, true)]
    [InlineData(new[] { "system-undo", "--list", "d" }, false)]
    [InlineData(new[] { "system-undo", "--delete" }, false)]
    [InlineData(new[] { "system-undo", "a", "b" }, false)]
    [InlineData(new[] { "system-undo", "a", "--plan" }, false)]
    [InlineData(new[] { "system-backup", "--plan" }, false)]
    public void Parser_AcceptsPlanAndUndoCommands(string[] args, bool valid)
    {
        Program.TryParseArguments(args, out _, out _).Should().Be(valid);
    }
}
