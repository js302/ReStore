using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary><c>encryption status|enable|disable</c>, <c>unlock</c> and <c>lock</c>.</summary>
internal static class EncryptionCommands
{
    public static IEnumerable<CommandSpec> Specs { get; } =
    [
        new CommandSpec
        {
            Name = "encryption status",
            Category = CommandCategory.Repository,
            Summary = "Show whether encryption is on and whether a password is remembered.",
            Description = "Read-only; never needs the password. JSON: enabled, keyDerivationIterations, saltPresent, verificationTokenPresent, rememberedPasswordPresent, rememberedPasswordSupported.",
            Examples = ["encryption status --json"],
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = StatusAsync
        },
        new CommandSpec
        {
            Name = "encryption enable",
            Category = CommandCategory.Repository,
            Summary = "Turn on encryption for new backups (AES-256-GCM, password-derived key).",
            Description = "The password comes from --password-stdin, RESTORE_ENCRYPTION_PASSWORD or RESTORE_ENCRYPTION_PASSWORD_FILE; in a terminal it is asked for twice, without one the command exits 4. "
                + "Same rules as the app: at least 8 characters, a weak one is accepted with a warning. Refuses when encryption is already on (change the password with change-password). "
                + "Store the password safely: it cannot be recovered. After the first backup keep an offline copy of the key file (export-key). Use unlock --remember to let scheduled runs use the password. JSON: enabled, passwordStrength, warnings, notes, backup.",
            Examples = ["encryption enable --password-stdin"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = EnableAsync
        },
        new CommandSpec
        {
            Name = "encryption disable",
            Category = CommandCategory.Repository,
            Summary = "Turn off encryption for new backups (existing encrypted backups still need the password).",
            Description = "Needs --yes without a terminal (exit 4 otherwise). Only new backups become unencrypted; the salt and password check stay in the config so existing encrypted snapshots remain restorable with the password. JSON: enabled, changed, backup.",
            Examples = ["encryption disable --yes"],
            Mutates = true,
            Destructive = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = DisableAsync
        },
        new CommandSpec
        {
            Name = "unlock",
            Category = CommandCategory.Repository,
            Summary = "Check the encryption password; --remember keeps it for later runs (Windows).",
            Description = "Reads the password like other commands (--password-stdin, RESTORE_ENCRYPTION_PASSWORD, RESTORE_ENCRYPTION_PASSWORD_FILE, a remembered one, else a prompt; exit 4 without a terminal) and verifies it against the configuration: exit 1 when it is wrong. "
                + "--remember stores it protected with the current Windows user's DPAPI key in state/remembered-password.bin, so later commands, run-due from Task Scheduler and --service need no environment variable. "
                + "Anyone running as this Windows user can use it (the same trust model as dpapi: provider secrets); 'lock' removes it; after change-password it is updated. A remembered password that no longer matches is ignored with a warning. JSON: unlocked, remembered.",
            Options = [new OptionSpec("--remember", "Store the password for later runs (Windows only).")],
            Examples = ["unlock --password-stdin --remember"],
            Mutates = true,
            Config = ConfigRequirement.Loaded,
            State = StateRequirement.None,
            PayloadOnStdout = true,
            Handler = UnlockAsync
        },
        new CommandSpec
        {
            Name = "lock",
            Category = CommandCategory.Repository,
            Summary = "Forget the remembered encryption password.",
            Description = "Deletes state/remembered-password.bin. Nothing else changes; a password from the environment or a file is not affected. JSON: locked, wasRemembered.",
            Examples = ["lock"],
            Mutates = true,
            Config = ConfigRequirement.None,
            State = StateRequirement.None,
            Handler = LockAsync
        }
    ];

    private static async Task<int> StatusAsync(CliContext ctx)
    {
        var encryption = ctx.Config.Encryption;
        var remembered = RememberedPassword.Exists;
        var supported = CliServices.SecretProtector.IsAvailable;
        await CliSupport.EmitAsync(
            ctx,
            new
            {
                enabled = encryption.Enabled,
                keyDerivationIterations = encryption.KeyDerivationIterations,
                saltPresent = !string.IsNullOrWhiteSpace(encryption.Salt),
                verificationTokenPresent = !string.IsNullOrWhiteSpace(encryption.VerificationToken),
                rememberedPasswordPresent = remembered,
                rememberedPasswordSupported = supported
            },
            $"Encryption: {(encryption.Enabled ? "enabled" : "disabled")}{Environment.NewLine}"
            + $"Key derivation iterations: {encryption.KeyDerivationIterations:N0}{Environment.NewLine}"
            + $"Salt: {(string.IsNullOrWhiteSpace(encryption.Salt) ? "no" : "yes")}, password check token: {(string.IsNullOrWhiteSpace(encryption.VerificationToken) ? "no" : "yes")}{Environment.NewLine}"
            + $"Remembered password: {(remembered ? "yes" : "no")}{(supported ? string.Empty : " (not supported on this platform)")}").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    /// <summary>
    /// The new password from the usual sources (never a remembered one), else asked for twice; without a terminal exit 4.
    /// Enforces the app's minimum length.
    /// </summary>
    internal static string AcquireNewPassword()
    {
        var password = CliPasswordSources.TryGetConfiguredPassword();
        if (password == null)
        {
            if (CliRuntime.NonInteractive)
            {
                const string message = "The new encryption password is required but cannot be asked for without a terminal.";
                CliRuntime.PasswordUnavailableMessage = message;
                throw CliCommandException.NeedsUser(message, CliErrorCodes.PasswordHint);
            }

            Console.Error.Write("Enter new encryption password: ");
            password = Program.ReadPasswordFromConsole();
            Console.Error.Write("Confirm new encryption password: ");
            if (!string.Equals(password, Program.ReadPasswordFromConsole(), StringComparison.Ordinal))
            {
                throw CliSupport.Usage("The passwords do not match.");
            }
        }

        if (password.Length < PasswordStrength.MinimumLength)
        {
            throw CliSupport.Usage($"The password must be at least {PasswordStrength.MinimumLength} characters long.");
        }

        return password;
    }

    internal static readonly string[] SetupNotes =
    [
        "Store your password securely: it cannot be recovered if lost.",
        "After the first backup, keep an offline copy of the key file: restore.exe export-key <file>.",
        "Scheduled runs and --service need the password without a prompt: restore.exe unlock --remember (Windows), or RESTORE_ENCRYPTION_PASSWORD / RESTORE_ENCRYPTION_PASSWORD_FILE."
    ];

    /// <summary>The edit that turns encryption on: salt, password check token and flag, keeping the configured iteration count.</summary>
    internal static Action<System.Text.Json.Nodes.JsonObject> EnableEdit(IConfigManager config, ILogger logger, string password)
    {
        var settings = new EncryptionConfig { KeyDerivationIterations = config.Encryption.KeyDerivationIterations };
        EncryptionConfigurator.Enable(settings, logger, password, EncryptionService.GenerateSalt());
        return root =>
        {
            if (root["encryption"] is not System.Text.Json.Nodes.JsonObject section)
            {
                section = [];
                root["encryption"] = section;
            }

            section["enabled"] = true;
            section["salt"] = settings.Salt;
            section["keyDerivationIterations"] = settings.KeyDerivationIterations;
            section["verificationToken"] = settings.VerificationToken;
        };
    }

    private static async Task<int> EnableAsync(CliContext ctx)
    {
        if (ctx.Config.Encryption.Enabled)
        {
            throw CliSupport.Usage("Encryption is already enabled.", "change the password with 'restore.exe change-password', or turn it off with 'restore.exe encryption disable --yes'");
        }

        var password = AcquireNewPassword();
        var strength = PasswordStrength.Evaluate(password);
        var warnings = new List<string>();
        if (strength.IsWeak)
        {
            warnings.Add("The password is weak. Use 12 or more characters mixing upper and lower case, digits and symbols.");
        }

        var result = await CliSupport.EditConfigAsync(ctx, EnableEdit(ctx.Config, ctx.Logger, password)).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        // A password remembered for an earlier encryption setup would never match the new one.
        RememberedPassword.Clear();
        warnings.AddRange(result.Warnings);
        await CliSupport.EmitAsync(
            ctx,
            new { enabled = true, passwordStrength = new { label = strength.Label, length = strength.Length }, warnings, notes = SetupNotes, backup = result.BackupPath },
            $"Encryption has been enabled. All new backups will be encrypted with AES-256-GCM ({strength.Label} password, {strength.Length} characters)."
                + string.Concat(warnings.Select(w => $"{Environment.NewLine}Warning: {w}"))
                + string.Concat(SetupNotes.Select(n => $"{Environment.NewLine}{n}"))).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> DisableAsync(CliContext ctx)
    {
        if (!ctx.Config.Encryption.Enabled)
        {
            await CliSupport.EmitAsync(ctx, new { enabled = false, changed = false }, "Encryption is already disabled.").ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        if (!CliSupport.Confirm(
                "Disable encryption? New backups will no longer be encrypted; existing encrypted backups still require the password to restore.",
                "pass --yes to confirm"))
        {
            throw CliSupport.Usage("Cancelled; encryption is still enabled.");
        }

        var result = await CliSupport.EditConfigAsync(ctx, root =>
        {
            if (root["encryption"] is System.Text.Json.Nodes.JsonObject section)
            {
                section["enabled"] = false;
            }
        }).ConfigureAwait(false);
        if (!result.Applied)
        {
            return await CliSupport.RejectAsync(ctx, result).ConfigureAwait(false);
        }

        await CliSupport.EmitAsync(
            ctx,
            new { enabled = false, changed = result.Changed, backup = result.BackupPath, warnings = result.Warnings },
            "Encryption has been disabled. New backups will not be encrypted; existing encrypted backups still require the password to restore.").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> UnlockAsync(CliContext ctx)
    {
        var encryption = ctx.Config.Encryption;
        if (!encryption.Enabled || string.IsNullOrWhiteSpace(encryption.Salt))
        {
            throw CliSupport.Usage("Encryption is not enabled, so there is nothing to unlock.", "turn it on with 'restore.exe encryption enable'");
        }

        if (string.IsNullOrEmpty(encryption.VerificationToken))
        {
            throw new CliCommandException(Program.ExitCodes.Failure, CliErrorCodes.Failure,
                "The configuration has no password check token (it predates it), so the password cannot be verified here.",
                "run a backup once with the password to add the token");
        }

        var remember = ctx.Parsed.HasOption("--remember");
        if (remember && !CliServices.SecretProtector.IsAvailable)
        {
            throw new CliCommandException(Program.ExitCodes.Failure, CliErrorCodes.Failure,
                "--remember needs Windows DPAPI, which is not available on this platform.",
                "use RESTORE_ENCRYPTION_PASSWORD_FILE (a file only you can read) instead");
        }

        var password = await Program.PromptForPasswordAsync(new PasswordPromptRequest(PasswordPurpose.Decrypt, false), allowPrompt: true, ctx.Config).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
        {
            throw CliCommandException.NeedsUser("No password was given.", CliErrorCodes.PasswordHint);
        }

        var valid = new EncryptionService(ctx.Logger).VerifyPassword(
            password, Convert.FromBase64String(encryption.Salt), encryption.VerificationToken, encryption.KeyDerivationIterations);
        if (!valid)
        {
            throw new CliCommandException(Program.ExitCodes.Failure, CliErrorCodes.Failure, "Incorrect password.");
        }

        if (remember)
        {
            RememberedPassword.Save(password, CliServices.SecretProtector);
        }

        await CliSupport.EmitAsync(
            ctx,
            new { unlocked = true, remembered = remember },
            remember
                ? "The password is correct and is remembered for this Windows user. Run 'restore.exe lock' to forget it."
                : "The password is correct.").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> LockAsync(CliContext ctx)
    {
        var was = RememberedPassword.Clear();
        await CliSupport.EmitAsync(
            ctx,
            new { locked = true, wasRemembered = was },
            was ? "The remembered password was removed." : "No password was remembered.").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }
}
