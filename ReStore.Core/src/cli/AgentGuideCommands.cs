using System.Reflection;

namespace ReStore.Core.src.cli;

/// <summary><c>agent-guide</c>: prints docs/agents.md as embedded in this binary, so the guide always matches the version in use.</summary>
internal static class AgentGuideCommands
{
    public const string ResourceName = "agents.md";

    public static IEnumerable<CommandSpec> Specs { get; } =
    [
        new CommandSpec
        {
            Name = "agent-guide",
            Category = CommandCategory.Info,
            Summary = "Print the guide for AI agents operating ReStore (rules, exit codes, recipes); matches this version.",
            Description = "Prints docs/agents.md as markdown, embedded in this binary. With --json the document has 'version' and 'guide' (the markdown text). Needs no configuration.",
            Examples = ["agent-guide", "agent-guide --json"],
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = GuideAsync
        }
    ];

    /// <summary>The embedded guide, or null when the resource is missing.</summary>
    public static string? ReadGuide()
    {
        using var stream = typeof(AgentGuideCommands).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return StripTestMarkers(reader.ReadToEnd());
    }

    /// <summary>Drops the <c>&lt;!-- e2e --&gt;</c> lines that mark runnable recipes for the doc tests; agents don't need them.</summary>
    internal static string StripTestMarkers(string guide) =>
        System.Text.RegularExpressions.Regex.Replace(guide, @"^[ \t]*<!--\s*e2e\b[^>]*-->[ \t]*\r?\n", "", System.Text.RegularExpressions.RegexOptions.Multiline);

    private static async Task<int> GuideAsync(CliContext ctx)
    {
        var guide = ReadGuide() ?? throw new CliCommandException(Program.ExitCodes.Failure, CliErrorCodes.Failure, "The agent guide is not embedded in this build.");
        await CliSupport.EmitAsync(ctx, new { version = CommandRegistry.Version, guide }, guide.TrimEnd()).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }
}
