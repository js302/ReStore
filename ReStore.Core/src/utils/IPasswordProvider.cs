namespace ReStore.Core.src.utils;

/// <summary>Why a password is being requested; the two purposes never share a cached password.</summary>
public enum PasswordPurpose
{
    /// <summary>Encrypting new backups with the configured key (must match the config's verification token).</summary>
    Encrypt,

    /// <summary>Decrypting an existing snapshot (restore/verify); may belong to an older key.</summary>
    Decrypt
}

public interface IPasswordProvider
{
    Task<string?> GetPasswordAsync();

    bool IsPasswordSet();
    void ClearPassword();
}

/// <summary>A provider that keeps separate encrypt/decrypt passwords.</summary>
public interface IPurposePasswordProvider : IPasswordProvider
{
    Task<string?> GetPasswordAsync(PasswordPurpose purpose);
}

public static class PasswordProviderExtensions
{
    /// <summary>Purpose-aware request; providers that do not distinguish purposes fall back to the plain call.</summary>
    public static Task<string?> GetPasswordAsync(this IPasswordProvider provider, PasswordPurpose purpose)
    {
        return provider is IPurposePasswordProvider purposeProvider
            ? purposeProvider.GetPasswordAsync(purpose)
            : provider.GetPasswordAsync();
    }
}

public class StaticPasswordProvider : IPasswordProvider
{
    private readonly string? _password;

    public StaticPasswordProvider(string? password)
    {
        _password = password;
    }

    public Task<string?> GetPasswordAsync()
    {
        return Task.FromResult(_password);
    }

    public bool IsPasswordSet()
    {
        return !string.IsNullOrEmpty(_password);
    }

    public void ClearPassword()
    {
        // Static password provider doesn't support clearing
    }
}
