using System.Reflection;
using System.Text;
using ReStore.Core;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary>
/// The single source of truth for the CLI: every command's spec lives in a list, and parsing, help, <c>commands --json</c> and
/// dispatch are all driven from <see cref="All"/>. To add a command, write its spec (and handler) in a new
/// <c>src/cli/&lt;Feature&gt;Commands.cs</c> exposing a static <c>Specs</c> list and add one line to <see cref="Sources"/>.
/// </summary>
internal static class CommandRegistry
{
    public const string ProgramName = "restore.exe";

    private static readonly CommandCategory[] CategoryOrder = Enum.GetValues<CommandCategory>();

    // One line per source file of commands.
    private static IEnumerable<IEnumerable<CommandSpec>> Sources() =>
    [
        Program.BuiltInSpecs(),
        InfoCommands.Specs,
        DoctorCommands.Specs,
        AgentGuideCommands.Specs,
        TelemetryCommands.Specs,
        UpdateCommands.Specs,
        Program.OpsSpecs(),
        ScheduleCommands.Specs,
        ConfigCommands.Specs,
        EncryptionCommands.Specs,
        SetupCommands.Specs,
        AuthCommands.Specs,
    ];

    private static readonly Lazy<IReadOnlyList<CommandSpec>> AllSpecs = new(() =>
    {
        var specs = Sources().SelectMany(source => source).ToList();
        var duplicate = specs.SelectMany(s => new[] { s.Name }.Concat(s.Aliases)).GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        return duplicate != null
            ? throw new InvalidOperationException($"Command name '{duplicate.Key}' is registered twice.")
            : specs;
    });

    public static IReadOnlyList<CommandSpec> All => AllSpecs.Value;

    public static string CategoryTitle(CommandCategory category) => category switch
    {
        CommandCategory.BackupRestore => "Backup & restore",
        CommandCategory.Browse => "Browse",
        CommandCategory.Repository => "Repository & keys",
        CommandCategory.System => "System",
        CommandCategory.Configuration => "Configuration",
        CommandCategory.Automation => "Automation",
        _ => "Info"
    };

    /// <summary>Finds a command by its full name or alias (<c>config show</c>, <c>--validate-config</c>).</summary>
    public static CommandSpec? Find(string name) =>
        All.FirstOrDefault(s => s.Name == name || s.Aliases.Contains(name, StringComparer.Ordinal));

    /// <summary>True for <c>config</c>: a word that only exists as the first word of multi-word commands.</summary>
    public static bool IsGroup(string word) => All.Any(s => s.IsSubcommand && s.Group == word) && Find(word) == null;

    public static IReadOnlyList<CommandSpec> Subcommands(string group) => [.. All.Where(s => s.IsSubcommand && s.Group == group)];

    /// <summary>Every word a user may type first: command names, aliases and group names.</summary>
    public static IEnumerable<string> TopLevelWords() =>
        All.SelectMany(s => s.IsSubcommand ? [s.Group] : new[] { s.Name }.Concat(s.Aliases)).Distinct(StringComparer.Ordinal);

    public static IEnumerable<string> SubcommandWords(string group) =>
        Subcommands(group).Select(s => s.Name[(group.Length + 1)..]);

    public static string Version => AboutInfo.VersionOf(typeof(Program).Assembly);

    public static string FullVersion =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>The exit codes with their JSON <c>error.code</c> names, for help, docs and <c>commands --json</c>.</summary>
    public static readonly (int Code, string Name, string Meaning)[] ExitCodeTable =
    [
        (Program.ExitCodes.Success, "success", "The command succeeded (also help, version and commands)."),
        (Program.ExitCodes.Failure, "failure", "The operation failed (failed verification or restore, backup error, ...). error.code is not_found when the snapshot or file the command was pointed at is not in the storage."),
        (Program.ExitCodes.Usage, "usage", "Usage error: unknown command or option, missing value or argument, invalid value."),
        (Program.ExitCodes.Config, "config", "Configuration error: config.json cannot be loaded or fails validation."),
        (Program.ExitCodes.NeedsUser, "needs_user", "A person must act: no password available without a prompt, interactive sign-in or elevation required, --yes required."),
        (Program.ExitCodes.Busy, "busy", "Another ReStore process holds the repository (retry, or raise --wait)."),
        (Program.ExitCodes.Cancelled, "cancelled", "Cancelled with Ctrl+C.")
    ];

    private static readonly string[] GeneralNotes =
    [
        "Option names are case-insensitive and may appear before or after the positional arguments. Unknown options are rejected, never ignored.",
        "Storage types and per-folder storage are configured in config.json; --storage overrides the configured storage for one command.",
        "Ctrl+C cancels an in-flight backup, restore or verify.",
        "Encrypted repositories need the password without a prompt in scripts and agents: --password-stdin, RESTORE_ENCRYPTION_PASSWORD, or RESTORE_ENCRYPTION_PASSWORD_FILE.",
        "Set up and configure without the app: init (one-command first-time setup), config get/set/unset/add-watch/set-storage/schema, encryption enable, unlock --remember, auth gdrive/dropbox.",
        "Something wrong? restore.exe doctor --json lists every problem with a fix. AI agents: restore.exe agent-guide prints the rules and recipes.",
        "Machine-readable reference of all of this: restore.exe commands --json"
    ];

    // ---- Help text -------------------------------------------------------------------------------------------------

    /// <summary>The top-level help: every command by category, global options, exit codes, notes.</summary>
    public static string RenderTopLevelUsage()
    {
        var text = new StringBuilder();
        text.AppendLine($"ReStore {Version}: back up and restore folders and system settings.");
        text.AppendLine();
        text.AppendLine("Usage:");
        text.AppendLine($"  {ProgramName} <command> [arguments] [options]");
        text.AppendLine($"  {ProgramName} help <command>        Help for one command (also: {ProgramName} <command> --help)");
        text.AppendLine($"  {ProgramName} commands --json       The whole command reference as JSON, for tools and agents");
        text.AppendLine($"  {ProgramName} doctor                 Health check: what is wrong with this setup, and the command that fixes it");
        text.AppendLine($"  {ProgramName} agent-guide            The guide for AI agents operating ReStore (rules, exit codes, recipes)");
        text.AppendLine();
        AppendCommandTable(text);
        text.AppendLine();
        AppendGlobalOptions(text);
        text.AppendLine();
        AppendExitCodes(text);
        text.AppendLine();
        text.AppendLine("Notes:");
        foreach (var note in GeneralNotes)
        {
            text.AppendLine($"  - {note}");
        }

        text.AppendLine();
        text.AppendLine("Examples:");
        foreach (var example in new[]
        {
            "backup %USERPROFILE%\\Documents",
            "restore snapshots/documents_abc123/HEAD C:\\Restored --dry-run",
            "verify snapshots/documents_abc123/HEAD --mode quick --json",
            "status --json",
            "help restore"
        })
        {
            text.AppendLine($"  {ProgramName} {example}");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The command list grouped by category: synopsis and summary.</summary>
    public static void AppendCommandTable(StringBuilder text)
    {
        var width = All.Max(s => s.Synopsis.Length) + 2;
        foreach (var category in CategoryOrder)
        {
            var specs = All.Where(s => s.Category == category).ToList();
            if (specs.Count == 0)
            {
                continue;
            }

            text.AppendLine($"{CategoryTitle(category)}:");
            foreach (var spec in specs)
            {
                text.AppendLine($"  {spec.Synopsis.PadRight(width)}{spec.Summary}");
            }

            text.AppendLine();
        }

        text.Length -= Environment.NewLine.Length;
    }

    public static void AppendGlobalOptions(StringBuilder text)
    {
        text.AppendLine("Global options (any command, before or after it):");
        foreach (var option in CliGlobals.Documentation)
        {
            var name = option.ValueName == null ? option.Name : $"{option.Name} <{option.ValueName}>";
            text.AppendLine($"  {name.PadRight(22)}{option.Description}");
        }
    }

    public static void AppendExitCodes(StringBuilder text)
    {
        text.AppendLine("Exit codes:");
        foreach (var (code, name, meaning) in ExitCodeTable)
        {
            text.AppendLine($"  {code,-4}{name,-11}{meaning}");
        }
    }

    /// <summary>The usage lines of a command, shown after a usage error.</summary>
    public static string RenderUsageError(CommandSpec spec)
    {
        var text = new StringBuilder();
        text.AppendLine("Usage:");
        foreach (var line in spec.UsageLines)
        {
            text.AppendLine($"  {ProgramName} {line}");
        }

        text.Append($"Run '{ProgramName} help {spec.Name}' for details.");
        return text.ToString();
    }

    /// <summary>The subcommands of a group, shown after a usage error or for <c>help config</c>.</summary>
    public static string RenderGroupHelp(string group)
    {
        var text = new StringBuilder();
        text.AppendLine("Usage:");
        text.AppendLine($"  {ProgramName} {group} <subcommand> [arguments] [options]");
        text.AppendLine();
        text.AppendLine($"Subcommands of '{group}':");
        var subcommands = Subcommands(group);
        var width = subcommands.Max(s => s.Synopsis.Length) + 2;
        foreach (var spec in subcommands)
        {
            text.AppendLine($"  {spec.Synopsis.PadRight(width)}{spec.Summary}");
        }

        text.Append($"Run '{ProgramName} help {group} <subcommand>' for details.");
        return text.ToString();
    }

    /// <summary>The full help of one command.</summary>
    public static string RenderCommandHelp(CommandSpec spec)
    {
        var text = new StringBuilder();
        text.AppendLine("Usage:");
        foreach (var line in spec.UsageLines)
        {
            text.AppendLine($"  {ProgramName} {line}");
        }

        text.AppendLine();
        text.AppendLine(spec.Summary);
        if (!string.IsNullOrWhiteSpace(spec.Description))
        {
            text.AppendLine();
            text.AppendLine(spec.Description.TrimEnd());
        }

        if (spec.Positionals.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("Arguments:");
            var width = spec.Positionals.Max(p => p.Name.Length) + 4;
            foreach (var positional in spec.Positionals)
            {
                text.AppendLine($"  {positional.Name.PadRight(width)}{positional.Description}{(positional.Required ? string.Empty : " (optional)")}");
            }
        }

        if (spec.Options.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("Options:");
            var labels = spec.Options.Select(o => o.TakesValue ? $"{o.Name} <{o.ValueName}>" : o.Name).ToList();
            var width = labels.Max(l => l.Length) + 2;
            for (var index = 0; index < spec.Options.Length; index++)
            {
                var option = spec.Options[index];
                var extra = option.AllowedValues is { Length: > 0 } allowed ? $" One of: {string.Join(", ", allowed)}." : string.Empty;
                text.AppendLine($"  {labels[index].PadRight(width)}{option.Description}{extra}{(option.Repeatable ? " May be repeated." : string.Empty)}");
            }
        }

        text.AppendLine();
        text.AppendLine("Properties:");
        text.AppendLine($"  --json            {(spec.SupportsJson ? "supported (one JSON document on stdout)" : "not supported")}");
        text.AppendLine($"  changes data      {(spec.Mutates ? "yes" : "no")}{(spec.Destructive ? " (destructive: deletes or overwrites data)" : string.Empty)}");
        if (spec.WindowsOnly)
        {
            text.AppendLine("  platform          Windows only");
        }

        text.AppendLine($"  global options    see '{ProgramName} help' ({string.Join(", ", CliGlobals.Documentation.Select(o => o.Name))})");

        if (spec.Examples.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("Examples:");
            foreach (var example in spec.Examples)
            {
                text.AppendLine($"  {ProgramName} {example}");
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Help for the words typed after <c>help</c>; null when they name nothing.</summary>
    public static string? RenderHelpFor(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return RenderTopLevelUsage();
        }

        var name = string.Join(' ', words);
        if (Find(name) is { } spec)
        {
            return RenderCommandHelp(spec);
        }

        return words.Count == 1 && IsGroup(words[0]) ? RenderGroupHelp(words[0]) : null;
    }

    /// <summary>The plain-text <c>commands</c> output.</summary>
    public static string RenderCommandsText()
    {
        var text = new StringBuilder();
        AppendCommandTable(text);
        text.AppendLine();
        text.AppendLine();
        text.Append($"Run '{ProgramName} help <command>' for details, or '{ProgramName} commands --json' for the machine-readable reference.");
        return text.ToString();
    }

    // ---- commands --json --------------------------------------------------------------------------------------------

    /// <summary>The document of <c>commands --json</c>.</summary>
    public static object BuildCommandsDocument() => new
    {
        schemaVersion = 1,
        version = Version,
        globalOptions = CliGlobals.Documentation.Select(o => new
        {
            name = o.Name,
            takesValue = o.ValueName != null,
            valueName = o.ValueName,
            description = o.Description
        }),
        exitCodes = ExitCodeTable.Select(e => new { code = e.Code, name = e.Name, meaning = e.Meaning }),
        commands = All.Select(BuildCommandEntry)
    };

    /// <summary>One command as in <c>commands --json</c>; <c>&lt;command&gt; --help --json</c> prints the same object.</summary>
    public static object BuildCommandEntry(CommandSpec s) => new
    {
        name = s.Name,
        aliases = s.Aliases,
        category = CategoryTitle(s.Category),
        summary = s.Summary,
        description = s.Description,
        usage = s.UsageLines.Select(line => $"{ProgramName} {line}"),
        positionals = s.Positionals.Select(p => new { name = p.Name, required = p.Required, repeatable = p.Repeatable, description = p.Description }),
        options = s.Options.Select(o => new
        {
            name = o.Name,
            takesValue = o.TakesValue,
            valueName = o.ValueName,
            repeatable = o.Repeatable,
            allowedValues = o.AllowedValues,
            description = o.Description
        }),
        examples = s.Examples.Select(e => $"{ProgramName} {e}"),
        supportsJson = s.SupportsJson,
        mutates = s.Mutates,
        destructive = s.Destructive,
        windowsOnly = s.WindowsOnly,
        needs = new
        {
            config = NeedsConfigName(s.Config),
            systemState = s.State != StateRequirement.None
        }
    };

    private static string NeedsConfigName(ConfigRequirement requirement) => requirement switch
    {
        ConfigRequirement.None => "none",
        ConfigRequirement.Loaded => "loaded",
        ConfigRequirement.Full => "full-validation",
        _ => "storage-validation"
    };
}
