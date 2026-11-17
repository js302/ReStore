namespace ReStore.Core.src.utils;

/// <summary>What the UI/console should ask the user for.</summary>
/// <param name="Purpose">Encrypt or Decrypt.</param>
/// <param name="IsConfirmation">True when the user must re-type a password they just entered (token migration).</param>
public sealed record PasswordPromptRequest(PasswordPurpose Purpose, bool IsConfirmation);

/// <summary>
/// Shared password decision logic for the GUI and CLI. Encryption-mode passwords are only cached after they
/// verify against the config's verification token; configs that predate the token are migrated by asking for
/// the password twice and saving a token. Decrypt-mode passwords live in a separate cache and are never
/// handed to encryption-mode callers.
/// </summary>
public class VerifiedPasswordProvider : IPurposePasswordProvider
{
    public const int DefaultMaxAttempts = 3;

    private readonly IConfigManager _config;
    private readonly ILogger _logger;
    private readonly Func<PasswordPromptRequest, Task<string?>> _prompt;
    private readonly Action<string>? _notify;
    private readonly int _maxAttempts;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _encryptPassword;
    private string? _decryptPassword;

    /// <param name="prompt">Returns the entered password, or null/empty if the user cancelled.</param>
    /// <param name="notify">Optional user-visible message sink (wrong password, mismatch, giving up).</param>
    public VerifiedPasswordProvider(
        IConfigManager config,
        ILogger logger,
        Func<PasswordPromptRequest, Task<string?>> prompt,
        Action<string>? notify = null,
        int maxAttempts = DefaultMaxAttempts)
    {
        _config = config;
        _logger = logger;
        _prompt = prompt;
        _notify = notify;
        _maxAttempts = maxAttempts;
    }

    /// <summary>The parameterless call is treated as encryption mode, the strictest one.</summary>
    public Task<string?> GetPasswordAsync() => GetPasswordAsync(PasswordPurpose.Encrypt);

    public async Task<string?> GetPasswordAsync(PasswordPurpose purpose)
    {
        var cached = purpose == PasswordPurpose.Encrypt ? _encryptPassword : _decryptPassword;
        if (!string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        // Serialise prompts so concurrent background callers never stack dialogs.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            cached = purpose == PasswordPurpose.Encrypt ? _encryptPassword : _decryptPassword;
            if (!string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            return purpose == PasswordPurpose.Encrypt
                ? await AcquireEncryptPasswordAsync().ConfigureAwait(false)
                : await AcquireDecryptPasswordAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Seeds the encryption cache with a password the caller has just set up (wizard/Settings); it is not re-verified.</summary>
    public void SetPassword(string? password)
    {
        _encryptPassword = string.IsNullOrEmpty(password) ? null : password;
    }

    public bool IsPasswordSet() => !string.IsNullOrEmpty(_encryptPassword);

    /// <summary>Clears the decrypt-mode cache (restore callers use this after a failed decrypt). Encrypt cache is kept.</summary>
    public void ClearPassword()
    {
        _decryptPassword = null;
    }

    /// <summary>Forgets every cached password (e.g. lock).</summary>
    public void ClearAll()
    {
        _encryptPassword = null;
        _decryptPassword = null;
    }

    private async Task<string?> AcquireDecryptPasswordAsync()
    {
        var password = await _prompt(new PasswordPromptRequest(PasswordPurpose.Decrypt, false)).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        _decryptPassword = password;
        return password;
    }

    private async Task<string?> AcquireEncryptPasswordAsync()
    {
        var encryption = _config.Encryption;
        if (!encryption.Enabled || string.IsNullOrWhiteSpace(encryption.Salt))
        {
            return null;
        }

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(encryption.Salt);
        }
        catch (FormatException)
        {
            _logger.Log("Encryption salt in configuration is not valid base64.", LogLevel.Error);
            return null;
        }

        var service = new EncryptionService(_logger);
        var hasToken = !string.IsNullOrEmpty(encryption.VerificationToken);

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            var password = await _prompt(new PasswordPromptRequest(PasswordPurpose.Encrypt, false)).ConfigureAwait(false);
            if (string.IsNullOrEmpty(password))
            {
                return null;
            }

            if (hasToken)
            {
                if (service.VerifyPassword(password, salt, encryption.VerificationToken!, encryption.KeyDerivationIterations))
                {
                    _encryptPassword = password;
                    return password;
                }

                Report(attempt, "Incorrect password.");
                continue;
            }

            // Migration: no token yet, so a typo would silently become the key. Require it twice.
            var confirmation = await _prompt(new PasswordPromptRequest(PasswordPurpose.Encrypt, true)).ConfigureAwait(false);
            if (string.IsNullOrEmpty(confirmation))
            {
                return null;
            }

            if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            {
                Report(attempt, "The passwords do not match.");
                continue;
            }

            encryption.VerificationToken = service.CreatePasswordVerificationToken(
                password, salt, encryption.KeyDerivationIterations);
            await _config.SaveAsync().ConfigureAwait(false);
            _encryptPassword = password;
            return password;
        }

        return null;
    }

    private void Report(int attempt, string problem)
    {
        var remaining = _maxAttempts - attempt;
        _notify?.Invoke(remaining > 0
            ? $"{problem} Attempts remaining: {remaining}"
            : $"{problem} Maximum attempts reached. Operation cancelled.");
    }
}

/// <summary>Single place that turns encryption on, so the wizard and Settings cannot diverge.</summary>
public static class EncryptionConfigurator
{
    /// <summary>Enables encryption in <paramref name="config"/> with the given salt and a fresh password verification token. Does not save.</summary>
    public static void Enable(IConfigManager config, ILogger logger, string password, byte[] salt) =>
        Enable(config.Encryption, logger, password, salt);

    /// <summary>Same for a bare <see cref="EncryptionConfig"/> (the CLI edits the config document and only needs the resulting values).</summary>
    public static void Enable(EncryptionConfig encryption, ILogger logger, string password, byte[] salt)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(salt);

        var token = new EncryptionService(logger).CreatePasswordVerificationToken(
            password, salt, encryption.KeyDerivationIterations);

        encryption.Enabled = true;
        encryption.Salt = Convert.ToBase64String(salt);
        encryption.VerificationToken = token;
    }
}
