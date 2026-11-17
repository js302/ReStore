namespace ReStore.Core.src.storage.sftp;

/// <summary>
/// Parsing and matching of SHA256 SSH host-key fingerprints as printed by OpenSSH
/// (<c>SHA256:base64</c>, unpadded). The <c>SHA256:</c> prefix and trailing <c>=</c> padding are optional.
/// </summary>
public static class SftpHostKey
{
    private const string Prefix = "SHA256:";

    /// <summary>
    /// Returns the canonical form (no prefix, no padding, no surrounding whitespace), or null if the
    /// value is not a valid base64 encoding of a 32-byte SHA256 digest.
    /// </summary>
    public static string? Normalize(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        var value = fingerprint.Trim();
        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[Prefix.Length..];
        }

        value = value.TrimEnd('=');
        if (value.Length != 43)
        {
            return null;
        }

        // 43 unpadded base64 chars = 32 bytes. Validate the alphabet by decoding.
        try
        {
            var bytes = Convert.FromBase64String(value + "=");
            return bytes.Length == 32 ? value : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool IsValid(string? fingerprint) => Normalize(fingerprint) != null;

    /// <summary>Base64 is case sensitive, so the comparison is ordinal after normalisation.</summary>
    public static bool Matches(string? expected, string? actual)
    {
        var normalizedExpected = Normalize(expected);
        var normalizedActual = Normalize(actual);
        return normalizedExpected != null
            && normalizedActual != null
            && string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal);
    }
}
