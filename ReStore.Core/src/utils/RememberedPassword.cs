namespace ReStore.Core.src.utils;

/// <summary>
/// The encryption password kept for later CLI runs (<c>unlock --remember</c>): protected with the current user's Windows DPAPI
/// key in <c>state/remembered-password.bin</c>, so anyone running as this Windows user can use it (the same trust model as
/// <c>dpapi:</c> provider secrets). The protector is injectable so tests need no DPAPI.
/// </summary>
public static class RememberedPassword
{
    public const string FileName = "remembered-password.bin";

    public static string FilePath => Path.Combine(AppPaths.StateDirectory, FileName);

    public static bool Exists => File.Exists(FilePath);

    public static void Save(string password, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (!protector.IsAvailable)
        {
            throw new PlatformNotSupportedException("Remembering the password needs Windows DPAPI, which is not available on this platform.");
        }

        Directory.CreateDirectory(AppPaths.StateDirectory);
        SharedFile.WriteAllBytesAtomic(FilePath, protector.Protect(System.Text.Encoding.UTF8.GetBytes(password)));
    }

    /// <summary>The stored password, or null when there is none or it cannot be decrypted here (another Windows user, another machine).</summary>
    public static string? TryLoad(ISecretProtector protector)
    {
        if (!protector.IsAvailable || !File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var password = System.Text.Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(FilePath)));
            return string.IsNullOrEmpty(password) ? null : password;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes the stored password; true when there was one.</summary>
    public static bool Clear()
    {
        if (!File.Exists(FilePath))
        {
            return false;
        }

        File.Delete(FilePath);
        return true;
    }

    /// <summary>
    /// The stored password when it is the right one for <paramref name="config"/>; null when none is stored, encryption is off or
    /// unverifiable, or the password no longer matches (<paramref name="stale"/> is then true: the password was changed).
    /// </summary>
    public static string? TryLoadVerified(IConfigManager config, ISecretProtector protector, out bool stale)
    {
        stale = false;
        var encryption = config.Encryption;
        if (encryption == null || !encryption.Enabled || string.IsNullOrWhiteSpace(encryption.Salt) || string.IsNullOrEmpty(encryption.VerificationToken))
        {
            return null;
        }

        var password = TryLoad(protector);
        if (password == null)
        {
            // A file that cannot be decrypted is as stale as one with the wrong password.
            stale = protector.IsAvailable && Exists;
            return null;
        }

        try
        {
            if (new EncryptionService(new ConfigPortability.SilentLogger()).VerifyPassword(
                    password, Convert.FromBase64String(encryption.Salt), encryption.VerificationToken, encryption.KeyDerivationIterations))
            {
                return password;
            }
        }
        catch (FormatException)
        {
            return null;
        }

        stale = true;
        return null;
    }
}
