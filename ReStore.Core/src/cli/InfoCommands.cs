using System.Runtime.InteropServices;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary><c>help</c>, <c>version</c> and <c>commands</c>: they need no configuration and never touch the data folder.</summary>
internal static class InfoCommands
{
    public static IEnumerable<CommandSpec> Specs { get; } =
    [
        new CommandSpec
        {
            Name = "help",
            Category = CommandCategory.Info,
            Summary = "Show help for a command, or the list of commands.",
            Description = "'help' alone is the same as --help. Multi-word commands work: help config import. With --json the help of one command is the same object as its entry in 'commands --json'; help alone gives the whole command reference.",
            Positionals = [new PositionalSpec("command", "Command name (one or two words).", Required: false, Repeatable: true)],
            Examples = ["help", "help restore", "help config import"],
            SupportsJson = true,
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            Handler = HelpAsync
        },
        new CommandSpec
        {
            Name = "version",
            Aliases = ["--version"],
            Category = CommandCategory.Info,
            Summary = "Show the version, install channel, platform and data locations.",
            Description = "Works without a valid configuration. Fields: version, informationalVersion, channel (msix, portable or dev), architecture, os, runtime, dataRoot, configFile, executable.",
            Examples = ["version", "--version", "version --json"],
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            Handler = VersionAsync
        },
        new CommandSpec
        {
            Name = "commands",
            Category = CommandCategory.Info,
            Summary = "List every command; with --json, the complete machine-readable command reference.",
            Description = "The JSON document has schemaVersion, version, globalOptions, exitCodes and commands (name, usage, positionals, options, examples, supportsJson, mutates, destructive, windowsOnly, needs). Meant for scripts and AI agents that must discover the CLI.",
            Examples = ["commands", "commands --json"],
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            Handler = CommandsAsync
        },
    ];

    private static async Task<int> HelpAsync(CliContext context)
    {
        var words = context.Parsed.Positionals;
        var text = CommandRegistry.RenderHelpFor(words);
        if (text != null && context.Json)
        {
            var name = string.Join(' ', words);
            object document = CommandRegistry.Find(name) is { } spec
                ? CommandRegistry.BuildCommandEntry(spec)
                : words.Count == 1 && CommandRegistry.IsGroup(words[0])
                    ? new { group = words[0], commands = CommandRegistry.All.Where(s => s.Name.StartsWith(words[0] + " ", StringComparison.Ordinal)).Select(CommandRegistry.BuildCommandEntry) }
                    : CommandRegistry.BuildCommandsDocument();
            await context.WriteJsonAsync(document).ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        if (text == null)
        {
            var name = string.Join(' ', context.Parsed.Positionals);
            Console.Error.WriteLine($"Error: Unknown command '{name}'.");
            await Console.Out.WriteLineAsync(CommandRegistry.RenderTopLevelUsage()).ConfigureAwait(false);
            return Program.ExitCodes.Usage;
        }

        await Console.Out.WriteLineAsync(text).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> CommandsAsync(CliContext context)
    {
        if (context.Json)
        {
            await context.WriteJsonAsync(CommandRegistry.BuildCommandsDocument()).ConfigureAwait(false);
        }
        else
        {
            await Console.Out.WriteLineAsync(CommandRegistry.RenderCommandsText()).ConfigureAwait(false);
        }

        return Program.ExitCodes.Success;
    }

    internal static VersionInfo GetVersionInfo() => new(
        CommandRegistry.Version,
        CommandRegistry.FullVersion,
        AppChannel.Current,
        AppChannel.Architecture,
        RuntimeInformation.OSDescription,
        RuntimeInformation.FrameworkDescription,
        AppPaths.Root,
        AppPaths.ConfigFile,
        Environment.ProcessPath ?? string.Empty);

    internal sealed record VersionInfo(
        string Version, string InformationalVersion, string Channel, string Architecture, string Os, string Runtime,
        string DataRoot, string ConfigFile, string Executable);

    private static async Task<int> VersionAsync(CliContext context)
    {
        var info = GetVersionInfo();
        if (context.Json)
        {
            await context.WriteJsonAsync(new
            {
                version = info.Version,
                informationalVersion = info.InformationalVersion,
                channel = info.Channel,
                architecture = info.Architecture,
                os = info.Os,
                runtime = info.Runtime,
                dataRoot = info.DataRoot,
                configFile = info.ConfigFile,
                executable = info.Executable
            }).ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        var text = $"""
            ReStore {info.Version}
              Informational version: {info.InformationalVersion}
              Channel:               {info.Channel}
              Architecture:          {info.Architecture}
              OS:                    {info.Os}
              Runtime:               {info.Runtime}
              Data folder:           {info.DataRoot}
              Config file:           {info.ConfigFile}
              Executable:            {info.Executable}
            """;
        await Console.Out.WriteLineAsync(text).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }
}
