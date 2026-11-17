using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core;
using ReStore.Core.src.cli;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary><c>init</c>, <c>encryption</c>, <c>unlock</c>/<c>lock</c> and <c>auth</c>, run in-process against a temp data root.</summary>
public class CliSetupCommandsTests : CliConfigTestBase
{
    private const string Password = "correct horse battery 9";

    private string NewStore => Path.Combine(Root, "init-store");

    // ---- init ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Init_NextSteps_QuoteAFolderWithSpace_SoTheCommandCanBeCopied()
    {
        WritePristineConfig();
        var folder = Path.Combine(Root, "My Documents");
        Directory.CreateDirectory(folder);

        var (exitCode, stdout, stderr) = await RunAsync("init", "--storage", "local", "--path", NewStore, "--watch", folder, "--json");
    
        exitCode.Should().Be(0, stdout + stderr);
        var steps = Json(stdout)["nextSteps"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
        steps.Should().Contain($"restore.exe backup \"{folder}\"");
    }

    [Fact]
    public async Task Init_Local_ConfiguresStorageAndFolders_AndReportsTheNextSteps()
    {
        WritePristineConfig();

        var (exitCode, stdout, stderr) = await RunAsync("init", "--storage", "local", "--path", NewStore, "--watch", WatchedDirectory, "--json");

        exitCode.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["storage"]!["type"]!.GetValue<string>().Should().Be("local");
        doc["storage"]!["path"]!.GetValue<string>().Should().Be(NewStore);
        doc["watchDirectories"]!.AsArray().Select(w => w!.GetValue<string>()).Should().Equal(WatchedDirectory);
        doc["encryption"]!["enabled"]!.GetValue<bool>().Should().BeFalse();
        var steps = doc["nextSteps"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
        var watched = WatchedDirectory.Contains(' ') ? $"\"{WatchedDirectory}\"" : WatchedDirectory;
        steps.Should().Contain($"restore.exe backup {watched}").And.Contain("restore.exe schedule install");
        steps.Should().NotContain("restore.exe unlock --remember");

        var config = ReadConfig();
        config["globalStorageType"]!.GetValue<string>().Should().Be("local");
        config["storageSources"]!["local"]!["path"]!.GetValue<string>().Should().Be(NewStore);
        config["watchDirectories"]!.AsArray().Should().HaveCount(1);
        config["retention"].Should().NotBeNull("everything else keeps its defaults");
        Directory.Exists(NewStore).Should().BeTrue();
    }

    [Fact]
    public async Task Init_RefusesToOverwriteAWorkingSetup_WithoutForce()
    {
        WritePristineConfig();
        (await RunAsync("init", "--storage", "local", "--path", NewStore, "--watch", WatchedDirectory)).ExitCode.Should().Be(0);
        var before = File.ReadAllBytes(AppPaths.ConfigFile);

        var (exitCode, stdout, stderr) = await RunAsync("init", "--storage", "local", "--path", Path.Combine(Root, "other"), "--watch", WatchedDirectory, "--json");

        exitCode.Should().Be(2, stdout + stderr);
        var error = Json(stdout)["error"]!;
        error["code"]!.GetValue<string>().Should().Be("usage");
        error["message"]!.GetValue<string>().Should().Contain("already has a ReStore setup");
        error["hint"]!.GetValue<string>().Should().Contain("--force");
        File.ReadAllBytes(AppPaths.ConfigFile).Should().Equal(before);

        var other = Path.Combine(Root, "other");
        (await RunAsync("init", "--storage", "local", "--path", other, "--watch", WatchedDirectory, "--force")).ExitCode.Should().Be(0);
        ReadConfig()["storageSources"]!["local"]!["path"]!.GetValue<string>().Should().Be(other);
    }

    [Fact]
    public async Task Init_RefusesWhenAStorageOrEncryptionWasAlreadyConfigured()
    {
        WritePristineConfig();
        (await RunAsync("config", "set", "globalStorageType", "local")).ExitCode.Should().Be(0, "setting the default value again changes nothing");
        // Pristine except for the default storage name: still a fresh machine.
        var fresh = ReadConfig();
        SetupCommands_CustomisationOf(fresh).Should().BeNull();

        fresh["encryption"]!["enabled"] = true;
        SetupCommands_CustomisationOf(fresh).Should().Contain("encryption");
        fresh["encryption"]!["enabled"] = false;
        fresh["storageSources"]!["s3"]!["options"]!["bucketName"] = "mine";
        SetupCommands_CustomisationOf(fresh).Should().Contain("storage");
        fresh = ReadConfig();
        fresh["globalStorageType"] = "s3";
        SetupCommands_CustomisationOf(fresh).Should().Contain("default storage");
        fresh = ReadConfig();
        fresh["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = "D:\\Data", ["storageType"] = null });
        SetupCommands_CustomisationOf(fresh).Should().Contain("folders");
    }

    private static string? SetupCommands_CustomisationOf(JsonObject config) => SetupCommands.CustomisationOf(config);

    [Fact]
    public async Task Init_Validates_BeforeWriting_AndRejectsBadArguments()
    {
        WritePristineConfig();
        var before = File.ReadAllBytes(AppPaths.ConfigFile);

        (await RunAsync("init")).ExitCode.Should().Be(2);
        (await RunAsync("init", "--storage", "floppy")).ExitCode.Should().Be(2);
        (await RunAsync("init", "--storage", "s3", "--option", "region=eu-west-1")).ExitCode.Should().Be(2, "required options are missing");
        (await RunAsync("init", "--storage", "local", "--path", NewStore, "--watch", Path.Combine(Root, "missing"))).ExitCode.Should().Be(2);
        (await RunAsync("init", "--storage", "local", "--path", NewStore, "--watch", WatchedDirectory, "--option", "bucketName=x")).ExitCode.Should().Be(2);

        File.ReadAllBytes(AppPaths.ConfigFile).Should().Equal(before);
    }

    [Fact]
    public async Task Init_WithoutAValidFolder_IsRefusedAsInvalid()
    {
        WritePristineConfig();
        var before = File.ReadAllBytes(AppPaths.ConfigFile);

        // Without --watch the example's %USERPROFILE% folders stay; on a machine where none exists the result is invalid.
        var defaultsExist = ReadConfig()["watchDirectories"]!.AsArray()
            .Any(w => Directory.Exists(ConfigManager.ExpandConfiguredPath(w!["path"]!.GetValue<string>())));
        var (exitCode, _, _) = await RunAsync("init", "--storage", "local", "--path", NewStore);

        if (defaultsExist)
        {
            exitCode.Should().Be(0);
        }
        else
        {
            exitCode.Should().Be(3);
            File.ReadAllBytes(AppPaths.ConfigFile).Should().Equal(before);
        }
    }

    [Fact]
    public async Task Init_S3_TakesSecretsFromStdin_AndProtectsThem()
    {
        WritePristineConfig();

        var (exitCode, stdout, stderr) = await RunWithStdinAsync(
            "s3-init-secret\n",
            "init", "--storage", "s3", "--option", "region=eu-west-1", "--option", "bucketName=my-backups", "--option", "accessKeyId=AKIATEST",
            "--option-stdin", "secretAccessKey", "--protect", "--watch", WatchedDirectory, "--json");

        exitCode.Should().Be(0, stdout + stderr);
        var config = ReadConfig();
        config["globalStorageType"]!.GetValue<string>().Should().Be("s3");
        config["storageSources"]!["s3"]!["options"]!["secretAccessKey"]!.GetValue<string>().Should().StartWith("dpapi:");
        (stdout + stderr).Should().NotContain("s3-init-secret");
        Json(stdout)["nextSteps"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Contain("restore.exe probe --storage s3");
    }

    [Fact]
    public async Task Init_Encrypt_WithPasswordStdin_EnablesEncryption_AndTheStdinOrderIsOptionsThenPassword()
    {
        WritePristineConfig();

        var (exitCode, stdout, stderr) = await RunWithStdinAsync(
            $"{Password}\n",
            "init", "--storage", "local", "--path", NewStore, "--watch", WatchedDirectory, "--encrypt", "--password-stdin", "--json");

        exitCode.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        doc["encryption"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        doc["notes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(n => n.Contains("export-key"));
        doc["nextSteps"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Contain("restore.exe unlock --remember");
        stdout.Should().NotContain(Password);

        var encryption = ReadConfig()["encryption"]!;
        encryption["enabled"]!.GetValue<bool>().Should().BeTrue();
        var salt = Convert.FromBase64String(encryption["salt"]!.GetValue<string>());
        new EncryptionService(new TestLogger()).VerifyPassword(
            Password, salt, encryption["verificationToken"]!.GetValue<string>(), encryption["keyDerivationIterations"]!.GetValue<int>())
            .Should().BeTrue();
    }

    [Fact]
    public async Task Init_Encrypt_WithoutAPasswordSource_ExitsFour_AndWritesNothing()
    {
        WritePristineConfig();
        var before = File.ReadAllBytes(AppPaths.ConfigFile);

        var (exitCode, stdout, _) = await RunAsync("init", "--storage", "local", "--path", NewStore, "--watch", WatchedDirectory, "--encrypt", "--json");

        exitCode.Should().Be(4);
        var error = Json(stdout)["error"]!;
        error["code"]!.GetValue<string>().Should().Be("needs_user");
        error["hint"]!.GetValue<string>().Should().Contain("--password-stdin");
        File.ReadAllBytes(AppPaths.ConfigFile).Should().Equal(before);
    }

    [Fact]
    public async Task Init_Encrypt_RejectsShortPasswords_AndTakesThePasswordFromTheEnvironment()
    {
        WritePristineConfig();
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "short");
        (await RunAsync("init", "--storage", "local", "--path", NewStore, "--watch", WatchedDirectory, "--encrypt")).ExitCode.Should().Be(2);
        ReadConfig()["encryption"]!["enabled"]!.GetValue<bool>().Should().BeFalse();

        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "weakpass");
        var (exitCode, stdout, _) = await RunAsync("init", "--storage", "local", "--path", NewStore, "--watch", WatchedDirectory, "--encrypt", "--json");
        exitCode.Should().Be(0);
        Json(stdout)["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().Contain(w => w.Contains("weak"));
    }

    [Fact]
    public async Task Init_Probe_RunsARealRoundTripAgainstTheStorage()
    {
        WritePristineConfig();

        var (exitCode, stdout, stderr) = await RunAsync("init", "--storage", "local", "--path", NewStore, "--watch", WatchedDirectory, "--probe", "--json");

        exitCode.Should().Be(0, stdout + stderr);
        var probe = Json(stdout)["probe"]!;
        probe["success"]!.GetValue<bool>().Should().BeTrue();
        probe["steps"]!.AsArray().Should().NotBeEmpty();
        Json(stdout)["nextSteps"]!.AsArray().Select(s => s!.GetValue<string>()).Should().NotContain(s => s.Contains("probe"));

        var (_, text, _) = await RunAsync("init", "--storage", "local", "--path", NewStore, "--watch", WatchedDirectory, "--probe", "--force");
        text.Should().Contain("Storage probe: passed");
    }

    // ---- encryption ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EncryptionStatus_ReportsTheState()
    {
        WriteConfig();

        var (exitCode, stdout, _) = await RunAsync("encryption", "status", "--json");

        exitCode.Should().Be(0);
        var doc = Json(stdout);
        doc["enabled"]!.GetValue<bool>().Should().BeFalse();
        doc["keyDerivationIterations"]!.GetValue<int>().Should().Be(1000);
        doc["saltPresent"]!.GetValue<bool>().Should().BeFalse();
        doc["verificationTokenPresent"]!.GetValue<bool>().Should().BeFalse();
        doc["rememberedPasswordPresent"]!.GetValue<bool>().Should().BeFalse();
        doc["rememberedPasswordSupported"]!.GetValue<bool>().Should().BeTrue("the test protector is available");

        (await RunAsync("encryption", "status")).Stdout.Should().Contain("Encryption: disabled");
    }

    [Fact]
    public async Task EncryptionEnable_SetsSaltAndToken_PrintsTheGuidance_AndRefusesASecondTime()
    {
        WriteConfig();

        var (exitCode, stdout, stderr) = await RunWithStdinAsync($"{Password}\n", "encryption", "enable", "--password-stdin");

        exitCode.Should().Be(0, stdout + stderr);
        stdout.Should().Contain("enabled").And.Contain("Store your password securely").And.Contain("export-key");
        stdout.Should().NotContain(Password);
        var status = Json((await RunAsync("encryption", "status", "--json")).Stdout);
        status["enabled"]!.GetValue<bool>().Should().BeTrue();
        status["saltPresent"]!.GetValue<bool>().Should().BeTrue();
        status["verificationTokenPresent"]!.GetValue<bool>().Should().BeTrue();
        status["keyDerivationIterations"]!.GetValue<int>().Should().Be(1000, "the configured iteration count is kept");

        var (again, againOut, _) = await RunWithStdinAsync($"{Password}\n", "encryption", "enable", "--password-stdin", "--json");
        again.Should().Be(2);
        Json(againOut)["error"]!["message"]!.GetValue<string>().Should().Contain("already enabled");
    }

    [Fact]
    public async Task EncryptionEnable_AppliesTheGuiPasswordRules()
    {
        WriteConfig();

        (await RunWithStdinAsync("1234567\n", "encryption", "enable", "--password-stdin")).ExitCode.Should().Be(2, "fewer than 8 characters");
        ReadConfig()["encryption"]!["enabled"]!.GetValue<bool>().Should().BeFalse();

        var (nothing, json, _) = await RunAsync("encryption", "enable", "--json");
        nothing.Should().Be(4);
        Json(json)["error"]!["code"]!.GetValue<string>().Should().Be("needs_user");

        var (weak, weakOut, _) = await RunWithStdinAsync("abcdefgh\n", "encryption", "enable", "--password-stdin", "--json");
        weak.Should().Be(0);
        Json(weakOut)["passwordStrength"]!["label"]!.GetValue<string>().Should().Be("Weak");
        Json(weakOut)["warnings"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public void PasswordStrength_MatchesTheEncryptionSetupWindow()
    {
        PasswordStrength.Evaluate("abcdefgh").Label.Should().Be("Weak");
        PasswordStrength.Evaluate("Abcdefg1").Label.Should().Be("Medium");
        PasswordStrength.Evaluate("Abcdefghijk1!").Label.Should().Be("Strong");
        PasswordStrength.Evaluate("abc").Score.Should().Be(1);
    }

    [Fact]
    public async Task EncryptionDisable_NeedsYes_ThenTurnsNewBackupsUnencrypted_KeepingSaltAndToken()
    {
        WriteConfig();
        (await RunWithStdinAsync($"{Password}\n", "encryption", "enable", "--password-stdin")).ExitCode.Should().Be(0);

        var (refused, refusedOut, _) = await RunAsync("encryption", "disable", "--json");
        refused.Should().Be(4);
        Json(refusedOut)["error"]!["hint"]!.GetValue<string>().Should().Contain("--yes");
        ReadConfig()["encryption"]!["enabled"]!.GetValue<bool>().Should().BeTrue();

        var (exitCode, stdout, _) = await RunAsync("encryption", "disable", "--yes");
        exitCode.Should().Be(0);
        stdout.Should().Contain("New backups will not be encrypted").And.Contain("still require the password");
        var encryption = ReadConfig()["encryption"]!;
        encryption["enabled"]!.GetValue<bool>().Should().BeFalse();
        encryption["salt"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        encryption["verificationToken"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        var (again, againOut, _) = await RunAsync("encryption", "disable", "--json");
        again.Should().Be(0);
        Json(againOut)["changed"]!.GetValue<bool>().Should().BeFalse();
    }

    // ---- unlock / lock ---------------------------------------------------------------------------------------------

    private async Task EnableEncryptionAsync()
    {
        WriteConfig();
        (await RunWithStdinAsync($"{Password}\n", "encryption", "enable", "--password-stdin")).ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Unlock_VerifiesThePassword_RightWrongAndNone()
    {
        await EnableEncryptionAsync();

        var (ok, okOut, _) = await RunWithStdinAsync($"{Password}\n", "unlock", "--password-stdin", "--json");
        ok.Should().Be(0);
        Json(okOut)["unlocked"]!.GetValue<bool>().Should().BeTrue();
        Json(okOut)["remembered"]!.GetValue<bool>().Should().BeFalse();
        RememberedPassword.Exists.Should().BeFalse();

        var (wrong, wrongOut, _) = await RunWithStdinAsync("not the password\n", "unlock", "--password-stdin", "--json");
        wrong.Should().Be(1);
        Json(wrongOut)["error"]!["message"]!.GetValue<string>().Should().Contain("Incorrect password");

        var (none, noneOut, _) = await RunAsync("unlock", "--json");
        none.Should().Be(4);
        Json(noneOut)["error"]!["code"]!.GetValue<string>().Should().Be("needs_user");

        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", Password);
        (await RunAsync("unlock")).ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Unlock_WhenEncryptionIsOff_IsAUsageError()
    {
        WriteConfig();

        var (exitCode, _, stderr) = await RunAsync("unlock");

        exitCode.Should().Be(2);
        stderr.Should().Contain("not enabled");
    }

    [Fact]
    public async Task UnlockRemember_StoresThePasswordProtected_AndLaterCommandsUseIt()
    {
        await EnableEncryptionAsync();

        var (exitCode, stdout, _) = await RunWithStdinAsync($"{Password}\n", "unlock", "--password-stdin", "--remember", "--json");

        exitCode.Should().Be(0);
        Json(stdout)["remembered"]!.GetValue<bool>().Should().BeTrue();
        var file = File.ReadAllBytes(RememberedPassword.FilePath);
        file.Should().Equal(Encoding.UTF8.GetBytes(Password).Reverse(), "the fake protector reverses the bytes; the file is not the plain password");
        Encoding.UTF8.GetString(file).Should().NotContain(Password);
        Json((await RunAsync("encryption", "status", "--json")).Stdout)["rememberedPasswordPresent"]!.GetValue<bool>().Should().BeTrue();

        // A later command without any other source: the unattended password check, the chain and unlock itself all find it.
        var config = new ConfigManager(new TestLogger());
        await config.LoadAsync();
        Program.GetServicePasswordError(config).Should().BeNull();
        (await Program.PromptForPasswordAsync(new PasswordPromptRequest(PasswordPurpose.Decrypt, false), allowPrompt: false, config)).Should().Be(Password);
        (await RunAsync("unlock")).ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task RememberedPassword_IsNotUsedWithoutAConfig_OrForOtherSources()
    {
        await EnableEncryptionAsync();
        (await RunWithStdinAsync($"{Password}\n", "unlock", "--password-stdin", "--remember")).ExitCode.Should().Be(0);

        // Setting up a new password must never pick up the remembered one.
        CliPasswordSources.TryGetConfiguredPassword().Should().BeNull();
        (await RunAsync("encryption", "enable")).ExitCode.Should().BeOneOf(2, 4);

        // The environment wins over the remembered password.
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "from-the-environment");
        var config = new ConfigManager(new TestLogger());
        await config.LoadAsync();
        CliPasswordSources.TryGetConfiguredPassword(config).Should().Be("from-the-environment");
    }

    [Fact]
    public async Task RememberedPassword_ThatNoLongerMatches_IsIgnoredWithAWarning()
    {
        await EnableEncryptionAsync();
        RememberedPassword.Save("an old password", CliServices.SecretProtector);

        var (exitCode, stdout, stderr) = await RunAsync("unlock", "--json");

        exitCode.Should().Be(4, "the stale remembered password is not a usable source");
        stderr.Should().Contain("remembered encryption password no longer matches").And.Contain("unlock --remember");
        Json(stdout)["error"]!["code"]!.GetValue<string>().Should().Be("needs_user");

        // The right password re-remembers over the stale one.
        (await RunWithStdinAsync($"{Password}\n", "unlock", "--password-stdin", "--remember")).ExitCode.Should().Be(0);
        (await RunAsync("unlock")).ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task UnlockRemember_WithoutDpapi_FailsClearly()
    {
        await EnableEncryptionAsync();
        CliServices.SecretProtector = new FakeSecretProtector(available: false);

        var (exitCode, _, stderr) = await RunWithStdinAsync($"{Password}\n", "unlock", "--password-stdin", "--remember");

        exitCode.Should().Be(1);
        stderr.Should().Contain("DPAPI");
        RememberedPassword.Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Lock_ForgetsTheRememberedPassword()
    {
        await EnableEncryptionAsync();
        (await RunWithStdinAsync($"{Password}\n", "unlock", "--password-stdin", "--remember")).ExitCode.Should().Be(0);

        var (exitCode, stdout, _) = await RunAsync("lock", "--json");

        exitCode.Should().Be(0);
        Json(stdout)["wasRemembered"]!.GetValue<bool>().Should().BeTrue();
        RememberedPassword.Exists.Should().BeFalse();
        Json((await RunAsync("lock", "--json")).Stdout)["wasRemembered"]!.GetValue<bool>().Should().BeFalse();
        (await RunAsync("unlock")).ExitCode.Should().Be(4);
    }

    [Fact]
    public async Task ChangePassword_UpdatesTheRememberedPassword()
    {
        await EnableEncryptionAsync();
        await File.WriteAllTextAsync(Path.Combine(WatchedDirectory, "doc.txt"), "document " + new string('d', 2000));
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", Password);
        var (backupExit, backupOut, backupErr) = await RunAsync("backup", WatchedDirectory, "--json");
        backupExit.Should().Be(0, backupOut + backupErr);
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);
        (await RunWithStdinAsync($"{Password}\n", "unlock", "--password-stdin", "--remember")).ExitCode.Should().Be(0);

        // The current password comes from the remembered one; the new one from the environment.
        Environment.SetEnvironmentVariable("RESTORE_NEW_ENCRYPTION_PASSWORD", "a brand new password 7");
        var (exitCode, stdout, stderr) = await RunAsync("change-password", "--json");
        Environment.SetEnvironmentVariable("RESTORE_NEW_ENCRYPTION_PASSWORD", null);

        exitCode.Should().Be(0, stdout + stderr);
        RememberedPassword.TryLoad(CliServices.SecretProtector).Should().Be("a brand new password 7");
        (await RunAsync("unlock")).ExitCode.Should().Be(0, "the remembered password was updated to the new one");
    }

    private async Task<(int Exit, string Out, string Err)> RunRecoveryKeyAsync(params string[] extra)
    {
        await EnableEncryptionAsync();
        await File.WriteAllTextAsync(Path.Combine(WatchedDirectory, "doc.txt"), "document " + new string('d', 2000));
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", Password);
        try
        {
            var (backupExit, backupOut, backupErr) = await RunAsync("backup", WatchedDirectory, "--json");
            backupExit.Should().Be(0, backupOut + backupErr);
            return await RunAsync(["recovery-key", .. extra]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);
        }
    }

    [Fact]
    public async Task RecoveryKey_PrintsTheKeyToStdout_AndTheWarningToStderr()
    {
        var (exit, stdout, stderr) = await RunRecoveryKeyAsync();

        exit.Should().Be(0, stdout + stderr);
        stdout.Trim().Should().NotBeNullOrEmpty().And.NotContain("shown once", "stdout carries only the key");
        stderr.Should().Contain("shown once");
    }

    [Fact]
    public async Task RecoveryKey_Quiet_StillPrintsTheKey()
    {
        var (exit, stdout, stderr) = await RunRecoveryKeyAsync("--quiet");

        exit.Should().Be(0, stdout + stderr);
        stdout.Trim().Should().NotBeNullOrEmpty("the key exists nowhere else; --quiet must not swallow it");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryKey_Json_HasTheKey_AlsoWithQuiet(bool quiet)
    {
        var (exit, stdout, stderr) = await RunRecoveryKeyAsync(quiet ? ["--json", "--quiet"] : ["--json"]);

        exit.Should().Be(0, stdout + stderr);
        Json(stdout)["recoveryKey"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task EncryptionEnable_ClearsAStaleRememberedPassword()
    {
        WriteConfig();
        RememberedPassword.Save("left over", CliServices.SecretProtector);

        (await RunWithStdinAsync($"{Password}\n", "encryption", "enable", "--password-stdin")).ExitCode.Should().Be(0);

        RememberedPassword.Exists.Should().BeFalse();
    }

    // ---- auth ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AuthStatus_ReportsConfiguredAndTokenPresence_WithoutTouchingTheNetwork()
    {
        var tokenFolder = Path.Combine(Root, "tokens");
        WriteConfig(root => root["storageSources"] = new JsonObject
        {
            ["local"] = new JsonObject { ["path"] = StorageDirectory },
            ["gdrive"] = new JsonObject { ["options"] = new JsonObject { ["client_id"] = "id", ["client_secret"] = "secret", ["token_folder"] = tokenFolder } },
            ["dropbox"] = new JsonObject { ["options"] = new JsonObject { ["appKey"] = "key", ["appSecret"] = "secret" } }
        });

        var (_, before, _) = await RunAsync("auth", "status", "--json");
        var drive = Json(before)["providers"]!.AsArray().Single(p => p!["provider"]!.GetValue<string>() == "gdrive")!;
        drive["configured"]!.GetValue<bool>().Should().BeTrue();
        drive["tokenPresent"]!.GetValue<bool>().Should().BeFalse();
        var dropbox = Json(before)["providers"]!.AsArray().Single(p => p!["provider"]!.GetValue<string>() == "dropbox")!;
        dropbox["configured"]!.GetValue<bool>().Should().BeTrue();
        dropbox["tokenPresent"]!.GetValue<bool>().Should().BeFalse();

        Directory.CreateDirectory(tokenFolder);
        File.WriteAllText(Path.Combine(tokenFolder, "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user"), "{}");
        (await RunAsync("config", "set", "storageSources.dropbox.options.refreshToken", "refresh-1")).ExitCode.Should().Be(0);

        var (text, after, _) = await RunAsync("auth", "status");
        text.Should().Be(0);
        after.Should().Contain("gdrive: configured, signed in").And.Contain("dropbox: configured, signed in");
    }

    [Fact]
    public async Task AuthStatus_TreatsExamplePlaceholdersAsNotConfigured()
    {
        WritePristineConfig();

        var (exitCode, stdout, _) = await RunAsync("auth", "status", "--json");

        exitCode.Should().Be(0);
        Json(stdout)["providers"]!.AsArray().Should().OnlyContain(p => !p!["configured"]!.GetValue<bool>() && !p["tokenPresent"]!.GetValue<bool>());
    }

    private void ConfigureDrive() => WriteConfig(root => root["storageSources"] = new JsonObject
    {
        ["local"] = new JsonObject { ["path"] = StorageDirectory },
        ["gdrive"] = new JsonObject { ["options"] = new JsonObject { ["client_id"] = "the-id", ["client_secret"] = "the-secret", ["token_folder"] = Path.Combine(Root, "tokens") } }
    });

    [Fact]
    public async Task AuthGdrive_NonInteractive_ExitsFourWithInstructions_AndDoesNotSignIn()
    {
        ConfigureDrive();

        var (exitCode, stdout, _) = await RunAsync("auth", "gdrive", "--json");

        exitCode.Should().Be(4);
        var error = Json(stdout)["error"]!;
        error["code"]!.GetValue<string>().Should().Be("needs_user");
        error["hint"]!.GetValue<string>().Should().Contain("--no-browser");
    }

    [Fact]
    public async Task AuthGdrive_NoBrowser_RunsTheSignInWithTheConfiguredCredentials()
    {
        ConfigureDrive();
        DriveSignInRequest? seen = null;
        CliServices.DriveSignIn = (request, _) =>
        {
            seen = request;
            request.ShowMessage("Open https://example.invalid/auth");
            return Task.CompletedTask;
        };

        var (exitCode, stdout, stderr) = await RunAsync("auth", "gdrive", "--no-browser", "--json");

        exitCode.Should().Be(0, stdout + stderr);
        seen.Should().NotBeNull();
        seen!.ClientId.Should().Be("the-id");
        seen.ClientSecret.Should().Be("the-secret");
        seen.NoBrowser.Should().BeTrue();
        seen.TokenFolder.Should().Be(Path.Combine(Root, "tokens"));
        stderr.Should().Contain("Open https://example.invalid/auth", "the URL goes to stderr so stdout stays one JSON document");
        Json(stdout)["signedIn"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task AuthGdrive_ReportsAnUnconfiguredStorage_AndTimeouts()
    {
        WriteConfig();
        var (notConfigured, _, _) = await RunAsync("auth", "gdrive", "--no-browser");
        notConfigured.Should().Be(3);

        ConfigureDrive();
        CliServices.DriveSignIn = (_, _) => throw new TimeoutException("Google Drive sign-in was not completed within 5 minutes.");
        var (timedOut, json, _) = await RunAsync("auth", "gdrive", "--no-browser", "--json");
        timedOut.Should().Be(4);
        Json(json)["error"]!["code"]!.GetValue<string>().Should().Be("needs_user");

        CliServices.DriveSignIn = (_, _) => throw new InvalidOperationException("access_denied");
        (await RunAsync("auth", "gdrive", "--no-browser")).ExitCode.Should().Be(1);
    }

    private void ConfigureDropbox(bool withSecret = true) => WriteConfig(root => root["storageSources"] = new JsonObject
    {
        ["local"] = new JsonObject { ["path"] = StorageDirectory },
        ["dropbox"] = new JsonObject
        {
            ["options"] = withSecret
                ? new JsonObject { ["appKey"] = "the-app-key", ["appSecret"] = "the-app-secret" }
                : new JsonObject { ["appKey"] = "the-app-key" }
        }
    });

    private static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task AuthDropbox_Step1_PrintsAnOfflinePkceUrl_AndRemembersTheVerifier()
    {
        ConfigureDropbox();

        var (exitCode, stdout, stderr) = await RunAsync("auth", "dropbox", "--json");

        exitCode.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        var url = new Uri(doc["authorizeUrl"]!.GetValue<string>());
        url.Host.Should().Be("www.dropbox.com");
        url.AbsolutePath.Should().Be("/oauth2/authorize");
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        query["client_id"].Should().Be("the-app-key");
        query["response_type"].Should().Be("code");
        query["token_access_type"].Should().Be("offline");
        query["code_challenge_method"].Should().Be("S256");
        query["redirect_uri"].Should().BeNull("the code is shown to the person instead");
        doc["nextStep"]!.GetValue<string>().Should().Contain("--code");

        var state = Json(File.ReadAllText(Path.Combine(AppPaths.StateDirectory, AuthCommands.DropboxStateFile)));
        Challenge(state["codeVerifier"]!.GetValue<string>()).Should().Be(query["code_challenge"]);
        state["appKey"]!.GetValue<string>().Should().Be("the-app-key");
        stdout.Should().NotContain(state["codeVerifier"]!.GetValue<string>(), "the verifier never leaves the state folder");
    }

    [Fact]
    public async Task AuthDropbox_Step2_ExchangesTheCode_AndStoresAProtectedRefreshToken()
    {
        ConfigureDropbox();
        var step1 = Json((await RunAsync("auth", "dropbox", "--json")).Stdout);
        var verifier = Json(File.ReadAllText(Path.Combine(AppPaths.StateDirectory, AuthCommands.DropboxStateFile)))["codeVerifier"]!.GetValue<string>();
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":\"sl.access\",\"token_type\":\"bearer\",\"refresh_token\":\"the-refresh-token\",\"expires_in\":14400}")
        });
        CliServices.HttpHandler = handler;

        var (exitCode, stdout, stderr) = await RunAsync("auth", "dropbox", "--code", "THE-CODE", "--json");

        exitCode.Should().Be(0, stdout + stderr);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri!.ToString().Should().Be("https://api.dropboxapi.com/oauth2/token");
        var form = System.Web.HttpUtility.ParseQueryString(handler.Requests[0].Body!);
        form["code"].Should().Be("THE-CODE");
        form["grant_type"].Should().Be("authorization_code");
        form["client_id"].Should().Be("the-app-key");
        form["code_verifier"].Should().Be(verifier);
        Challenge(form["code_verifier"]!).Should().Be(System.Web.HttpUtility.ParseQueryString(new Uri(step1["authorizeUrl"]!.GetValue<string>()).Query)["code_challenge"]);

        var options = ReadConfig()["storageSources"]!["dropbox"]!["options"]!;
        options["refreshToken"]!.GetValue<string>().Should().StartWith("dpapi:");
        SecretReferences.Resolve("refreshToken", options["refreshToken"]!.GetValue<string>(), CliServices.SecretProtector).Should().Be("the-refresh-token");
        options["appKey"]!.GetValue<string>().Should().Be("the-app-key");
        options["appSecret"]!.GetValue<string>().Should().Be("the-app-secret");
        (stdout + stderr).Should().NotContain("the-refresh-token").And.NotContain("sl.access");
        Json(stdout)["refreshTokenStored"]!.GetValue<bool>().Should().BeTrue();
        File.Exists(Path.Combine(AppPaths.StateDirectory, AuthCommands.DropboxStateFile)).Should().BeFalse("the verifier is single-use");

        (await RunAsync("auth", "dropbox", "--code", "X")).ExitCode.Should().Be(2, "nothing is pending any more");
    }

    [Fact]
    public async Task AuthDropbox_Step2_WithoutProtection_StoresThePlainTokenWithAWarning()
    {
        ConfigureDropbox(withSecret: false);
        await RunAsync("auth", "dropbox");
        CliServices.SecretProtector = new FakeSecretProtector(available: false);
        CliServices.HttpHandler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"refresh_token\":\"plain-refresh\"}") });

        var (exitCode, stdout, _) = await RunAsync("auth", "dropbox", "--code", "C", "--json");

        exitCode.Should().Be(0);
        ReadConfig()["storageSources"]!["dropbox"]!["options"]!["refreshToken"]!.GetValue<string>().Should().Be("plain-refresh");
        var warnings = Json(stdout)["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        warnings.Should().Contain(w => w.Contains("plain text")).And.Contain(w => w.Contains("appSecret"));
    }

    [Fact]
    public async Task AuthDropbox_RefusedCodes_AndMisuse_AreReported()
    {
        ConfigureDropbox();

        (await RunAsync("auth", "dropbox", "--code", "C")).ExitCode.Should().Be(2, "no step 1 yet");

        await RunAsync("auth", "dropbox");
        CliServices.HttpHandler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"code has expired\"}")
        });
        var (refused, json, stderr) = await RunAsync("auth", "dropbox", "--code", "OLD", "--json");
        refused.Should().Be(1);
        Json(json)["error"]!["message"]!.GetValue<string>().Should().Contain("code has expired");
        stderr.Should().Contain("code has expired");
        ReadConfig()["storageSources"]!["dropbox"]!["options"]!.AsObject().ContainsKey("refreshToken").Should().BeFalse();

        CliServices.HttpHandler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"only\"}") });
        (await RunAsync("auth", "dropbox", "--code", "C")).ExitCode.Should().Be(1);

        WriteConfig();
        (await RunAsync("auth", "dropbox")).ExitCode.Should().Be(3);
        WritePristineConfig();
        File.Delete(AppPaths.ConfigFile);
        WritePristineConfig();
        (await RunAsync("auth", "dropbox")).ExitCode.Should().Be(3, "the example's placeholder app key is not an app key");
    }

    [Fact]
    public async Task AuthDropbox_Step2_FailsWhenTheAppKeyChangedSinceStep1()
    {
        ConfigureDropbox();
        await RunAsync("auth", "dropbox");
        (await RunAsync("config", "set", "storageSources.dropbox.options.appKey", "another-key")).ExitCode.Should().Be(0);

        var (exitCode, _, stderr) = await RunAsync("auth", "dropbox", "--code", "C");

        exitCode.Should().Be(2);
        stderr.Should().Contain("different app key");
    }
}
