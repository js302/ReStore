using ReStore.Core;

namespace ReStore.Core.src.cli;

/// <summary>Help category of a command; the order is the order of the top-level help.</summary>
internal enum CommandCategory
{
    BackupRestore,
    Browse,
    Repository,
    System,
    Configuration,
    Automation,
    Info
}

/// <summary>How much of the configuration a command needs before its handler runs.</summary>
internal enum ConfigRequirement
{
    /// <summary>Nothing: the config is not even loaded (help, version, commands).</summary>
    None,

    /// <summary>Loaded (and migrated) but not validated.</summary>
    Loaded,

    /// <summary>The whole configuration must validate.</summary>
    Full,

    /// <summary>Only the storage the command uses (<c>--storage</c> or the global one) must validate.</summary>
    Storage,

    /// <summary>Only the storage named by <c>--storage</c>, when given, must validate.</summary>
    StorageOverrideOnly
}

/// <summary>Whether a command loads the system state, and whether it saves it at exit.</summary>
internal enum StateRequirement
{
    /// <summary>The state is not loaded.</summary>
    None,

    /// <summary>Loaded; saved at exit only when telemetry changed.</summary>
    ReadOnly,

    /// <summary>Loaded and always saved at exit under the repository lease.</summary>
    Mutating
}

/// <param name="ValueName">Name of the value (<c>storageType</c>), or null for a switch.</param>
internal sealed record OptionSpec(string Name, string Description, string? ValueName = null, bool Repeatable = false, string[]? AllowedValues = null)
{
    public bool TakesValue => ValueName != null;
}

internal sealed record PositionalSpec(string Name, string Description, bool Required = true, bool Repeatable = false);

/// <summary>
/// Everything the CLI knows about one command: parsing rules, help text, the <c>commands --json</c> entry and the handler.
/// To add a command from another file, build a spec in a static <c>Specs</c> list (see <c>InfoCommands</c>) and add that
/// list to <see cref="CommandRegistry"/>. Global options (<c>--json</c>, <c>--quiet</c>, <c>--yes</c>, ...) are not listed here.
/// </summary>
internal sealed record CommandSpec
{
    /// <summary>Canonical name; may be several words (<c>config show</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Other names the command answers to (<c>--validate-config</c> for <c>config validate</c>).</summary>
    public string[] Aliases { get; init; } = [];

    public required CommandCategory Category { get; init; }
    public required string Summary { get; init; }

    /// <summary>Longer notes shown by <c>help &lt;command&gt;</c>.</summary>
    public string? Description { get; init; }

    /// <summary>Usage lines without the program name. When empty they are generated from the name, positionals and options.</summary>
    public string[] Usage { get; init; } = [];

    public PositionalSpec[] Positionals { get; init; } = [];
    public OptionSpec[] Options { get; init; } = [];
    public string[] Examples { get; init; } = [];

    /// <summary>Accepts the global <c>--json</c> and then writes exactly one JSON document to stdout.</summary>
    public bool SupportsJson { get; init; } = true;

    /// <summary>Changes the repository, the configuration or this machine.</summary>
    public bool Mutates { get; init; }

    /// <summary>Deletes or overwrites data.</summary>
    public bool Destructive { get; init; }

    public bool WindowsOnly { get; init; }

    public ConfigRequirement Config { get; init; } = ConfigRequirement.Full;
    public StateRequirement State { get; init; } = StateRequirement.ReadOnly;

    /// <summary>
    /// True when the command prints its payload to the real stdout itself (<c>config show</c>): log lines are then kept on
    /// stderr even without <c>--json</c>.
    /// </summary>
    public bool PayloadOnStdout { get; init; }

    /// <summary>
    /// With <see cref="ConfigRequirement.Full"/>: false skips the "watch directories" checks, for commands that get their
    /// folder on the command line (<c>backup &lt;path&gt;</c>) and so work with an empty or stale watch list.
    /// </summary>
    public bool ValidateWatchDirectories { get; init; } = true;

    /// <summary>Never prompts or opens a browser, even in an interactive terminal (service, scheduled runs).</summary>
    public bool ForceUnattended { get; init; }

    /// <summary>Fails with exit 4 (needs_user) up front when encryption is on and no password is available from the environment.</summary>
    public bool RequiresServicePassword { get; init; }

    /// <summary>Extra checks on the parsed arguments (combinations, value formats); returns an error message or null.</summary>
    public Func<Program.ParsedCommand, string?>? Validate { get; init; }

    /// <summary>
    /// For a <see cref="StateRequirement.Mutating"/> command: true when this invocation only previews (<c>--dry-run</c>), so the state is
    /// treated as read-only and the file is left byte-identical unless telemetry changed.
    /// </summary>
    public Func<Program.ParsedCommand, bool>? IsPreview { get; init; }

    public required Func<CliContext, Task<int>> Handler { get; init; }

    public int MinPositionals => Positionals.Count(p => p.Required);

    public int MaxPositionals => Positionals.Any(p => p.Repeatable) ? int.MaxValue : Positionals.Length;

    /// <summary>First word of the name; the group (<c>config</c>) of a multi-word command, otherwise the name itself.</summary>
    public string Group => Name.Contains(' ', StringComparison.Ordinal) ? Name[..Name.IndexOf(' ', StringComparison.Ordinal)] : Name;

    public bool IsSubcommand => Name.Contains(' ', StringComparison.Ordinal);

    /// <summary>The name and the positionals, for example <c>backup &lt;sourceDir&gt;</c>.</summary>
    public string Synopsis => string.Join(' ', new[] { Name }.Concat(Positionals.Select(FormatPositional)));

    public IReadOnlyList<string> UsageLines
    {
        get
        {
            if (Usage.Length > 0)
            {
                return Usage;
            }

            var optionText = Options.Select(o => o.TakesValue ? $"[{o.Name} <{o.ValueName}>]" : $"[{o.Name}]");
            return [string.Join(' ', new[] { Synopsis }.Concat(optionText))];
        }
    }

    private static string FormatPositional(PositionalSpec positional)
    {
        var name = positional.Repeatable ? $"{positional.Name}..." : positional.Name;
        return positional.Required ? $"<{name}>" : $"[{name}]";
    }
}
