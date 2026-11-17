using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary>
/// Where the CLI gets the encryption password without asking anybody, in precedence order: the first line of stdin
/// (<c>--password-stdin</c>), <c>RESTORE_ENCRYPTION_PASSWORD</c>, then the first line of the file named by
/// <c>RESTORE_ENCRYPTION_PASSWORD_FILE</c>, then the password remembered by <c>unlock --remember</c> (DPAPI-protected; only
/// when it still matches the configuration). The prompt always comes last and only in interactive mode.
/// </summary>
internal static class CliPasswordSources
{
    public const string PasswordVariable = "RESTORE_ENCRYPTION_PASSWORD";
    public const string PasswordFileVariable = "RESTORE_ENCRYPTION_PASSWORD_FILE";

    private static bool _stdinRead;
    private static string? _stdinPassword;

    private static bool _rememberedLoaded;
    private static string? _rememberedPassword;
    private static bool _staleWarned;

    public static void Reset()
    {
        _stdinRead = false;
        _stdinPassword = null;
        _rememberedLoaded = false;
        _rememberedPassword = null;
        _staleWarned = false;
    }

    /// <summary>
    /// The password from the first source that has one, or null. The remembered password is only considered when
    /// <paramref name="config"/> is given (it is checked against the configured verification token; one that no longer matches is
    /// ignored with a warning).
    /// </summary>
    public static string? TryGetConfiguredPassword(IConfigManager? config = null)
    {
        if (CliRuntime.PasswordStdin && ReadStdinPassword() is { } fromStdin)
        {
            return fromStdin;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(PasswordVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (ReadPasswordFile(Environment.GetEnvironmentVariable(PasswordFileVariable)) is { } fromFile)
        {
            return fromFile;
        }

        return config != null ? TryGetRememberedPassword(config) : null;
    }

    private static string? TryGetRememberedPassword(IConfigManager config)
    {
        if (!_rememberedLoaded)
        {
            _rememberedLoaded = true;
            _rememberedPassword = RememberedPassword.TryLoadVerified(config, CliServices.SecretProtector, out var stale);
            if (stale && !_staleWarned)
            {
                _staleWarned = true;
                Console.Error.WriteLine("Warning: the remembered encryption password no longer matches the configuration (it was changed). Ignoring it; run 'restore.exe unlock --remember' to remember the new one.");
            }
        }

        return _rememberedPassword;
    }

    // stdin can only be read once; every later request (confirmation, second command step) gets the same answer.
    private static string? ReadStdinPassword()
    {
        if (!_stdinRead)
        {
            _stdinRead = true;
            _stdinPassword = FirstLine(Console.In.ReadLine());
        }

        return _stdinPassword;
    }

    private static string? ReadPasswordFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var reader = new StreamReader(path);
            return FirstLine(reader.ReadLine());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Warning: {PasswordFileVariable} is set but the file could not be read ({ex.GetType().Name}).");
            return null;
        }
    }

    private static string? FirstLine(string? line)
    {
        var trimmed = line?.TrimEnd('\r', '\n');
        // A blank line is "no password", like an unset or blank environment variable; trailing spaces in a real password are kept.
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
