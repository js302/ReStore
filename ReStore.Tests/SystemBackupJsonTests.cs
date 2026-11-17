using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Export -> import round trips using the real exporters, plus fixtures in the exact shape
/// v1.7 wrote (C5: the importers used to read camelCase JSON case-sensitively and restored nothing).
/// </summary>
[Collection("GlobalState")]
public class SystemBackupJsonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreSysJsonTests_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();

    public SystemBackupJsonTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task EnvironmentVariables_RoundTrip_WithRealExporterOutput()
    {
        var manager = new EnvironmentVariablesManager(_logger);
        var path = Path.Combine(_root, "env.json");
        await manager.ExportEnvironmentVariablesToJsonAsync(
        [
            new() { Name = "A_USER", Value = "1", Target = EnvironmentVariableTarget.User },
            new() { Name = "B_MACHINE", Value = "2", Target = EnvironmentVariableTarget.Machine }
        ], path);

        var loaded = await EnvironmentVariablesManager.LoadVariablesFromJsonAsync(path);

        loaded.Should().BeEquivalentTo(
        [
            new EnvironmentVariableEntry { Name = "A_USER", Value = "1", Target = EnvironmentVariableTarget.User },
            new EnvironmentVariableEntry { Name = "B_MACHINE", Value = "2", Target = EnvironmentVariableTarget.Machine }
        ]);
    }

    [Fact]
    public async Task EnvironmentVariables_V17Shape_CamelCaseWithNumericEnum_Imports()
    {
        // v1.7 serialised the enum as a number (no string converter) with camelCase property names.
        var path = Path.Combine(_root, "env17.json");
        await File.WriteAllTextAsync(path, """
{
  "exportDate": "2026-01-01T00:00:00Z",
  "computerName": "PC",
  "userName": "me",
  "totalVariables": 2,
  "systemVariables": 1,
  "userVariables": 1,
  "variables": [
    { "name": "FOO", "value": "bar", "target": 2, "targetName": "Machine" },
    { "name": "BAZ", "value": "qux", "target": 1, "targetName": "User" }
  ]
}
""");

        var loaded = await EnvironmentVariablesManager.LoadVariablesFromJsonAsync(path);

        loaded.Should().HaveCount(2);
        loaded[0].Name.Should().Be("FOO");
        loaded[0].Value.Should().Be("bar");
        loaded[0].Target.Should().Be(EnvironmentVariableTarget.Machine);
        loaded[1].Target.Should().Be(EnvironmentVariableTarget.User);
    }

    [Fact]
    public async Task EnvironmentVariables_PascalCaseAndStringEnum_StillImport()
    {
        var path = Path.Combine(_root, "envpascal.json");
        await File.WriteAllTextAsync(path,
            """{ "Variables": [ { "Name": "X", "Value": "y", "Target": "user" } ] }""");

        var loaded = await EnvironmentVariablesManager.LoadVariablesFromJsonAsync(path);

        loaded.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new EnvironmentVariableEntry { Name = "X", Value = "y", Target = EnvironmentVariableTarget.User });
    }

    [Fact]
    public async Task EnvironmentVariables_AllEmptyNames_IsRejected()
    {
        var path = Path.Combine(_root, "envempty.json");
        await File.WriteAllTextAsync(path, """{ "variables": [ { "nom": "A" }, { "foo": "B" } ] }""");

        var manager = new EnvironmentVariablesManager(_logger);
        var act = () => manager.RestoreEnvironmentVariablesAsync(path);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*none has a name*");
    }

    [Fact]
    public async Task Programs_RoundTrip_WithRealExporterOutput()
    {
        var path = Path.Combine(_root, "programs.json");
        var discovery = new SystemProgramDiscovery(_logger);
        await discovery.ExportProgramsToJsonAsync(
        [
            new() { Name = "Git", WingetId = "Git.Git", IsWingetAvailable = true },
            new() { Name = "Legacy", IsWingetAvailable = false }
        ], path);

        var result = await new ProgramRestoreManager(_logger).RestoreProgramsFromJsonAsync(path, dryRun: true);

        result.WingetPrograms.Should().Be(1);
        result.ManualPrograms.Should().Be(1);
    }

    [Fact]
    public async Task Programs_V17Shape_Imports()
    {
        var path = Path.Combine(_root, "programs17.json");
        await File.WriteAllTextAsync(path, """
{
  "exportDate": "2026-01-01T00:00:00Z",
  "totalPrograms": 1,
  "wingetAvailable": 1,
  "programs": [
    { "name": "Git", "version": "2", "publisher": "x", "installDate": "", "installLocation": "",
      "uninstallString": "", "source": "winget", "wingetId": "Git.Git", "isWingetAvailable": true }
  ]
}
""");

        var result = await new ProgramRestoreManager(_logger).RestoreProgramsFromJsonAsync(path, dryRun: true);

        result.WingetPrograms.Should().Be(1);
        result.ManualPrograms.Should().Be(0);
    }

    [Fact]
    public async Task Programs_AllEmptyNamesAndIds_IsRejectedWithClearError()
    {
        var path = Path.Combine(_root, "programsempty.json");
        await File.WriteAllTextAsync(path, """{ "programs": [ { "nombre": "x" }, { "other": 1 } ] }""");

        var result = await new ProgramRestoreManager(_logger).RestoreProgramsFromJsonAsync(path, dryRun: true);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("none has a name or winget id");
    }

    [Fact]
    public async Task WindowsSettingsManifest_RealExporterOutput_ReadsBack_AndV17PascalCaseStillReads()
    {
        var dir = Path.Combine(_root, "settings");
        var manager = new WindowsSettingsManager(_logger);
        var exported = await manager.ExportWindowsSettingsAsync(dir, ["UnknownCategory"]);

        var json = await File.ReadAllTextAsync(Path.Combine(dir, "settings_manifest.json"));
        var read = JsonSerializer.Deserialize<WindowsSettingsExport>(json, JsonDefaults.SystemBackup)!;
        read.ComputerName.Should().Be(exported.ComputerName);
        read.UserName.Should().Be(exported.UserName);

        // v1.7 wrote this manifest PascalCase (no naming policy).
        var v17 = JsonSerializer.Deserialize<WindowsSettingsExport>("""
{ "ExportDate": "2026-01-01T00:00:00", "ComputerName": "PC", "UserName": "me",
  "ExportedCategories": ["Mouse"], "ExportedFiles": { "HKEY_CURRENT_USER\\Control Panel\\Mouse": "Mouse_Mouse.reg" } }
""", JsonDefaults.SystemBackup)!;
        v17.ComputerName.Should().Be("PC");
        v17.ExportedCategories.Should().Equal("Mouse");
        v17.ExportedFiles.Should().ContainSingle().Which.Value.Should().Be("Mouse_Mouse.reg");
    }

    [Fact]
    public async Task RestoreSystemAsync_CopiesScriptsToDurableDirectory_ThatSurvivesTempCleanup()
    {
        var home = Path.Combine(_root, "home");
        var previousRoot = AppPaths.Root;
        AppPaths.OverrideRoot(home);
        try
        {
            var zipSource = Path.Combine(_root, "payload");
            Directory.CreateDirectory(zipSource);
            await File.WriteAllTextAsync(Path.Combine(zipSource, "restore_programs.ps1"), "Write-Host hi");
            await File.WriteAllTextAsync(Path.Combine(zipSource, "installed_programs.json"), "{}");
            var zipFile = Path.Combine(_root, "src.zip");
            ZipFile.CreateFromDirectory(zipSource, zipFile);

            var storage = new Mock<IStorage>();
            storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((_, dest, _) =>
                {
                    File.Copy(zipFile, dest, overwrite: true);
                    return Task.CompletedTask;
                });
            var config = new Mock<IConfigManager>();
            config.SetupGet(c => c.GlobalStorageType).Returns("g");
            config.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig());
            config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage.Object);
            var state = new Mock<SystemState>(_logger);

            var manager = new SystemBackupManager(_logger, config.Object, state.Object);
            await manager.RestoreSystemAsync("programs", "system_backups/programs/x.zip");

            var scriptsRoot = Path.Combine(home, "restore-scripts");
            var script = Directory.GetFiles(scriptsRoot, "restore_programs.ps1", SearchOption.AllDirectories)
                .Should().ContainSingle().Subject;
            File.ReadAllText(script).Should().Be("Write-Host hi");
            Path.GetFileName(Path.GetDirectoryName(script)).Should().MatchRegex(@"^\d{8}-\d{6}(-\d+)?$");
            _logger.Messages.Should().Contain(m => m.Contains("Script location: " + script));
        }
        finally
        {
            AppPaths.OverrideRoot(previousRoot);
        }
    }
}
