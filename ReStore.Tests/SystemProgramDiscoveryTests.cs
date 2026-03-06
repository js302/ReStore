using FluentAssertions;
using Microsoft.Win32;
using ReStore.Core.src.utils;
using System.Reflection;
using System.Text.Json;

namespace ReStore.Tests;

public class SystemProgramDiscoveryTests : IDisposable
{
    private readonly string _testRoot;
    private readonly SystemProgramDiscovery _discovery;

    public SystemProgramDiscoveryTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreProgramDiscoveryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _discovery = new SystemProgramDiscovery(new TestLogger());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public void ParseWingetOutput_ShouldParseExpectedPrograms()
    {
        const string wingetOutput = """
Name                        Id                            Version      Available Source
---------------------------------------------------------------------------------------
App One                     Contoso.AppOne                1.2.3                  winget
App Two                     Fabrikam.AppTwo               2.0.1
""";

        var programs = InvokePrivate<List<InstalledProgram>>(_discovery, "ParseWingetOutput", wingetOutput);

        programs.Should().HaveCount(2);

        programs[0].Name.Should().Be("App One");
        programs[0].WingetId.Should().Be("Contoso.AppOne");
        programs[0].Version.Should().Be("1.2.3");
        programs[0].Source.Should().Be("winget");
        programs[0].IsWingetAvailable.Should().BeTrue();

        programs[1].Name.Should().Be("App Two");
        programs[1].Source.Should().Be("registry");
        programs[1].IsWingetAvailable.Should().BeFalse();
    }

    [Fact]
    public void ParseWingetOutput_ShouldReturnEmpty_WhenHeaderMissing()
    {
        var programs = InvokePrivate<List<InstalledProgram>>(_discovery, "ParseWingetOutput", "random output without expected columns");

        programs.Should().BeEmpty();
    }

    [Fact]
    public void FormatInstallDate_ShouldFormatEightDigitDate_AndIgnoreInvalid()
    {
        var validDate = InvokePrivateStatic<string>(typeof(SystemProgramDiscovery), "FormatInstallDate", "20250214");
        var invalidDate = InvokePrivateStatic<string>(typeof(SystemProgramDiscovery), "FormatInstallDate", "abc");
        var nullDate = InvokePrivateStatic<string>(typeof(SystemProgramDiscovery), "FormatInstallDate", [null]);

        validDate.Should().Be("2025-02-14");
        invalidDate.Should().BeEmpty();
        nullDate.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportProgramsToJsonAsync_ShouldWriteExpectedMetadata()
    {
        var programs = new List<InstalledProgram>
        {
            new() { Name = "A", Publisher = "P", Version = "1", Source = "winget", IsWingetAvailable = true },
            new() { Name = "B", Publisher = "Q", Version = "2", Source = "registry", IsWingetAvailable = false }
        };

        var outputPath = Path.Combine(_testRoot, "programs.json");
        var exportedPath = await _discovery.ExportProgramsToJsonAsync(programs, outputPath);

        exportedPath.Should().Be(outputPath);
        File.Exists(outputPath).Should().BeTrue();

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var root = doc.RootElement;

        root.GetProperty("totalPrograms").GetInt32().Should().Be(2);
        root.GetProperty("wingetAvailable").GetInt32().Should().Be(1);
        root.GetProperty("programs").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void GetProgramsFromRegistryPath_ShouldReturnOnlyValidPrograms()
    {
        var relativePath = CreateTestRegistryPath("GetPrograms");

        try
        {
            using (var root = Registry.CurrentUser.CreateSubKey(relativePath))
            {
                root.Should().NotBeNull();

                using var included = root!.CreateSubKey("IncludedApp");
                included!.SetValue("DisplayName", "Included Application");
                included.SetValue("DisplayVersion", "1.2.3");
                included.SetValue("Publisher", "ReStore");
                included.SetValue("InstallDate", "20250301");
                included.SetValue("InstallLocation", "C:\\Apps\\Included");
                included.SetValue("UninstallString", "uninstall.exe /x");

                using var update = root.CreateSubKey("WindowsUpdate");
                update!.SetValue("DisplayName", "Update for Windows");
                update.SetValue("UninstallString", "update-uninstall.exe");

                using var noUninstall = root.CreateSubKey("NoUninstall");
                noUninstall!.SetValue("DisplayName", "No Uninstall App");
                noUninstall.SetValue("DisplayVersion", "9.9");
            }

            var programs = InvokePrivate<List<InstalledProgram>>(
                _discovery,
                "GetProgramsFromRegistryPath",
                Registry.CurrentUser,
                relativePath);

            programs.Should().HaveCount(1);
            programs[0].Name.Should().Be("Included Application");
            programs[0].Version.Should().Be("1.2.3");
            programs[0].Publisher.Should().Be("ReStore");
            programs[0].InstallDate.Should().Be("2025-03-01");
            programs[0].Source.Should().Be("registry");
        }
        finally
        {
            TryDeleteRegistryTree(relativePath);
        }
    }

    [Fact]
    public void ShouldSkipProgram_ShouldDetectSystemComponentAndMissingUninstall()
    {
        var relativePath = CreateTestRegistryPath("ShouldSkip");

        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(relativePath);
            root.Should().NotBeNull();

            using var systemComponent = root!.CreateSubKey("SystemComponentApp");
            systemComponent!.SetValue("DisplayName", "Normal Name");
            systemComponent.SetValue("SystemComponent", "1");
            systemComponent.SetValue("UninstallString", "uninstall.exe /x");

            using var missingUninstall = root.CreateSubKey("MissingUninstall");
            missingUninstall!.SetValue("DisplayName", "Missing Uninstall");

            using var normal = root.CreateSubKey("Normal");
            normal!.SetValue("DisplayName", "Normal App");
            normal.SetValue("UninstallString", "uninstall.exe /x");

            var shouldSkipBySystemComponent = InvokePrivate<bool>(
                _discovery,
                "ShouldSkipProgram",
                "Normal Name",
                systemComponent);

            var shouldSkipByMissingUninstall = InvokePrivate<bool>(
                _discovery,
                "ShouldSkipProgram",
                "Missing Uninstall",
                missingUninstall);

            var shouldNotSkipNormal = InvokePrivate<bool>(
                _discovery,
                "ShouldSkipProgram",
                "Normal App",
                normal);

            shouldSkipBySystemComponent.Should().BeTrue();
            shouldSkipByMissingUninstall.Should().BeTrue();
            shouldNotSkipNormal.Should().BeFalse();
        }
        finally
        {
            TryDeleteRegistryTree(relativePath);
        }
    }

    [Fact]
    public void ParseWingetOutput_ShouldIgnoreMalformedOrSeparatorLikeRows()
    {
        const string wingetOutput = """
Name                        Id                            Version      Available Source
---------------------------------------------------------------------------------------
BadLineWithoutColumns
-----
Good App                    Contoso.GoodApp              4.5.6                  winget
""";

        var programs = InvokePrivate<List<InstalledProgram>>(_discovery, "ParseWingetOutput", wingetOutput);

        programs.Should().ContainSingle(p => p.Name == "Good App");
        programs.Should().OnlyContain(p => !p.Name.StartsWith("-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetWingetProgramsAsync_ShouldReturnList_WithoutThrowing()
    {
        var result = await InvokePrivateAsync<List<InstalledProgram>>(_discovery, "GetWingetProgramsAsync");

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckSingleProgramWingetAvailability_ShouldReturnFalse_ForMissingPackage()
    {
        var missingName = "ReStoreDefinitelyMissing_" + Guid.NewGuid().ToString("N");

        var isAvailable = await InvokePrivateAsync<bool>(_discovery, "CheckSingleProgramWingetAvailability", missingName);

        isAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task GetWingetIdForProgram_ShouldReturnEmpty_ForMissingPackage()
    {
        var missingName = "ReStoreDefinitelyMissing_" + Guid.NewGuid().ToString("N");

        var wingetId = await InvokePrivateAsync<string>(_discovery, "GetWingetIdForProgram", missingName);

        wingetId.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckWingetAvailabilityAsync_ShouldCompleteWithoutThrowing()
    {
        var programs = Enumerable.Range(1, 12)
            .Select(index => new InstalledProgram { Name = $"ReStoreMissing-{index}-{Guid.NewGuid():N}", Source = "registry" })
            .ToList();

        var action = async () => await InvokePrivateAsync<object?>(_discovery, "CheckWingetAvailabilityAsync", programs);

        await action.Should().NotThrowAsync();
        programs.Should().HaveCount(12);
    }

    private static string CreateTestRegistryPath(string prefix)
    {
        return $"Software\\ReStoreTests\\{prefix}_{Guid.NewGuid():N}";
    }

    private static void TryDeleteRegistryTree(string relativePath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
        catch
        {
        }
    }

    private static T InvokePrivate<T>(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (T)method!.Invoke(instance, args)!;
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (T)method!.Invoke(null, args)!;
    }

    private static async Task<T> InvokePrivateAsync<T>(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var invocationResult = method!.Invoke(instance, args);
        invocationResult.Should().NotBeNull();

        if (invocationResult is Task<T> genericTask)
        {
            return await genericTask;
        }

        if (invocationResult is Task nonGenericTask)
        {
            await nonGenericTask;
            return default!;
        }

        throw new InvalidOperationException($"Method '{methodName}' did not return a Task.");
    }
}
