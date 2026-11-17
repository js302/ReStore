using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ReStore.Core.src.storage;

namespace ReStore.Core.src.utils;

/// <summary>Encrypts/decrypts small secrets for the current user. Injectable so tests never need Windows DPAPI.</summary>
public interface ISecretProtector
{
    /// <summary>False when the platform cannot protect secrets (anything other than Windows).</summary>
    bool IsAvailable { get; }

    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// Windows DPAPI, CurrentUser scope, with an application-specific entropy. A blob can only be decrypted by the
/// same Windows user on the same machine.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ReStore.Secrets.v1");

    public static DpapiSecretProtector Instance { get; } = new();

    public bool IsAvailable => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is only available on Windows.");
        }

        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is only available on Windows.");
        }

        return ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
    }
}

/// <summary>
/// Secret references usable as any provider option value: <c>dpapi:&lt;base64&gt;</c> (encrypted for the current
/// Windows user) and <c>env:NAME</c> (read from the environment at use). Plain values keep working. The model and
/// config.json always keep the reference; resolution happens only when a provider is built.
/// </summary>
public static partial class SecretReferences
{
    public const string DpapiPrefix = "dpapi:";
    public const string EnvPrefix = "env:";
    public const string RedactedValue = "***";

    /// <summary>Provider options that hold secrets (case-insensitive). Used for default encryption and redaction.</summary>
    private static readonly HashSet<string> SecretOptionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "secretAccessKey", "sessionToken", "applicationKey", "connectionString", "token", "accessToken",
        "refreshToken", "appKey", "appSecret", "password", "passphrase", "client_secret"
    };

    public static bool IsSecretOption(string optionName) => SecretOptionKeys.Contains(optionName);

    /// <summary>The names of all secret provider options (case-insensitive set).</summary>
    public static IReadOnlyCollection<string> SecretOptionNames => SecretOptionKeys;

    public static bool IsDpapiReference(string? value) =>
        value != null && value.StartsWith(DpapiPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsEnvReference(string? value) =>
        value != null && EnvReferencePattern().IsMatch(value);

    public static bool IsReference(string? value) => IsDpapiReference(value) || IsEnvReference(value);

    /// <summary>True when the value is already a reference (dpapi:/env:/%VAR%) or empty, i.e. not a literal secret to protect.</summary>
    public static bool IsProtectedOrEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) || IsReference(value) || EnvironmentVariableReferencePattern().IsMatch(value);

    /// <summary>Encrypts <paramref name="plaintext"/> into a <c>dpapi:</c> reference.</summary>
    public static string Protect(string plaintext, ISecretProtector? protector = null)
    {
        protector ??= DpapiSecretProtector.Instance;
        if (!protector.IsAvailable)
        {
            throw new PlatformNotSupportedException("Encrypting secrets requires Windows DPAPI, which is not available on this platform.");
        }

        return DpapiPrefix + Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(plaintext)));
    }

    /// <summary>
    /// Resolves <paramref name="value"/> for option <paramref name="optionName"/>. Plain values are returned unchanged.
    /// Failures throw <see cref="StorageAuthRequiredException"/> naming the option, never the value.
    /// </summary>
    public static string Resolve(string optionName, string value, ISecretProtector? protector = null)
    {
        if (IsDpapiReference(value))
        {
            protector ??= DpapiSecretProtector.Instance;
            if (!protector.IsAvailable)
            {
                throw new StorageAuthRequiredException(
                    $"Option '{optionName}' is stored as a Windows DPAPI value ('dpapi:'), which is not supported on this platform. " +
                    $"Use an 'env:NAME' reference or a plain value instead.");
            }

            try
            {
                var blob = Convert.FromBase64String(value[DpapiPrefix.Length..]);
                return Encoding.UTF8.GetString(protector.Unprotect(blob));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                throw new StorageAuthRequiredException(
                    $"Option '{optionName}' could not be decrypted. It was probably encrypted by a different Windows user or on a different machine. " +
                    "Re-enter the value in Settings (or edit config.json) on this account.", ex);
            }
        }

        if (IsEnvReference(value))
        {
            var name = value[EnvPrefix.Length..];
            var fromEnvironment = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(fromEnvironment))
            {
                throw new StorageAuthRequiredException(
                    $"Option '{optionName}' refers to the environment variable '{name}', which is not set. " +
                    "Set it for the account that runs ReStore (restart ReStore afterwards).");
            }

            return fromEnvironment;
        }

        return value;
    }

    /// <summary>Resolves every option value of an already-expanded copy.</summary>
    public static void ResolveOptions(Dictionary<string, string> options, ISecretProtector? protector = null)
    {
        foreach (var key in options.Keys.ToList())
        {
            var value = options[key];
            if (IsReference(value))
            {
                options[key] = Resolve(key, value, protector);
            }
        }
    }

    /// <summary>The value to display for <paramref name="value"/> when redacting: references are kept (dpapi blobs shortened), literal secrets masked.</summary>
    public static string Redact(string optionName, string value)
    {
        if (IsDpapiReference(value))
        {
            return DpapiPrefix + "…";
        }

        if (IsReference(value) || EnvironmentVariableReferencePattern().IsMatch(value) || string.IsNullOrEmpty(value))
        {
            return value;
        }

        return IsSecretOption(optionName) ? RedactedValue : value;
    }

    /// <summary>Rewrites literal secret options of <paramref name="options"/> into <c>dpapi:</c> references; returns the names changed.</summary>
    public static List<string> ProtectSecretOptions(Dictionary<string, string> options, ISecretProtector? protector = null)
    {
        var changed = new List<string>();
        foreach (var key in options.Keys.ToList())
        {
            if (IsSecretOption(key) && !IsProtectedOrEmpty(options[key]) && !options[key].Contains("your_", StringComparison.OrdinalIgnoreCase))
            {
                options[key] = Protect(options[key], protector);
                changed.Add(key);
            }
        }

        return changed;
    }

    [GeneratedRegex(@"^env:[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.IgnoreCase)]
    private static partial Regex EnvReferencePattern();

    [GeneratedRegex("%[^%]+%")]
    private static partial Regex EnvironmentVariableReferencePattern();
}
