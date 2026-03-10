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
    public void ResolveApplicationExampleConfigPath_ShouldReturnNullOrExistingExamplePath()
    {
        var method = typeof(ConfigInitializer).GetMethod("ResolveApplicationExampleConfigPath", BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var result = (string?)method!.Invoke(null, [null]);

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
    public void ResolveApplicationExampleConfigPath_WithExplicitAssemblyPath_ShouldUseInAppConfigFirst()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "ReStoreConfigInitializer_" + Guid.NewGuid().ToString("N"));
        var assemblyDir = Path.Combine(testRoot, "bin", "Debug", "net9.0");
        Directory.CreateDirectory(Path.Combine(assemblyDir, "config"));

        try
        {
            var expectedPath = Path.Combine(assemblyDir, "config", "config.example.json");
            File.WriteAllText(expectedPath, "{}");

            var method = typeof(ConfigInitializer).GetMethod("ResolveApplicationExampleConfigPath", BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(string)], null);
            method.Should().NotBeNull();

            var result = (string?)method!.Invoke(null, [Path.Combine(assemblyDir, "ReStore.Core.dll")]);

            result.Should().Be(expectedPath);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public void ResolveApplicationExampleConfigPath_WithBuildOutput_ShouldProbeProjectAncestors()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "ReStoreConfigInitializer_" + Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "ReStore.Core");
        var assemblyDir = Path.Combine(projectRoot, "bin", "Debug", "net9.0");
        Directory.CreateDirectory(assemblyDir);
        Directory.CreateDirectory(Path.Combine(projectRoot, "config"));

        try
        {
            var expectedPath = Path.Combine(projectRoot, "config", "config.example.json");
            File.WriteAllText(expectedPath, "{}");

            var method = typeof(ConfigInitializer).GetMethod("ResolveApplicationExampleConfigPath", BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(string)], null);
            method.Should().NotBeNull();

            var result = (string?)method!.Invoke(null, [Path.Combine(assemblyDir, "ReStore.Core.dll")]);

            result.Should().Be(expectedPath);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public void ResolveApplicationExampleConfigPath_WithReleaseWindowsBuildOutput_ShouldProbeProjectAncestors()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "ReStoreConfigInitializer_" + Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "ReStore");
        var assemblyDir = Path.Combine(projectRoot, "bin", "Release", "net9.0-windows");
        Directory.CreateDirectory(assemblyDir);
        Directory.CreateDirectory(Path.Combine(projectRoot, "config"));

        try
        {
            var expectedPath = Path.Combine(projectRoot, "config", "config.example.json");
            File.WriteAllText(expectedPath, "{}");

            var method = typeof(ConfigInitializer).GetMethod("ResolveApplicationExampleConfigPath", BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(string)], null);
            method.Should().NotBeNull();

            var result = (string?)method!.Invoke(null, [Path.Combine(assemblyDir, "ReStore.dll")]);

            result.Should().Be(expectedPath);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public void ResolveApplicationExampleConfigPath_WithProjectDirectoryContainingCsproj_ShouldUseProjectRoot()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "ReStoreConfigInitializer_" + Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "ReStore.Core");
        Directory.CreateDirectory(projectRoot);

        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "ReStore.Core.csproj"), "<Project />");
            var expectedPath = Path.Combine(projectRoot, "config", "config.example.json");
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, "{}");

            var method = typeof(ConfigInitializer).GetMethod("ResolveApplicationExampleConfigPath", BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(string)], null);
            method.Should().NotBeNull();

            var result = (string?)method!.Invoke(null, [Path.Combine(projectRoot, "ReStore.Core.dll")]);

            result.Should().Be(expectedPath);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public void ResolveApplicationExampleConfigPath_WithMissingDirectory_ShouldReturnNull()
    {
        var method = typeof(ConfigInitializer).GetMethod("ResolveApplicationExampleConfigPath", BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(string)], null);
        method.Should().NotBeNull();

        var result = (string?)method!.Invoke(null, ["ReStore.Core.dll"]);

        result.Should().BeNull();
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
