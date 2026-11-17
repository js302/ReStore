namespace ReStore.Core.src.cli;

/// <summary>
/// A command failed in a way that has its own exit code and JSON error code. Throw it from a handler (or from code it calls)
/// instead of logging and returning an exit code when the failure needs a hint for the caller.
/// </summary>
internal sealed class CliCommandException(int exitCode, string code, string message, string? hint = null) : Exception(message)
{
    public int ExitCode { get; } = exitCode;

    /// <summary>The <c>error.code</c> of the JSON error document: usage, config, failure, busy, needs_user or cancelled.</summary>
    public string Code { get; } = code;

    public string? Hint { get; } = hint;

    /// <summary>Something only a person can do (a password, a sign-in, elevation, <c>--yes</c>) is needed.</summary>
    public static CliCommandException NeedsUser(string message, string? hint = null) =>
        new(Program.ExitCodes.NeedsUser, CliErrorCodes.NeedsUser, message, hint);

    /// <summary>A snapshot, group, path or file the command was pointed at is not in the storage: exit 1 with <c>error.code</c> <c>not_found</c>.</summary>
    public static CliCommandException NotFound(string message, string? hint = null) =>
        new(Program.ExitCodes.Failure, CliErrorCodes.NotFound, message, hint);

    public static CliCommandException Busy(string message) =>
        new(Program.ExitCodes.Busy, CliErrorCodes.Busy, message, CliErrorCodes.BusyHint);
}

/// <summary>The <c>error.code</c> values of JSON error documents and their default hints.</summary>
internal static class CliErrorCodes
{
    public const string Usage = "usage";
    public const string Config = "config";
    public const string Failure = "failure";
    public const string Busy = "busy";
    public const string NeedsUser = "needs_user";
    public const string Cancelled = "cancelled";

    /// <summary>A snapshot, manifest or file the command was pointed at is not in the storage. Exit code 1 (failure), more specific code.</summary>
    public const string NotFound = "not_found";

    public const string BusyHint = "another ReStore process is using the repository; retry or pass --wait <seconds>";

    public const string PasswordHint =
        "provide the encryption password without a prompt: pipe it to --password-stdin, set RESTORE_ENCRYPTION_PASSWORD, "
        + "set RESTORE_ENCRYPTION_PASSWORD_FILE to a file whose first line is the password, or (Windows) run 'restore.exe unlock --remember' once";

    public static string ForExitCode(int exitCode) => exitCode switch
    {
        Program.ExitCodes.Usage => Usage,
        Program.ExitCodes.Config => Config,
        Program.ExitCodes.NeedsUser => NeedsUser,
        Program.ExitCodes.Busy => Busy,
        Program.ExitCodes.Cancelled => Cancelled,
        _ => Failure
    };
}

/// <summary>The error object of a JSON error document.</summary>
internal sealed record CliError(string Code, string Message, string? Hint = null, IReadOnlyList<string>? Details = null);
