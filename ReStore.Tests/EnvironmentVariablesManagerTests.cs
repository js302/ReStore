using FluentAssertions;
using ReStore.Core.src.utils;
using System.Text.Json;

namespace ReStore.Tests;

public class EnvironmentVariablesManagerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger;
    private readonly EnvironmentVariablesManager _manager;

    public EnvironmentVariablesManagerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreEnvVarsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _logger = new TestLogger();
        _manager = new EnvironmentVariablesManager(_logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task ExportEnvironmentVariablesToJsonAsync_ShouldWriteExpectedSummary()
    {
        var variables = new List<EnvironmentVariableEntry>
        {
            new() { Name = "RESTORE_TEST_USER", Value = "user-value", Target = EnvironmentVariableTarget.User },
            new() { Name = "RESTORE_TEST_MACHINE", Value = "machine-value", Target = EnvironmentVariableTarget.Machine }
        };

        var outputPath = Path.Combine(_testRoot, "envvars.json");
        var exportedPath = await _manager.ExportEnvironmentVariablesToJsonAsync(variables, outputPath);

        exportedPath.Should().Be(outputPath);
        File.Exists(outputPath).Should().BeTrue();

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var root = doc.RootElement;

        root.GetProperty("totalVariables").GetInt32().Should().Be(2);
        root.GetProperty("systemVariables").GetInt32().Should().Be(1);
        root.GetProperty("userVariables").GetInt32().Should().Be(1);
        root.GetProperty("variables").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task CreateRestoreScriptAsync_ShouldEscapeVariableValues_AndIncludeTargets()
    {
        var variables = new List<EnvironmentVariableEntry>
        {
            new() { Name = "RESTORE_TEST_USER", Value = "A'B`C", Target = EnvironmentVariableTarget.User },
            new() { Name = "RESTORE_TEST_MACHINE", Value = "M'V`1", Target = EnvironmentVariableTarget.Machine }
        };

        var scriptPath = Path.Combine(_testRoot, "restore-env.ps1");
        var createdPath = await _manager.CreateRestoreScriptAsync(variables, scriptPath);

        createdPath.Should().Be(scriptPath);

        var script = await File.ReadAllTextAsync(scriptPath);
        script.Should().Contain("Set-EnvironmentVariableSafely -Name 'RESTORE_TEST_USER'");
        script.Should().Contain("Set-EnvironmentVariableSafely -Name 'RESTORE_TEST_MACHINE'");
        script.Should().Contain("-Target User");
        script.Should().Contain("-Target Machine");
        script.Should().Contain("A''B``C");
        script.Should().Contain("M''V``1");
    }

    [Fact]
    public async Task RestoreEnvironmentVariablesAsync_ShouldRestoreUserVariableFromJson()
    {
        var variableName = "RESTORE_TEST_VAR_" + Guid.NewGuid().ToString("N");
        var expectedValue = "restored-value";

        Environment.SetEnvironmentVariable(variableName, null, EnvironmentVariableTarget.User);

        try
        {
            var payload = new
            {
                variables = new[]
                {
                    new EnvironmentVariableEntry
                    {
                        Name = variableName,
                        Value = expectedValue,
                        Target = EnvironmentVariableTarget.User
                    }
                }
            };

            var jsonPath = Path.Combine(_testRoot, "restore-input.json");
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(payload));

            await _manager.RestoreEnvironmentVariablesAsync(jsonPath);

            var restoredValue = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.User);
            restoredValue.Should().Be(expectedValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null, EnvironmentVariableTarget.User);
        }
    }

    [Fact]
    public async Task RestoreEnvironmentVariablesWithPowerShellAsync_ShouldReturnFalse_WhenScriptDoesNotExist()
    {
        var missingScriptPath = Path.Combine(_testRoot, "missing-script.ps1");

        var result = await _manager.RestoreEnvironmentVariablesWithPowerShellAsync(missingScriptPath);

        result.Should().BeFalse();
    }
}
