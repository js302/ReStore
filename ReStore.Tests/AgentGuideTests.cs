using System.Text.RegularExpressions;
using FluentAssertions;
using System.Text.Json.Nodes;
using ReStore.Core;
using ReStore.Core.src.cli;
using ReStore.Core.src.core;
using ReStore.Core.src.updates;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary><c>restore agent-guide</c> and the documentation that must not drift from the command registry.</summary>
public partial class AgentGuideTests : CliConfigTestBase
{
    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReStore.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("ReStore.sln not found above " + AppContext.BaseDirectory);
    }

    private static string ReadRepositoryFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot, .. parts])).ReplaceLineEndings("\n");

    [GeneratedRegex(@"(?<![\w/\\.\-])restore(?:\.exe)?[ \t]+([a-z][a-z-]*)(?:[ \t]+([a-z][a-z-]*))?(?![\w/\\.])")]
    private static partial Regex CommandMention();

    /// <summary>Text of fenced blocks and inline code spans, where commands are written.</summary>
    private static IEnumerable<string> CodeText(string markdown)
    {
        foreach (Match block in Regex.Matches(markdown, @"```.*?\n(.*?)```", RegexOptions.Singleline))
        {
            foreach (var line in block.Groups[1].Value.Split('\n'))
            {
                yield return line;
            }
        }

        var withoutBlocks = Regex.Replace(markdown, @"```.*?```", string.Empty, RegexOptions.Singleline);
        foreach (Match span in Regex.Matches(withoutBlocks, @"`([^`\n]+)`"))
        {
            yield return span.Groups[1].Value;
        }
    }

    /// <summary>The command a mention names: two words when that is a registered command (<c>config show</c>), else one.</summary>
    private static string? Resolve(Match mention)
    {
        var first = mention.Groups[1].Value;
        var second = mention.Groups[2].Success ? mention.Groups[2].Value : null;
        if (second != null && CommandRegistry.Find($"{first} {second}") != null)
        {
            return $"{first} {second}";
        }

        if (CommandRegistry.IsGroup(first) && second != null)
        {
            return $"{first} {second}";
        }

        return first;
    }

    private static bool Exists(string name) => CommandRegistry.Find(name) != null;

    // ---- the command ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AgentGuide_PrintsTheEmbeddedGuide()
    {
        var (exitCode, stdout, stderr) = await RunAsync("agent-guide");

        exitCode.Should().Be(0, stderr);
        stdout.ReplaceLineEndings("\n").TrimEnd().Should().Be(ReStore.Core.src.cli.AgentGuideCommands.StripTestMarkers(ReadRepositoryFile("docs", "agents.md").ReplaceLineEndings("\n")).TrimEnd());
        stdout.Should().Contain("# ReStore guide for AI agents");
    }

    [Fact]
    public async Task AgentGuide_Json_HasTheVersionAndTheText()
    {
        var (exitCode, stdout, _) = await RunAsync("agent-guide", "--json");

        exitCode.Should().Be(0);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["version"]!.GetValue<string>().Should().Be(CommandRegistry.Version);
        doc["guide"]!.GetValue<string>().ReplaceLineEndings("\n").TrimEnd().Should().Be(ReStore.Core.src.cli.AgentGuideCommands.StripTestMarkers(ReadRepositoryFile("docs", "agents.md").ReplaceLineEndings("\n")).TrimEnd());
    }

    [Fact]
    public void AgentGuide_NeedsNoConfiguration_AndTopLevelHelpMentionsItAndDoctor()
    {
        CommandRegistry.Find("agent-guide")!.Config.Should().Be(ConfigRequirement.None);
        var help = CommandRegistry.RenderTopLevelUsage();
        help.Should().Contain("restore.exe doctor").And.Contain("restore.exe agent-guide");
        File.Exists(AppPaths_ConfigFile()).Should().BeFalse("agent-guide must not create the data folder's config");
    }

    private static string AppPaths_ConfigFile() => ReStore.Core.src.utils.AppPaths.ConfigFile;

    // ---- no drift ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Guide_EveryMentionedCommand_Exists()
    {
        var guide = ReadRepositoryFile("docs", "agents.md");
        var unknown = CodeText(guide)
            .SelectMany(text => CommandMention().Matches(text).Select(Resolve))
            .Where(name => name != null && !Exists(name))
            .Distinct()
            .ToList();

        unknown.Should().BeEmpty("docs/agents.md mentions commands the CLI does not have");
        CodeText(guide).SelectMany(text => CommandMention().Matches(text)).Count().Should().BeGreaterThan(30, "the matcher must actually find the commands");
    }

    /// <summary>The commands rule 4 lists as needing <c>--yes</c>: the bullets under the line that says so.</summary>
    private static List<string> CommandsRequiringYes()
    {
        var lines = ReadRepositoryFile("docs", "agents.md").Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains("require `--yes`", StringComparison.Ordinal));
        start.Should().BeGreaterThanOrEqualTo(0, "rule 4 must name the commands that require --yes");
        return [.. lines.Skip(start + 1)
            .SkipWhile(l => !l.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            .TakeWhile(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            .Select(l => Regex.Match(l, "`([^`]+)`").Groups[1].Value)];
    }

    [Fact]
    public void Guide_Rule4_NamesTheYesCommandsAndTheOnesThatDoNotAsk()
    {
        var guide = ReadRepositoryFile("docs", "agents.md");

        CommandsRequiringYes().Should().BeEquivalentTo(
            ["retention apply", "repair", "restore --conflict overwrite", "system-restore", "update install", "encryption disable"]);
        guide.Should().Contain("`schedule remove`, `system-undo` and `config import` do **not** ask");
        // The old, wrong claim must not come back: these three used to run at once.
        guide.Should().NotContain("`system-restore` (without `--plan`) start working the moment you run them");
    }

    [Fact]
    public async Task Guide_EveryCommandThatRequiresYes_ReallyExitsFourWithoutIt()
    {
        var scenarios = new Dictionary<string, Func<Task<(int ExitCode, string Stdout, string Stderr)>>>
        {
            ["retention apply"] = async () =>
            {
                WriteConfig();
                for (var revision = 1; revision <= 3; revision++)
                {
                    File.WriteAllText(Path.Combine(WatchedDirectory, "doc.txt"), $"version {revision} " + new string('x', 400 + revision));
                    (await RunAsync("backup", WatchedDirectory)).ExitCode.Should().Be(0);
                    await Task.Delay(30);
                }

                // Retention is switched on only now: a backup applies it itself, so there would be nothing left to delete.
                var config = ReadConfig();
                config["retention"] = new JsonObject { ["enabled"] = true, ["keepLast"] = 1, ["keepWithinDays"] = 0 };
                File.WriteAllText(AppPaths.ConfigFile, config.ToJsonString());
                return await RunAsync("retention", "apply", "--json");
            },
            ["repair"] = async () =>
            {
                WriteConfig();
                (await RunAsync("backup", WatchedDirectory)).ExitCode.Should().Be(0);
                return await RunAsync("repair", "--json");
            },
            ["restore --conflict overwrite"] = async () =>
            {
                WriteConfig();
                (await RunAsync("backup", WatchedDirectory)).ExitCode.Should().Be(0);
                var head = Json((await RunAsync("snapshots", "--json")).Stdout)["groups"]!.AsArray()
                    .SelectMany(g => g!["snapshots"]!.AsArray()).Select(snapshot => snapshot!["manifestPath"]!.GetValue<string>()).First();
                return await RunAsync("restore", head, Path.Combine(Root, "restored"), "--conflict", "overwrite", "--json");
            },
            ["system-restore"] = async () =>
            {
                WriteConfig();
                if (OperatingSystem.IsWindows())
                {
                    return await RunAsync("system-restore", "system_backups/environment/none.zip", "environment", "--json");
                }

                // Elsewhere the command stops at "Windows only" before it asks, so exercise its guard directly (it runs before any download).
                CliRuntime.Reset();
                CliRuntime.NonInteractive = true;
                try
                {
                    Program.RequireSystemRestoreConfirmationWithoutTerminal();
                    return (0, string.Empty, "the guard did not ask");
                }
                catch (CliCommandException ex)
                {
                    return (ex.ExitCode, $"{{\"error\":{{\"code\":\"{ex.Code}\"}}}}", string.Empty);
                }
                finally
                {
                    CliRuntime.Reset();
                }
            },
            ["encryption disable"] = async () =>
            {
                WriteConfig();
                (await RunWithStdinAsync("a long enough password 1\n", "encryption", "enable", "--password-stdin")).ExitCode.Should().Be(0);
                return await RunAsync("encryption", "disable", "--json");
            },
            ["update install"] = async () =>
            {
                WriteConfig();
                var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(UpdateCheckTests.ManifestJson("99.0.0"))));
                var install = Path.Combine(Root, "install");
                Directory.CreateDirectory(install);
                File.WriteAllText(Path.Combine(install, "restore.exe"), "old exe");
                UpdateCommands.CreateService = () => new UpdateService(
                    new UpdateChecker(handler, "1.7.0", AppChannel.Portable, "x64", null, _ => null), new UpdateCache(), null, _ => null, disableUnderTestOverride: false, "1.7.0");
                UpdateCommands.CreateInstaller = () => new UpdateInstaller(
                    handler, new FakeMsixLauncher(), AppChannel.Portable, "x64", Path.Combine(Root, "updates"), install, Root,
                    new RepositoryCoordinator(Path.Combine(Root, "repo.lock")));
                try
                {
                    return await RunAsync("update", "install", "--json");
                }
                finally
                {
                    UpdateCommands.CreateService = () => new UpdateService();
                    UpdateCommands.CreateInstaller = () => new UpdateInstaller();
                }
            }
        };

        var listed = CommandsRequiringYes();
        listed.Should().BeEquivalentTo(scenarios.Keys, "every command the guide lists needs a scenario here, and the other way round");
        foreach (var command in listed)
        {
            var (exitCode, stdout, stderr) = await scenarios[command]();

            exitCode.Should().Be(4, $"'{command}' must exit 4 without --yes: {stdout}{stderr}");
            Json(stdout)["error"]!["code"]!.GetValue<string>().Should().Be("needs_user", command);
        }
    }

    [Fact]
    public void Guide_Scheduling_UsesTheRealScheduleSyntax()
    {
        var guide = ReadRepositoryFile("docs", "agents.md");

        guide.Should().Contain("restore schedule preview \"daily 02:00\"").And.Contain("restore config set-schedule <dir> \"daily 02:00\"")
            .And.Contain("restore schedule next").And.Contain("restore config set-window 22:00 06:00");
        CommandRegistry.Find("config set-schedule")!.Positionals.Select(p => p.Name).Should().Equal("target", "expression");
        CommandRegistry.Find("schedule preview")!.Options.Should().Contain(o => o.Name == "--count");
        CommandRegistry.Find("config set-window")!.Positionals.Should().HaveCount(2);
    }

    [Fact]
    public void Guide_StaysCompact_AndCarriesTheRules()
    {
        var guide = ReadRepositoryFile("docs", "agents.md");

        guide.Split('\n').Length.Should().BeLessThan(400);
        guide.Should().Contain("irm https://github.com/js302/ReStore/releases/latest/download/install.ps1 | iex")
            .And.Contain("restore doctor --json").And.Contain("restore commands --json")
            .And.Contain("--non-interactive").And.Contain("--password-stdin").And.Contain("--value-stdin")
            .And.Contain("--wait").And.Contain("--yes").And.Contain("restore schedule install")
            .And.Contain("Never send crash reports without asking");
    }

    [Fact]
    public void CliDoc_MentionsEveryRegisteredCommand()
    {
        var cliDoc = ReadRepositoryFile("docs", "usage", "cli.md");
        var missing = CommandRegistry.All
            .Select(spec => spec.Name)
            .Where(name => !MentionsCommand(cliDoc, name))
            .ToList();

        missing.Should().BeEmpty("docs/usage/cli.md must describe every registered command");
    }

    private static bool MentionsCommand(string doc, string name)
    {
        if (name.StartsWith("--", StringComparison.Ordinal) || name.Contains(' ', StringComparison.Ordinal) == false)
        {
            return Regex.IsMatch(doc, @"(?<![\w-])" + Regex.Escape(name) + @"(?![\w-])");
        }

        // 'config set-storage' counts when written in full, or as 'config ... set-storage' in a list of a group's subcommands.
        var parts = name.Split(' ', 2);
        return doc.Contains(name, StringComparison.Ordinal)
            || Regex.IsMatch(doc, @"(?<![\w-])" + Regex.Escape(parts[0]) + @"[^\n]{0,200}?(?<![\w-])" + Regex.Escape(parts[1]) + @"(?![\w-])");
    }

    [Fact]
    public void LlmsTxt_FollowsTheFormat_AndLinksTheDocs()
    {
        var llms = ReadRepositoryFile("llms.txt");

        llms.Should().StartWith("# ReStore\n").And.Contain("\n> ");
        foreach (var page in new[] { "docs/agents.md", "docs/usage/cli.md", "docs/configuration.md", "docs/telemetry.md", "docs/updates.md", "docs/installation.md" })
        {
            llms.Should().Contain("/blob/main/" + page);
            File.Exists(Path.Combine(RepositoryRoot, page)).Should().BeTrue(page);
        }

        Regex.Matches(llms, @"^## ", RegexOptions.Multiline).Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ReadmeAndInstallationDocs_MentionTheAgentEntryPoints()
    {
        var readme = ReadRepositoryFile("README.md");
        readme.Should().Contain("Use ReStore from an AI agent").And.Contain("install.ps1").And.Contain("restore agent-guide");

        var installation = ReadRepositoryFile("docs", "installation.md");
        installation.Should().Contain("Portable CLI (no install, no admin)")
            .And.Contain("-Version").And.Contain("-InstallDir").And.Contain("-NoPathUpdate").And.Contain("-Uninstall")
            .And.Contain("RESTORE_INSTALL_VERSION").And.Contain("RESTORE_INSTALL_DIR").And.Contain("RESTORE_INSTALL_NO_PATH");
    }

    [Fact]
    public void InstallScript_StillSupportsTheDocumentedOptions()
    {
        var script = ReadRepositoryFile("install.ps1");
        script.Should().Contain("[string]$Version").And.Contain("[string]$InstallDir").And.Contain("[switch]$NoPathUpdate").And.Contain("[switch]$Uninstall");
    }

    [Fact]
    public void PrintedGuide_HasNoDocTestMarkers_ButTheSourceDoesKeepThem()
    {
        var printed = ReStore.Core.src.cli.AgentGuideCommands.ReadGuide();
        printed.Should().NotBeNull();
        printed!.Should().NotContain("<!-- e2e");
        ReStore.Core.src.cli.AgentGuideCommands.StripTestMarkers("a\n<!-- e2e -->\n```\nrestore --version\n```\n").Should().Be("a\n```\nrestore --version\n```\n");
    }
}
