using System.Text.Json;

namespace ReStore.E2E;

/// <summary>The command reference of the CLI under test (<c>commands --json</c>), fetched once per run.</summary>
internal static class CliContract
{
    private static readonly Lazy<JsonElement> Reference = new(() =>
    {
        using var workspace = Workspace.Create("contract");
        return workspace.Run("commands", "--json").Ok();
    });

    public static JsonElement Document => Reference.Value;

    public sealed record Command(string Name, bool SupportsJson, bool Mutates, bool Destructive, bool WindowsOnly, string Config, JsonElement Element)
    {
        public string[] Words => Name.Split(' ');

        public override string ToString() => Name;
    }

    public static IReadOnlyList<Command> Commands { get; } = [.. Document.GetProperty("commands").EnumerateArray().Select(c => new Command(
        c.GetProperty("name").GetString()!,
        c.GetProperty("supportsJson").GetBoolean(),
        c.GetProperty("mutates").GetBoolean(),
        c.GetProperty("destructive").GetBoolean(),
        c.GetProperty("windowsOnly").GetBoolean(),
        c.GetProperty("needs").GetProperty("config").GetString()!,
        c))];

    public static IReadOnlyList<(int Code, string Name)> ExitCodes { get; } = [.. Document.GetProperty("exitCodes").EnumerateArray()
        .Select(e => (e.GetProperty("code").GetInt32(), e.GetProperty("name").GetString()!))];

    public static string ProductVersion
    {
        get
        {
            var props = File.ReadAllText(Path.Combine(CliBinary.RepositoryRoot, "Directory.Build.props"));
            var match = System.Text.RegularExpressions.Regex.Match(props, @"<Version>\s*([^<\s]+)\s*</Version>");
            return match.Success ? match.Groups[1].Value : throw new InvalidOperationException("No <Version> in Directory.Build.props");
        }
    }
}
