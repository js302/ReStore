using FluentAssertions;
using ReStore.Core.src.backup;
using ReStore.Core.src.utils;
using System.Text.Json;

namespace ReStore.Tests;

public class ProgramRestoreManagerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger;

    public ProgramRestoreManagerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreProgramRestoreTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_testRoot);
        _logger = new TestLogger();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task RestoreProgramsFromJsonAsync_ShouldReturnFailure_WhenJsonFileMissing()
    {
        var manager = new ProgramRestoreManager(_logger);
        var missingPath = Path.Combine(_testRoot, "missing.json");

        var result = await manager.RestoreProgramsFromJsonAsync(missingPath, dryRun: true);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RestoreProgramsFromJsonAsync_ShouldClassifyPrograms_WhenDryRun()
    {
        var manager = new ProgramRestoreManager(_logger);
        var jsonPath = Path.Combine(_testRoot, "programs.json");

        var payload = new
        {
            programs = new[]
            {
                new InstalledProgram { Name = "WingetApp", WingetId = "Vendor.WingetApp", IsWingetAvailable = true },
                new InstalledProgram { Name = "ManualApp", WingetId = "", IsWingetAvailable = false }
            }
        };

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(payload));

        var result = await manager.RestoreProgramsFromJsonAsync(jsonPath, wingetOnly: false, dryRun: true);

        result.Success.Should().BeTrue();
        result.WingetPrograms.Should().Be(1);
        result.ManualPrograms.Should().Be(1);
        result.SuccessfulInstalls.Should().Be(0);
        result.FailedInstalls.Should().Be(0);
    }

    [Fact]
    public async Task RestoreProgramsFromJsonAsync_ShouldReturnFailure_WhenJsonIsInvalid()
    {
        var manager = new ProgramRestoreManager(_logger);
        var jsonPath = Path.Combine(_testRoot, "invalid.json");
        await File.WriteAllTextAsync(jsonPath, "{ not valid json }");

        var result = await manager.RestoreProgramsFromJsonAsync(jsonPath, dryRun: true);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        _logger.Messages.Should().Contain(message => message.Contains("Error restoring programs"));
    }

    [Fact]
    public async Task RestoreProgramsFromJsonAsync_ShouldKeepDefaultResult_WhenProgramsPropertyIsNull()
    {
        var manager = new ProgramRestoreManager(_logger);
        var jsonPath = Path.Combine(_testRoot, "null-programs.json");

        await File.WriteAllTextAsync(jsonPath, """
{
  "programs": null
}
""");

        var result = await manager.RestoreProgramsFromJsonAsync(jsonPath, dryRun: true);

        result.Success.Should().BeTrue();
        result.WingetPrograms.Should().Be(0);
        result.ManualPrograms.Should().Be(0);
        result.SuccessfulInstalls.Should().Be(0);
        result.FailedInstalls.Should().Be(0);
    }

    [Fact]
    public async Task RestoreProgramsAsync_ShouldMarkManualPrograms_WhenNotWingetOnly()
    {
        var manager = new ProgramRestoreManager(_logger);
        var programs = new List<InstalledProgram>
        {
            new() { Name = "ManualOne", IsWingetAvailable = false },
            new() { Name = "ManualTwo", WingetId = "", IsWingetAvailable = true }
        };

        var result = await manager.RestoreProgramsAsync(programs, wingetOnly: false, dryRun: false);

        result.Success.Should().BeTrue();
        result.WingetPrograms.Should().Be(0);
        result.ManualPrograms.Should().Be(2);
        result.ManualInstallRequired.Should().HaveCount(2);
        result.FailedInstalls.Should().Be(0);
        result.SuccessfulInstalls.Should().Be(0);
    }

    [Fact]
    public async Task GenerateInstallationReportAsync_ShouldWriteValidReportJson()
    {
        var manager = new ProgramRestoreManager(_logger);
        var reportPath = Path.Combine(_testRoot, "installation-report.json");

        var result = new ProgramRestoreResult
        {
            Success = true,
            WingetPrograms = 2,
            ManualPrograms = 1,
            SuccessfulInstalls = 2,
            FailedInstalls = 0,
            ErrorMessage = ""
        };

        await manager.GenerateInstallationReportAsync(result, reportPath);

        File.Exists(reportPath).Should().BeTrue();
        var json = await File.ReadAllTextAsync(reportPath);
        var root = JsonSerializer.Deserialize<JsonElement>(json);

        root.TryGetProperty("summary", out var summary).Should().BeTrue();
        summary.GetProperty("wingetPrograms").GetInt32().Should().Be(2);
        summary.GetProperty("manualPrograms").GetInt32().Should().Be(1);
        summary.GetProperty("successfulInstalls").GetInt32().Should().Be(2);
        summary.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GenerateInstallationReportAsync_ShouldPersistDetailedLists_AndErrorMessage()
    {
        var manager = new ProgramRestoreManager(_logger);
        var reportPath = Path.Combine(_testRoot, "installation-report-detailed.json");

        var result = new ProgramRestoreResult
        {
            Success = false,
            WingetPrograms = 1,
            ManualPrograms = 1,
            SuccessfulInstalls = 0,
            FailedInstalls = 1,
            ErrorMessage = "winget failed",
            FailedPrograms =
            [
                new InstalledProgram { Name = "FailedApp", WingetId = "Vendor.FailedApp", IsWingetAvailable = true }
            ],
            ManualInstallRequired =
            [
                new InstalledProgram { Name = "ManualApp", IsWingetAvailable = false }
            ]
        };

        await manager.GenerateInstallationReportAsync(result, reportPath);

        var root = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(reportPath));
        root.GetProperty("errorMessage").GetString().Should().Be("winget failed");
        root.GetProperty("failedPrograms").EnumerateArray().Should().ContainSingle()
            .Subject.GetProperty("name").GetString().Should().Be("FailedApp");
        root.GetProperty("manualInstallRequired").EnumerateArray().Should().ContainSingle()
            .Subject.GetProperty("name").GetString().Should().Be("ManualApp");
    }

    [Fact]
    public async Task RestoreProgramsFromJsonAsync_ShouldKeepDefaultResult_WhenProgramsPropertyMissing()
    {
        var manager = new ProgramRestoreManager(_logger);
        var jsonPath = Path.Combine(_testRoot, "no-programs.json");

        await File.WriteAllTextAsync(jsonPath, "{}");

        var result = await manager.RestoreProgramsFromJsonAsync(jsonPath, dryRun: false);

        result.Success.Should().BeTrue();
        result.WingetPrograms.Should().Be(0);
        result.ManualPrograms.Should().Be(0);
        result.SuccessfulInstalls.Should().Be(0);
        result.FailedInstalls.Should().Be(0);
    }

    [Fact]
    public async Task CheckProgramStatusAsync_ShouldReturnEmpty_WhenNoProgramsProvided()
    {
        var manager = new ProgramRestoreManager(_logger);

        var result = await manager.CheckProgramStatusAsync([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreProgramsAsync_ShouldReturnSuccessfulEmptyResult_WhenNoProgramsProvided()
    {
        var manager = new ProgramRestoreManager(_logger);

        var result = await manager.RestoreProgramsAsync([], wingetOnly: false, dryRun: false);

        result.Success.Should().BeTrue();
        result.WingetPrograms.Should().Be(0);
        result.ManualPrograms.Should().Be(0);
        result.SuccessfulInstalls.Should().Be(0);
        result.FailedInstalls.Should().Be(0);
        result.ManualInstallRequired.Should().BeEmpty();
    }
}
