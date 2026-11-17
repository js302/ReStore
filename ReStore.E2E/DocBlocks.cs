using System.Text;
using System.Text.RegularExpressions;

namespace ReStore.E2E;

/// <summary>Reads the runnable command blocks (marked with <c>&lt;!-- e2e --&gt;</c> above the fence) out of docs/agents.md.</summary>
internal static class DocBlocks
{
    public const string Marker = "<!-- e2e -->";

    public sealed record Block(string Heading, IReadOnlyList<string> Lines)
    {
        public override string ToString() => Heading;
    }

    /// <summary>One command of a block: the stdin it is fed (from an <c>echo x |</c> prefix) and its arguments after <c>restore</c>.</summary>
    public sealed record Command(string? Stdin, IReadOnlyList<string> Arguments, string Original);

    public static string AgentsMarkdownPath => Path.Combine(CliBinary.RepositoryRoot, "docs", "agents.md");

    public static string CliMarkdownPath => Path.Combine(CliBinary.RepositoryRoot, "docs", "usage", "cli.md");

    public static string ReadNormalized(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    public static IReadOnlyList<Block> RunnableBlocks()
    {
        var lines = ReadNormalized(AgentsMarkdownPath).Split('\n');
        var blocks = new List<Block>();
        var heading = string.Empty;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith('#'))
            {
                heading = lines[i].TrimStart('#', ' ');
            }

            if (lines[i].Trim() != Marker)
            {
                continue;
            }

            Assert.True(i + 1 < lines.Length && lines[i + 1].StartsWith("```", StringComparison.Ordinal), $"'{Marker}' must be directly above a code fence (line {i + 1}).");
            var body = new List<string>();
            for (var j = i + 2; j < lines.Length && !lines[j].StartsWith("```", StringComparison.Ordinal); j++)
            {
                body.Add(lines[j]);
            }

            blocks.Add(new Block(heading, body));
        }

        return blocks;
    }

    /// <summary>Splits a block into commands. Only <c>restore ...</c> lines (optionally <c>echo x | restore ...</c>) are runnable; the guide shows nothing else in marked blocks.</summary>
    public static IReadOnlyList<Command> Commands(Block block, Func<string, string> substitute)
    {
        var commands = new List<Command>();
        foreach (var raw in block.Lines.Where(l => l.Trim().Length > 0))
        {
            string? stdin = null;
            var line = raw.Trim();
            var pipe = line.IndexOf(" | restore ", StringComparison.Ordinal);
            if (line.StartsWith("echo ", StringComparison.Ordinal) && pipe > 0)
            {
                stdin = substitute(line[5..pipe]) + "\n";
                line = line[(pipe + 3)..];
            }

            Assert.True(line.StartsWith("restore ", StringComparison.Ordinal), $"'{raw}' in '{block.Heading}' is not a runnable 'restore ...' line.");
            var arguments = Tokenize(substitute(line["restore ".Length..]));
            commands.Add(new Command(stdin, arguments, raw.Trim()));
        }

        return commands;
    }

    /// <summary>Whitespace-separated words; double quotes group (and are removed); backslashes are literal (the guide uses Windows paths).</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var any = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>Placeholders such as <c>&lt;dir&gt;</c> that are still present after substitution (the test must know about every one).</summary>
    public static IReadOnlyList<string> Placeholders(string text) =>
        [.. Regex.Matches(text, "<[A-Za-z][A-Za-z0-9]*>").Select(m => m.Value).Distinct()];
}
