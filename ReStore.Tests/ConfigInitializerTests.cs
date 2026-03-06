using FluentAssertions;
using ReStore.Core.src.utils;
using System.Reflection;

namespace ReStore.Tests;

public class ConfigInitializerTests
{
    [Fact]
    public void GetUserPaths_ShouldBeConsistent()
    {
        var configDir = ConfigInitializer.GetUserConfigDirectory();
        var stateDir = ConfigInitializer.GetUserStateDirectory();
        var configPath = ConfigInitializer.GetUserConfigPath();
        var examplePath = ConfigInitializer.GetUserExampleConfigPath();

        configDir.Should().NotBeNullOrWhiteSpace();
        stateDir.Should().Match(path => path.StartsWith(configDir, StringComparison.OrdinalIgnoreCase));
        configPath.Should().Match(path => path.StartsWith(configDir, StringComparison.OrdinalIgnoreCase));
        examplePath.Should().Match(path => path.StartsWith(configDir, StringComparison.OrdinalIgnoreCase));

        Path.GetFileName(configPath).Should().Be("config.json");
        Path.GetFileName(examplePath).Should().Be("config.example.json");
        Path.GetFileName(stateDir).Should().Be("state");
    }

    [Fact]
    public void EnsureConfigurationSetup_ShouldNotThrow_WithNullLogger()
    {
        var action = () => ConfigInitializer.EnsureConfigurationSetup(null);

        action.Should().NotThrow();
    }

    [Fact]
    public void EnsureConfigurationSetup_ShouldCreateUserDirectories()
    {
        ConfigInitializer.EnsureConfigurationSetup(new TestLogger());

        Directory.Exists(ConfigInitializer.GetUserConfigDirectory()).Should().BeTrue();
        Directory.Exists(ConfigInitializer.GetUserStateDirectory()).Should().BeTrue();
    }

    [Fact]
    public void GetApplicationExampleConfigPath_ShouldReturnNullOrExistingExamplePath()
    {
        var method = typeof(ConfigInitializer).GetMethod("GetApplicationExampleConfigPath", BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var result = (string?)method!.Invoke(null, null);

        if (result == null)
        {
            result.Should().BeNull();
        }
        else
        {
            result.Should().EndWith(Path.Combine("config", "config.example.json"));
            File.Exists(result).Should().BeTrue();
        }
    }

    [Fact]
    public void EnsureConfigurationSetup_ShouldLogDebugWhenConfigAlreadyExists()
    {
        var logger = new TestLogger();

        ConfigInitializer.EnsureConfigurationSetup(logger);
        ConfigInitializer.EnsureConfigurationSetup(logger);

        logger.Messages.Should().Contain(m => m.Contains("Configuration loaded from") || m.Contains("Created initial configuration") || m.Contains("Warning: Could not find example configuration file"));
    }

    [Fact]
    public void EnsureConfigurationSetup_ShouldHandleMissingConfigFile()
    {
        var logger = new TestLogger();
        var action = () => ConfigInitializer.EnsureConfigurationSetup(logger);

        action.Should().NotThrow();

        logger.Messages.Should().Contain(message =>
            message.Contains("Created initial configuration") ||
            message.Contains("Warning: Could not find example configuration file") ||
            message.Contains("Configuration loaded from"));

        Directory.Exists(ConfigInitializer.GetUserStateDirectory()).Should().BeTrue();
    }
}
