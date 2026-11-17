namespace ReStore.Core.src.cli;

/// <summary>Documentation of one global option for help and <c>commands --json</c>.</summary>
internal sealed record GlobalOptionInfo(string Name, string? ValueName, string Description);

/// <summary>
/// The options every command accepts, before or after the command name: <c>--json</c>, <c>--quiet</c>,
/// <c>--non-interactive</c>, <c>--yes</c>, <c>--wait &lt;seconds&gt;</c>, <c>--password-stdin</c> and <c>--help</c>/<c>-h</c>.
/// </summary>
internal sealed record CliGlobals
{
    public const string JsonFlag = "--json";
    public const string QuietFlag = "--quiet";
    public const string NonInteractiveFlag = "--non-interactive";
    public const string YesFlag = "--yes";
    public const string WaitFlag = "--wait";
    public const string PasswordStdinFlag = "--password-stdin";
    public const string HelpFlag = "--help";

    public static readonly GlobalOptionInfo[] Documentation =
    [
        new(JsonFlag, null, "Machine-readable output: one JSON document on stdout (logs go to stderr). Every document has \"ok\"; failures are {\"ok\":false,\"exitCode\":N,\"error\":{\"code\",\"message\",\"hint\"}}. Commands that cannot produce JSON reject it."),
        new(QuietFlag, null, "Write nothing to stdout except --json documents; only errors go to stderr."),
        new(NonInteractiveFlag, null, "Never prompt. Also on when stdin is redirected, RESTORE_NON_INTERACTIVE=1 or CI=true. A missing password exits 4 instead of waiting."),
        new(YesFlag, null, "Confirm destructive actions without asking (required for them in non-interactive mode)."),
        new(WaitFlag, "seconds", "How long to wait for another ReStore process (GUI, service, another CLI) that holds the repository. Default 30; 0 fails at once with exit 5."),
        new(PasswordStdinFlag, null, "Read the encryption password from the first line of stdin."),
        new("--help", null, "Show help for the command (also -h; same as 'help <command>').")
    ];

    public bool Json { get; init; }
    public bool Quiet { get; init; }
    public bool NonInteractive { get; init; }
    public bool Yes { get; init; }
    public bool PasswordStdin { get; init; }
    public bool Help { get; init; }

    /// <summary>Seconds to wait for the repository, or null for the default.</summary>
    public int? WaitSeconds { get; init; }

    /// <summary>
    /// Removes the global options from <paramref name="args"/> wherever they appear. On a bad <c>--wait</c> value
    /// <paramref name="error"/> is set, but the other flags are still read so a <c>--json</c> error document can be written.
    /// </summary>
    public static CliGlobals Extract(string[] args, out string[] remaining, out string? error)
    {
        error = null;
        var rest = new List<string>(args.Length);
        bool json = false, quiet = false, nonInteractive = false, yes = false, passwordStdin = false, help = false;
        int? wait = null;

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (Is(token, JsonFlag))
            {
                json = true;
            }
            else if (Is(token, QuietFlag))
            {
                quiet = true;
            }
            else if (Is(token, NonInteractiveFlag))
            {
                nonInteractive = true;
            }
            else if (Is(token, YesFlag))
            {
                yes = true;
            }
            else if (Is(token, PasswordStdinFlag))
            {
                passwordStdin = true;
            }
            else if (Is(token, HelpFlag) || token == "-h" || token == "/?")
            {
                help = true;
            }
            else if (Is(token, WaitFlag))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error ??= $"Option '{token}' requires a value.";
                }
                else if (int.TryParse(args[++index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                {
                    wait = seconds;
                }
                else
                {
                    error ??= $"Invalid {WaitFlag} value '{args[index]}'. Expected a whole number of seconds, 0 or more.";
                }
            }
            else
            {
                rest.Add(token);
            }
        }

        remaining = [.. rest];
        return new CliGlobals { Json = json, Quiet = quiet, NonInteractive = nonInteractive, Yes = yes, PasswordStdin = passwordStdin, Help = help, WaitSeconds = wait };
    }

    /// <summary>
    /// Non-interactive when asked for (<c>--non-interactive</c>), when <c>RESTORE_NON_INTERACTIVE</c> is <c>1</c>/<c>true</c>,
    /// when <c>CI</c> is <c>true</c>/<c>1</c>, or when stdin is redirected (nobody can type an answer).
    /// </summary>
    public static bool ResolveNonInteractive(bool flag, Func<string, string?> getEnvironmentVariable, bool inputRedirected) =>
        flag
        || inputRedirected
        || IsTruthy(getEnvironmentVariable("RESTORE_NON_INTERACTIVE"))
        || IsTruthy(getEnvironmentVariable("CI"));

    private static bool IsTruthy(string? value) =>
        value != null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    private static bool Is(string token, string flag) => token.Equals(flag, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Process-wide state of the running CLI invocation, set by <c>Program.Main</c> and reset when it ends, so the password
/// provider and other deep code can see the global options without threading them through every call.
/// </summary>
internal static class CliRuntime
{
    public static bool NonInteractive { get; set; }
    public static bool PasswordStdin { get; set; }
    public static bool Yes { get; set; }

    /// <summary>Set when a password was needed but could not be obtained without a prompt; turns a plain failure into exit 4.</summary>
    public static string? PasswordUnavailableMessage { get; set; }

    public static void Reset()
    {
        NonInteractive = false;
        PasswordStdin = false;
        Yes = false;
        PasswordUnavailableMessage = null;
        CliPasswordSources.Reset();
    }
}
