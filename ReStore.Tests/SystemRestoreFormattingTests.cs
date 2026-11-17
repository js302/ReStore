using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class SystemRestorePlanFormatterTests
{
    [Fact]
    public void EmptyPlan_ShowsOnlyTheHeader()
    {
        var text = SystemRestorePlanFormatter.Format(new SystemRestorePlan("programs"));

        text.Should().Be("System restore plan (programs): 0 change(s), 0 unchanged, 0 need administrator rights");
    }

    [Fact]
    public void Header_CountsChanges_Unchanged_AndAdminRows()
    {
        var plan = new SystemRestorePlan("environment");
        plan.Actions.Add(new PlannedAction { Id = "1", Component = "environment", Change = PlannedChange.Add, Name = "A", Scope = "Machine", NeedsAdmin = true, NewValue = "x" });
        plan.Actions.Add(new PlannedAction { Id = "2", Component = "environment", Change = PlannedChange.Change, Name = "B", Scope = "User", OldValue = "o", NewValue = "n" });
        plan.Actions.Add(new PlannedAction { Id = "3", Component = "environment", Change = PlannedChange.Unchanged, Name = "C", Scope = "User", NeedsAdmin = true });

        var text = SystemRestorePlanFormatter.Format(plan);

        text.Should().StartWith("System restore plan (environment): 2 change(s), 1 unchanged, 1 need administrator rights");
        text.Should().Contain("add       Machine  A [needs admin]");
        text.Should().Contain("change    User     B");
        text.Should().Contain("same      User     C");
    }

    [Fact]
    public void AddedAndChangedRows_ShowOldAndNewValues_ButAddedWithoutOldShowsOnlyNew()
    {
        var plan = new SystemRestorePlan("environment");
        plan.Actions.Add(new PlannedAction { Id = "1", Component = "environment", Change = PlannedChange.Add, Name = "NEWVAR", Scope = "User", NewValue = "fresh" });
        plan.Actions.Add(new PlannedAction { Id = "2", Component = "environment", Change = PlannedChange.Change, Name = "OLDVAR", Scope = "User", OldValue = "before", NewValue = "after" });

        var lines = SystemRestorePlanFormatter.Format(plan, reveal: true).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        lines.Should().ContainInOrder("      new: fresh", "      old: before", "      new: after");
        lines.Count(l => l.Contains("old:")).Should().Be(1);
    }

    [Fact]
    public void LongValues_AreShortenedToOneHundredCharacters()
    {
        var plan = new SystemRestorePlan("environment");
        plan.Actions.Add(new PlannedAction
        {
            Id = "1", Component = "environment", Change = PlannedChange.Change, Name = "PATH", Scope = "User",
            OldValue = new string('a', 100), NewValue = new string('b', 101)
        });

        var text = SystemRestorePlanFormatter.Format(plan, reveal: true);

        text.Should().Contain("old: " + new string('a', 100) + Environment.NewLine, "exactly 100 characters is not truncated");
        text.Should().Contain("new: " + new string('b', 100) + "...");
        text.Should().NotContain(new string('b', 101));
    }

    [Fact]
    public void ImportRows_ListTheirRegistryKeys_AndDetailIsParenthesised()
    {
        var plan = new SystemRestorePlan("settings");
        plan.Actions.Add(new PlannedAction
        {
            Id = "1", Component = "settings", Change = PlannedChange.Import, Name = "Explorer", Scope = "HKCU",
            Keys = [@"HKEY_CURRENT_USER\Software\A", @"HKEY_CURRENT_USER\Software\B"], Detail = "2 keys differ"
        });

        var text = SystemRestorePlanFormatter.Format(plan);

        text.Should().Contain(@"      key: HKEY_CURRENT_USER\Software\A");
        text.Should().Contain(@"      key: HKEY_CURRENT_USER\Software\B");
        text.Should().Contain("      (2 keys differ)");
        text.Should().NotContain("old:").And.NotContain("new:");
    }

    [Theory]
    [InlineData(PlannedChange.Add, "add")]
    [InlineData(PlannedChange.Change, "change")]
    [InlineData(PlannedChange.Unchanged, "same")]
    [InlineData(PlannedChange.Install, "install")]
    [InlineData(PlannedChange.AlreadyInstalled, "present")]
    [InlineData(PlannedChange.Unavailable, "manual")]
    [InlineData(PlannedChange.Import, "import")]
    [InlineData(PlannedChange.Create, "create")]
    [InlineData(PlannedChange.Replace, "replace")]
    [InlineData(PlannedChange.Skipped, "skip")]
    public void EveryChangeKind_HasAShortLabel(PlannedChange change, string label)
    {
        var plan = new SystemRestorePlan("x");
        plan.Actions.Add(new PlannedAction { Id = "1", Component = "x", Change = change, Name = "N", Scope = "S" });

        SystemRestorePlanFormatter.Format(plan).Split('\n')[1].TrimStart().Should().StartWith(label + " ");
    }

    [Fact]
    public void RejectedEntries_AreListedAfterTheActions()
    {
        var plan = new SystemRestorePlan("environment");
        plan.Actions.Add(new PlannedAction { Id = "1", Component = "environment", Change = PlannedChange.Unchanged, Name = "KEEP", Scope = "User" });
        plan.Rejected.Add("PATHEXT: not on the allow-list");

        var text = SystemRestorePlanFormatter.Format(plan);

        text.Should().EndWith("  rejected  PATHEXT: not on the allow-list");
        text.IndexOf("KEEP", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("rejected", StringComparison.Ordinal));
    }

    [Fact]
    public void Output_HasNoTrailingWhitespace()
    {
        var plan = new SystemRestorePlan("x");
        plan.Actions.Add(new PlannedAction { Id = "1", Component = "x", Change = PlannedChange.Skipped, Name = "N", Scope = "S" });

        var text = SystemRestorePlanFormatter.Format(plan);

        text.Should().Be(text.TrimEnd());
    }
}

[Collection("GlobalState")]
public class SystemRestoreServicesTests
{
    [Fact]
    public void CreateDefault_WiresEveryMachineFacingService()
    {
        var services = SystemRestoreServices.CreateDefault(new TestLogger());

        services.Environment.Should().BeOfType<DefaultEnvironmentVariableStore>();
        services.Programs.Should().BeOfType<DefaultInstalledProgramSource>();
        services.RegistryExporter.Should().BeOfType<DefaultRegistryKeyExporter>();
        services.RegistryImporter.Should().BeOfType<DefaultRegistryImporter>();
        services.Installer.Should().BeOfType<DefaultProgramInstaller>();
        services.DevTools.Should().BeOfType<DefaultDevToolsMachine>();
    }

    [Fact]
    public async Task RegistryExporter_RefusesHivesOtherThanCurrentUserAndLocalMachine()
    {
        var exporter = new DefaultRegistryKeyExporter(new TestLogger());

        var act = () => exporter.ExportKeyAsync(@"HKEY_USERS\S-1-5-18\Software", Path.Combine(Path.GetTempPath(), "never.reg"), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Unsupported registry hive");
    }

    [NonWindowsFact]
    public async Task InstalledProgramSource_ReturnsWhateverDiscoveryFinds_WithoutThrowing()
    {
        // Without winget on PATH, and on a machine without a registry, discovery degrades to an empty list.
        using var _ = FakeWinget.WithoutWinget();

        var programs = await new DefaultInstalledProgramSource(new TestLogger()).GetInstalledProgramsAsync(CancellationToken.None);

        programs.Should().BeEmpty();
    }
}
