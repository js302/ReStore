using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using System.Reflection;

namespace ReStore.Tests;

public class SystemBackupManagerHelperTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger;
    private readonly Mock<IConfigManager> _configMock;
    private readonly SystemBackupManager _manager;

    public SystemBackupManagerHelperTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreSystemBackupManagerHelperTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);

        _logger = new TestLogger();
        _configMock = new Mock<IConfigManager>();
        _configMock.SetupGet(c => c.GlobalStorageType).Returns("global-storage");
        _configMock.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig
        {
            StorageType = "default-system-storage",
            ProgramsStorageType = "program-storage",
            EnvironmentStorageType = "environment-storage",
            SettingsStorageType = "settings-storage"
        });

        var state = new Mock<SystemState>(_logger).Object;
        _manager = new SystemBackupManager(_logger, _configMock.Object, state);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Theory]
    [InlineData("system_programs", "system_backups/anything.zip", "programs")]
    [InlineData("system_environment", "system_backups/anything.zip", "environment")]
    [InlineData("system_settings", "system_backups/anything.zip", "settings")]
    [InlineData("all", "system_backups/programs/p.zip", "programs")]
    [InlineData("all", "system_backups/environment/e.zip", "environment")]
    [InlineData("all", "system_backups/settings/s.zip", "settings")]
    [InlineData("programs", "x", "programs")]
    public void ResolveRestoreComponent_ShouldNormalizeExpectedValues(string backupType, string backupPath, string expected)
    {
        var component = InvokePrivateStatic<string>(typeof(SystemBackupManager), "ResolveRestoreComponent", backupType, backupPath);

        component.Should().Be(expected);
    }

    [Fact]
    public void ResolveRestoreComponent_ShouldThrowForUnsupportedType()
    {
        var action = () => InvokePrivateStatic<string>(typeof(SystemBackupManager), "ResolveRestoreComponent", "unknown", "backup.zip");

        action.Should().Throw<TargetInvocationException>()
            .WithInnerException<ArgumentException>();
    }

    [Fact]
    public void CreateRestoreTempDirectory_AndCleanup_ShouldCreateAndRemoveDirectory()
    {
        var tempDir = InvokePrivateStatic<string>(typeof(SystemBackupManager), "CreateRestoreTempDirectory");

        Directory.Exists(tempDir).Should().BeTrue();

        InvokePrivate(_manager, "CleanupRestoreTempDirectory", tempDir);

        Directory.Exists(tempDir).Should().BeFalse();
    }

    [Fact]
    public void GetStorageTypeForComponent_ShouldRespectComponentOverrides()
    {
        InvokePrivate<string>(_manager, "GetStorageTypeForComponent", "programs").Should().Be("program-storage");
        InvokePrivate<string>(_manager, "GetStorageTypeForComponent", "environment").Should().Be("environment-storage");
        InvokePrivate<string>(_manager, "GetStorageTypeForComponent", "settings").Should().Be("settings-storage");
        InvokePrivate<string>(_manager, "GetStorageTypeForComponent", "unknown").Should().Be("global-storage");
    }

    [Fact]
    public async Task CreateWingetRestoreScriptAsync_ShouldContainOnlyWingetInstallCommands()
    {
        var programs = new List<InstalledProgram>
        {
            new() { Name = "Winget App", WingetId = "Contoso.WingetApp", IsWingetAvailable = true },
            new() { Name = "Manual App", IsWingetAvailable = false }
        };

        var scriptPath = Path.Combine(_testRoot, "restore_winget_programs.ps1");
        await InvokePrivateAsync(_manager, "CreateWingetRestoreScriptAsync", programs, scriptPath);

        var content = await File.ReadAllTextAsync(scriptPath);
        content.Should().Contain("Contoso.WingetApp");
        content.Should().Contain("Winget App");
        content.Should().NotContain("Manual App");
    }

    [Fact]
    public async Task CreateManualInstallListAsync_ShouldIncludeOnlyManualPrograms()
    {
        var programs = new List<InstalledProgram>
        {
            new() { Name = "Manual Z", Version = "3", Publisher = "P", IsWingetAvailable = false },
            new() { Name = "Winget A", WingetId = "A", IsWingetAvailable = true },
            new() { Name = "Manual A", Version = "1", Publisher = "Q", IsWingetAvailable = false }
        };

        var outputPath = Path.Combine(_testRoot, "manual_install_list.txt");
        await InvokePrivateAsync(_manager, "CreateManualInstallListAsync", programs, outputPath);

        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain("Total programs requiring manual installation: 2");
        content.Should().Contain("Manual A");
        content.Should().Contain("Manual Z");
        content.Should().NotContain("Winget A |");
    }

    [Fact]
    public async Task CreateFullRestoreScriptAsync_ShouldSummarizeManualOverflow()
    {
        var programs = new List<InstalledProgram>
        {
            new() { Name = "Winget App", WingetId = "Contoso.WingetApp", IsWingetAvailable = true }
        };

        for (int i = 0; i < 22; i++)
        {
            programs.Add(new InstalledProgram
            {
                Name = "Manual " + i,
                Version = "1.0",
                Publisher = "Publisher",
                IsWingetAvailable = false
            });
        }

        var scriptPath = Path.Combine(_testRoot, "restore_programs.ps1");
        await InvokePrivateAsync(_manager, "CreateFullRestoreScriptAsync", programs, scriptPath);

        var content = await File.ReadAllTextAsync(scriptPath);
        content.Should().Contain("Installing 1 programs via winget");
        content.Should().Contain("... and 2 more (see manual_install_list.txt)");
        content.Should().Contain("Manual installation required: 22");
    }

    [Fact]
    public async Task CreateRegistryBackupScriptAsync_ShouldWriteExpectedRegistryCommands()
    {
        var scriptPath = Path.Combine(_testRoot, "backup_env_registry.ps1");

        await InvokePrivateAsync(_manager, "CreateRegistryBackupScriptAsync", scriptPath);

        var content = await File.ReadAllTextAsync(scriptPath);
        content.Should().Contain("HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment");
        content.Should().Contain("HKCU\\Environment");
        content.Should().Contain("reg export");
    }

    [Fact]
    public async Task RestoreProgramsAsync_ShouldLogWhenScriptAndJsonExist()
    {
        var extractDir = Path.Combine(_testRoot, "programs");
        Directory.CreateDirectory(extractDir);
        await File.WriteAllTextAsync(Path.Combine(extractDir, "restore_programs.ps1"), "# script");
        await File.WriteAllTextAsync(Path.Combine(extractDir, "installed_programs.json"), "{}");

        await InvokePrivateAsync(_manager, "RestoreProgramsAsync", extractDir);

        _logger.Messages.Should().Contain(m => m.Contains("Programs restore script found"));
        _logger.Messages.Should().Contain(m => m.Contains("Programs backup data available"));
    }

    [Fact]
    public async Task RestoreWindowsSettingsAsync_ShouldLogScriptManifestAndWarning()
    {
        var extractDir = Path.Combine(_testRoot, "settings");
        Directory.CreateDirectory(extractDir);
        await File.WriteAllTextAsync(Path.Combine(extractDir, "restore_windows_settings.ps1"), "# script");
        await File.WriteAllTextAsync(Path.Combine(extractDir, "settings_manifest.json"), "{}");

        await InvokePrivateAsync(_manager, "RestoreWindowsSettingsAsync", extractDir);

        _logger.Messages.Should().Contain(m => m.Contains("Windows settings restore script available"));
        _logger.Messages.Should().Contain(m => m.Contains("Settings manifest available"));
        _logger.Messages.Should().Contain(m => m.Contains("IMPORTANT: Review the script before running"));
    }

    private static T InvokePrivate<T>(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (T)method!.Invoke(instance, args)!;
    }

    private static void InvokePrivate(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(instance, args);
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (T)method!.Invoke(null, args)!;
    }

    private static async Task InvokePrivateAsync(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var result = method!.Invoke(instance, args);
        if (result is Task task)
        {
            await task;
            return;
        }

        throw new InvalidOperationException($"Method {methodName} did not return Task.");
    }
}
