using System.Text.Json.Nodes;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary>Small helpers the configuration, setup, encryption and auth commands share.</summary>
internal static class CliSupport
{
    /// <summary>Writes the <c>--json</c> document, or the human text on the real stdout.</summary>
    public static async Task EmitAsync(CliContext ctx, object document, string text, int exitCode = Program.ExitCodes.Success)
    {
        if (ctx.Json)
        {
            await ctx.WriteJsonAsync(document, exitCode).ConfigureAwait(false);
        }
        else
        {
            await ctx.JsonOut.WriteLineAsync(text).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A warning when backing up <paramref name="expandedFolder"/> would save nothing because an <c>excludedPaths</c> entry (the
    /// default list contains the temp folder) or an exclusion glob covers it; null when nothing excludes it. Same matching as the
    /// <c>backup</c> command's guard.
    /// </summary>
    public static string? ExclusionWarning(ILogger logger, IConfigManager config, string expandedFolder) =>
        new FileSelectionService(logger, config).FindExcludingRule(expandedFolder) is { } rule
            ? $"'{expandedFolder}' is excluded by the {rule}, so a backup of it would save nothing. Change the rule (see 'restore config get excludedPaths', then 'restore config set excludedPaths <json list>') or watch another folder."
            : null;

    /// <summary>True for blank values and the placeholders of the example config (<c>your_...</c>, <c>your-...</c>, <c>optional-...</c>).</summary>
    public static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Contains("your_", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("your-", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("optional-", StringComparison.OrdinalIgnoreCase);

    public static CliCommandException Usage(string message, string? hint = null) =>
        new(Program.ExitCodes.Usage, CliErrorCodes.Usage, message, hint);

    public static CliCommandException ConfigProblem(string message, string? hint = null) =>
        new(Program.ExitCodes.Config, CliErrorCodes.Config, message, hint);

    /// <summary>One line of stdin (a value or a password), without the line break; null at end of input.</summary>
    public static string? ReadStdinLine()
    {
        var line = Console.In.ReadLine();
        return line?.TrimEnd('\r', '\n');
    }

    /// <summary>All of stdin without the trailing line break (so a multi-line JSON value works).</summary>
    public static string ReadStdinToEnd() => Console.In.ReadToEnd().TrimEnd('\r', '\n');

    /// <summary>Splits <c>key=value</c> arguments at the first '='.</summary>
    public static List<(string Key, string Value)> ParsePairs(IEnumerable<string> arguments, string optionName)
    {
        var pairs = new List<(string, string)>();
        foreach (var argument in arguments)
        {
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw Usage($"Invalid {optionName} value '{argument}'. Expected key=value.");
            }

            pairs.Add((argument[..separator].Trim(), argument[(separator + 1)..]));
        }

        return pairs;
    }

    /// <summary>
    /// Applies a validated config edit. A <see cref="ConfigEditException"/> is a usage error (exit 2); validation errors the edit would
    /// introduce come back in the result (nothing written; see <see cref="RejectAsync"/>).
    /// </summary>
    public static async Task<ConfigEditResult> EditConfigAsync(CliContext ctx, Action<JsonObject> edit, bool requireValid = false)
    {
        try
        {
            return await ConfigEditor.ApplyAsync(ctx.Config, edit, requireValid).ConfigureAwait(false);
        }
        catch (ConfigEditException ex)
        {
            throw Usage(ex.Message);
        }
    }

    /// <summary>Reports a refused edit: the errors on stderr and, with --json, a document with <c>applied: false</c>. Returns exit 3.</summary>
    public static async Task<int> RejectAsync(CliContext ctx, ConfigEditResult result)
    {
        const string message = "The change would make the configuration invalid, so nothing was written.";
        foreach (var error in result.Errors)
        {
            ctx.Logger.Log(error, LogLevel.Error);
        }

        ctx.Failure = new CliError(CliErrorCodes.Config, message, "fix the listed errors (restore.exe config schema describes every key) and retry", [.. result.Errors.Take(20)]);
        if (ctx.Json)
        {
            await ctx.WriteJsonAsync(new { applied = false, errors = result.Errors, warnings = result.Warnings }, Program.ExitCodes.Config).ConfigureAwait(false);
        }
        else
        {
            await ctx.JsonOut.WriteLineAsync($"{message} The configuration is unchanged.").ConfigureAwait(false);
        }

        return Program.ExitCodes.Config;
    }

    /// <summary>The same normalisation the GUI uses to compare folders: full path, no trailing separator, case-insensitive.</summary>
    public static string NormalizeForCompare(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToUpperInvariant();
        }
    }

    /// <summary>
    /// The text to store for a folder given on the command line: a path with <c>%VAR%</c> references stays as written, anything else
    /// becomes a full path without a trailing separator.
    /// </summary>
    public static string NormalizeStoredPath(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Contains('%', StringComparison.Ordinal))
        {
            return TrimSeparator(trimmed);
        }

        return TrimSeparator(Path.GetFullPath(trimmed));
    }

    private static string TrimSeparator(string path)
    {
        var root = Path.GetPathRoot(path);
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(trimmed) || (root != null && trimmed.Length < root.Length) ? path : trimmed;
    }

    /// <summary>
    /// <see cref="Confirm"/> for a destructive command: <c>--yes</c> or a typed <c>y</c> continues, no terminal exits 4 and a declined
    /// prompt exits 1 ("nothing was changed"), the same as <c>retention apply</c>.
    /// </summary>
    public static void ConfirmOrAbort(string question, string hint)
    {
        if (!Confirm(question, hint))
        {
            throw new CliCommandException(Program.ExitCodes.Failure, CliErrorCodes.Failure, "Cancelled: nothing was changed.");
        }
    }

    /// <summary>Asks for confirmation unless <c>--yes</c> was given; without a terminal that is "a person must act" (exit 4).</summary>
    public static bool Confirm(string question, string hint)
    {
        if (CliRuntime.Yes)
        {
            return true;
        }

        if (CliRuntime.NonInteractive)
        {
            throw CliCommandException.NeedsUser($"{question} Confirmation is required, and there is no terminal to ask on.", hint);
        }

        Console.Error.Write($"{question} [y/N] ");
        var answer = Console.In.ReadLine();
        return answer != null && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
