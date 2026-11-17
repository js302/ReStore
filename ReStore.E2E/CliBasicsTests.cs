using System.Text.Json;

namespace ReStore.E2E;

/// <summary>The self-describing surface: version, command reference, help for every command. None of it needs (or creates) a configuration.</summary>
[Trait("Category", "E2E")]
public class CliBasicsTests
{
    [Fact]
    public void Version_Flag_PrintsTheProductVersion()
    {
        using var ws = Workspace.Create();
        var result = ws.Run("--version");
        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.Contains("ReStore " + CliContract.ProductVersion, result.Stdout);
        Assert.Empty(Directory.GetFileSystemEntries(ws.Home));
    }

    [Fact]
    public void VersionJson_ReportsTheChannelOfTheBinaryUnderTest()
    {
        using var ws = Workspace.Create();
        var json = ws.Run("version", "--json").Ok();
        Assert.Equal(CliContract.ProductVersion, json.GetProperty("version").GetString());

        // The Windows run uses the published single-file restore.exe with the ReStore.channel marker, like the portable zip.
        if (Environment.GetEnvironmentVariable(CliBinary.OverrideVariable) == null)
        {
            Assert.Equal(OperatingSystem.IsWindows() ? "portable" : "dev", json.GetProperty("channel").GetString());
        }

        Assert.Equal(ws.Home, json.GetProperty("dataRoot").GetString());
        Assert.StartsWith(ws.Home, json.GetProperty("configFile").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("executable").GetString()));
    }

    [WindowsFact]
    public void PublishedWindowsBinary_IsThePortableChannel()
    {
        if (Environment.GetEnvironmentVariable(CliBinary.OverrideVariable) != null)
        {
            return;
        }

        using var ws = Workspace.Create();
        Assert.Equal("portable", ws.Run("version", "--json").Ok().GetProperty("channel").GetString());
        Assert.EndsWith("restore.exe", ws.Run("version", "--json").Ok().GetProperty("executable").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandsJson_IsTheDocumentedReference()
    {
        var document = CliContract.Document;
        Assert.Equal(1, document.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(CliContract.ProductVersion, document.GetProperty("version").GetString());
        Assert.NotEmpty(document.GetProperty("globalOptions").EnumerateArray());
        Assert.Equal([0, 1, 2, 3, 4, 5, 130], CliContract.ExitCodes.Select(e => e.Code).Order());

        Assert.True(CliContract.Commands.Count >= 60, "the reference should list every command, found " + CliContract.Commands.Count);
        foreach (var command in CliContract.Commands)
        {
            foreach (var property in new[] { "name", "aliases", "category", "summary", "description", "usage", "positionals", "options", "examples", "supportsJson", "mutates", "destructive", "windowsOnly", "needs" })
            {
                Assert.True(command.Element.TryGetProperty(property, out _), $"'{command.Name}' lacks '{property}'");
            }

            Assert.False(string.IsNullOrWhiteSpace(command.Element.GetProperty("summary").GetString()), command.Name);
        }
    }

    [Fact]
    public void Help_ForEveryCommand_Succeeds_InAllThreeForms()
    {
        using var ws = Workspace.Create();
        Each.InParallel(CliContract.Commands, command =>
        {
            var viaHelp = ws.Run(["help", .. command.Words]);
            Assert.True(viaHelp.ExitCode == 0 && viaHelp.Stdout.Trim().Length > 0, "help <command>\n" + viaHelp.Describe());

            var viaFlag = ws.Run([.. command.Words, "--help"]);
            Assert.True(viaFlag.ExitCode == 0 && viaFlag.Stdout.Trim().Length > 0, "<command> --help\n" + viaFlag.Describe());
            Assert.Equal(viaHelp.Stdout, viaFlag.Stdout);

            // `help <command> --json` is the same object as the command's entry in `commands --json`.
            var entry = ws.Run(["help", .. command.Words, "--json"]).Ok();
            Assert.Equal(command.Name, entry.GetProperty("name").GetString());
            Assert.Equal(JsonSerializer.Serialize(command.Element), JsonSerializer.Serialize(entry.Clone()).Replace("\"ok\":true,", string.Empty));
        });

        Assert.Empty(Directory.GetFileSystemEntries(ws.Home));
    }

    [Fact]
    public void Help_Overview_AndTheCommandList_AreNonEmptyAndNeedNoConfiguration()
    {
        using var ws = Workspace.Create();
        foreach (var args in new[] { new[] { "help" }, ["--help"], ["-h"], ["commands"], ["help", "config"], ["agent-guide"] })
        {
            var result = ws.Run(args);
            Assert.True(result.ExitCode == 0 && result.Stdout.Trim().Length > 0, result.Describe());
        }

        Assert.Empty(Directory.GetFileSystemEntries(ws.Home));
    }

    [Fact]
    public void QuietDoesNotSilenceTheAnswerToHelpVersionAndCommands()
    {
        using var ws = Workspace.Create();
        foreach (var args in new[] { new[] { "version", "--quiet" }, ["help", "--quiet"], ["commands", "--quiet"] })
        {
            var result = ws.Run(args);
            Assert.True(result.ExitCode == 0 && result.Stdout.Trim().Length > 0, result.Describe());
        }
    }
}
