using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class StartupModeTests
{
    [Fact]
    public void NormalLaunch_OpensTheWindow()
    {
        StartupMode.Decide(false, [], true).WindowMode.Should().Be(StartupWindowMode.Normal);
        StartupMode.Decide(false, null, false).WindowMode.Should().Be(StartupWindowMode.Normal);
        StartupMode.Decide(false, ["/startWatcher"], true).WindowMode.Should().Be(StartupWindowMode.Normal);
    }

    [Fact]
    public void StartupLaunch_WithTray_StartsHiddenInTray()
    {
        StartupMode.Decide(false, ["--startup"], true).WindowMode.Should().Be(StartupWindowMode.HiddenInTray);
        StartupMode.Decide(true, ["--STARTUP"], true).WindowMode.Should().Be(StartupWindowMode.HiddenInTray);
    }

    [Fact]
    public void StartupLaunch_WithoutTray_MinimizesToTaskbarSoTheWindowStaysReachable()
    {
        StartupMode.Decide(false, ["--startup"], false).WindowMode.Should().Be(StartupWindowMode.MinimizedToTaskbar);
    }

    [Theory]
    [InlineData("--startup", true)]
    [InlineData("--STARTUP", true)]
    [InlineData("--Startup", true)]
    [InlineData("/startup", false)]
    [InlineData(null, false)]
    public void IsStartupArgument_IsCaseInsensitive(string? arg, bool expected)
    {
        StartupMode.IsStartupArgument(arg).Should().Be(expected);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RunKeyToggle_IsOnlyOfferedWhenUnpackaged(bool packaged, bool offered)
    {
        StartupMode.Decide(packaged, [], false).CanConfigureRunAtStartup.Should().Be(offered);
    }

    [Fact]
    public void StartupTaskActivation_StartsHiddenWithoutArguments()
    {
        StartupMode.Decide(true, [], true, activatedByStartupTask: true).WindowMode.Should().Be(StartupWindowMode.HiddenInTray);
        StartupMode.Decide(true, [], false, activatedByStartupTask: true).WindowMode.Should().Be(StartupWindowMode.MinimizedToTaskbar);
        StartupMode.Decide(true, [], true).WindowMode.Should().Be(StartupWindowMode.Normal);
    }

    [Theory]
    [InlineData(StartupTaskStatus.Enabled, true, true)]
    [InlineData(StartupTaskStatus.Disabled, false, true)]
    [InlineData(StartupTaskStatus.DisabledByUser, false, false)]
    [InlineData(StartupTaskStatus.DisabledByPolicy, false, false)]
    [InlineData(StartupTaskStatus.EnabledByPolicy, true, false)]
    public void StartupTaskUi_MapsStateToCheckboxAndToggle(StartupTaskStatus status, bool isChecked, bool canToggle)
    {
        var ui = StartupTaskUi.Describe(status);
        ui.IsChecked.Should().Be(isChecked);
        ui.CanToggle.Should().Be(canToggle);
        ui.Note.Should().NotBeNullOrWhiteSpace();
    }
}
