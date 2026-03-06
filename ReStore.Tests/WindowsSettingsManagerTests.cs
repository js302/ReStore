using FluentAssertions;
using ReStore.Core.src.utils;
using System.Reflection;
using System.Text.Json;

namespace ReStore.Tests;

public class WindowsSettingsManagerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger;
    private readonly WindowsSettingsManager _manager;

    public WindowsSettingsManagerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreWindowsSettingsManagerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);

        _logger = new TestLogger();
        _manager = new WindowsSettingsManager(_logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public void GetAvailableCategories_ShouldContainExpectedCategories()
    {
        var categories = _manager.GetAvailableCategories();

        categories.Should().Contain(["Personalization", "Explorer", "Regional", "Taskbar", "Mouse", "Keyboard", "Accessibility", "System"]);
    }

    [Fact]
    public void GetAllCategories_ShouldExposeSystemKeysRequiringAdmin()
    {
        var categories = _manager.GetAllCategories();

        categories.Should().ContainKey("System");
        categories["System"].Should().NotBeEmpty();
        categories["System"].Should().OnlyContain(k => k.RequiresAdmin);
    }

    [Fact]
    public async Task ExportWindowsSettingsAsync_ShouldSkipUnknownCategory_AndCreateManifest()
    {
        var outputDirectory = Path.Combine(_testRoot, "export");

        var export = await _manager.ExportWindowsSettingsAsync(outputDirectory, ["UnknownCategory"]);

        export.ExportedCategories.Should().BeEmpty();
        export.ExportedFiles.Should().BeEmpty();

        var manifestPath = Path.Combine(outputDirectory, "settings_manifest.json");
        File.Exists(manifestPath).Should().BeTrue();

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = doc.RootElement;
        root.GetProperty("ExportedCategories").GetArrayLength().Should().Be(0);
        root.GetProperty("ExportedFiles").EnumerateObject().Should().BeEmpty();

        _logger.Messages.Should().Contain(m => m.Contains("Unknown category: UnknownCategory"));
    }

    [Fact]
    public async Task CreateRestoreScriptAsync_ShouldIncludeAdminAndUserSections()
    {
        var export = new WindowsSettingsExport
        {
            ExportedFiles = new Dictionary<string, string>
            {
                ["HKEY_CURRENT_USER\\Control Panel\\Desktop"] = "Personalization_Desktop.reg",
                ["HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Power"] = "System_Power.reg"
            }
        };

        var outputPath = Path.Combine(_testRoot, "restore_windows_settings.ps1");
        var createdPath = await _manager.CreateRestoreScriptAsync(export, _testRoot, outputPath);

        createdPath.Should().Be(outputPath);
        var content = await File.ReadAllTextAsync(outputPath);

        content.Should().Contain("if (-not $isAdmin)");
        content.Should().Contain("Importing user-level settings");
        content.Should().Contain("Importing system-level settings (requires admin)");
        content.Should().Contain("Skipped 1 system-level settings (requires admin)");
        content.Should().Contain("Personalization_Desktop");
        content.Should().Contain("System_Power");
    }

    [Fact]
    public async Task ExportRegistryKeyAsync_ShouldReturnFalse_ForMissingRegistryPath()
    {
        var outputPath = Path.Combine(_testRoot, "missing.reg");
        var registryPath = "HKEY_CURRENT_USER\\Software\\ReStore_Test_Missing_" + Guid.NewGuid().ToString("N");

        var result = await InvokePrivateAsync<bool>(_manager, "ExportRegistryKeyAsync", registryPath, outputPath);

        result.Should().BeFalse();
    }

    private static async Task<T> InvokePrivateAsync<T>(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var result = method!.Invoke(instance, args);
        if (result is Task<T> typedTask)
        {
            return await typedTask;
        }

        throw new InvalidOperationException($"Method {methodName} did not return Task<{typeof(T).Name}>.");
    }
}
