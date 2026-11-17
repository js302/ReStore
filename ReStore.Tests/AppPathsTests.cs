using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

[Collection("GlobalState")]
public class AppPathsTests : IDisposable
{
    private readonly string _originalOverride = AppPaths.Root;

    public void Dispose()
    {
        AppPaths.OverrideRoot(_originalOverride);
        Environment.SetEnvironmentVariable(AppPaths.RootEnvironmentVariable, null);
    }

    [Fact]
    public void OverrideRoot_ShouldRedirectAllSubPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReStoreTests", "apppaths_" + Guid.NewGuid().ToString("N"));
        AppPaths.OverrideRoot(root);

        AppPaths.Root.Should().Be(root);
        AppPaths.ConfigFile.Should().Be(Path.Combine(root, "config.json"));
        AppPaths.SystemStateFile.Should().Be(Path.Combine(root, "state", "system_state.json"));
        AppPaths.LogFile.Should().Be(Path.Combine(root, "logs", "restore.log"));
        AppPaths.ThemeSettingsFile.Should().StartWith(root);
        ConfigInitializer.GetUserConfigPath().Should().Be(AppPaths.ConfigFile);
    }

    [Fact]
    public void EnvironmentVariable_ShouldApplyWhenNoOverride_AndDefaultMatchesLegacyPath()
    {
        AppPaths.OverrideRoot(null);
        Environment.SetEnvironmentVariable(AppPaths.RootEnvironmentVariable, null);
        AppPaths.Root.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ReStore"));
        AppPaths.SystemStateFile.Should().Be(Path.Combine(AppPaths.Root, "state", "system_state.json"));

        var envRoot = Path.Combine(Path.GetTempPath(), "ReStoreTests", "env_" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(AppPaths.RootEnvironmentVariable, envRoot);
        AppPaths.Root.Should().Be(envRoot);

        var overrideRoot = Path.Combine(Path.GetTempPath(), "ReStoreTests", "ov_" + Guid.NewGuid().ToString("N"));
        AppPaths.OverrideRoot(overrideRoot);
        AppPaths.Root.Should().Be(overrideRoot);
    }
}
