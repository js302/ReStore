using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Google.Apis.Auth.OAuth2.Responses;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.google;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>dpapi:/env: secret references (P6-D): resolved only into the provider copy, never into the model or config.json.</summary>
[Collection("GlobalState")]
public class SecretReferencesTests : IDisposable
{
    private const string Variable = "RESTORE_TEST_SECRET_REF";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreSecretTests_" + Guid.NewGuid().ToString("N"));

    public SecretReferencesTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(Variable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Reversible stand-in for DPAPI: XOR with a key, so a "different user" key fails the check byte.</summary>
    private sealed class FakeProtector(byte key = 0x5A, bool available = true) : ISecretProtector
    {
        public bool IsAvailable => available;

        public byte[] Protect(byte[] plaintext) => [key, .. plaintext.Select(b => (byte)(b ^ key))];

        public byte[] Unprotect(byte[] protectedData)
        {
            if (protectedData.Length == 0 || protectedData[0] != key)
            {
                throw new CryptographicException("wrong user");
            }

            return protectedData.Skip(1).Select(b => (byte)(b ^ key)).ToArray();
        }
    }

    private static StorageConfig S3(string secret) => new()
    {
        Path = "s3://b",
        Options = new Dictionary<string, string> { ["accessKeyId"] = "AKIA", ["secretAccessKey"] = secret }
    };

    [Fact]
    public void BuildProviderConfig_ResolvesEnvReference_ModelKeepsReference()
    {
        Environment.SetEnvironmentVariable(Variable, "from-env");
        var config = S3("env:" + Variable);

        var built = ConfigManager.BuildProviderConfig(config);

        built.Options["secretAccessKey"].Should().Be("from-env");
        config.Options["secretAccessKey"].Should().Be("env:" + Variable);
    }

    [Fact]
    public void BuildProviderConfig_MissingEnvVariable_ThrowsAuthRequiredNamingOptionNotValue()
    {
        var act = () => ConfigManager.BuildProviderConfig(S3("env:" + Variable));

        act.Should().Throw<StorageAuthRequiredException>()
            .Which.Message.Should().Contain("secretAccessKey").And.Contain(Variable);
    }

    [Fact]
    public void BuildProviderConfig_ResolvesDpapiReference_WithInjectedProtector()
    {
        var protector = new FakeProtector();
        var reference = SecretReferences.Protect("p@ss", protector);

        reference.Should().StartWith("dpapi:").And.NotContain("p@ss");
        ConfigManager.BuildProviderConfig(S3(reference), protector).Options["secretAccessKey"].Should().Be("p@ss");
    }

    [Fact]
    public void Resolve_BlobFromAnotherUser_ThrowsClearErrorWithoutSecret()
    {
        var reference = SecretReferences.Protect("topsecret", new FakeProtector(key: 1));

        var act = () => SecretReferences.Resolve("password", reference, new FakeProtector(key: 2));

        var message = act.Should().Throw<StorageAuthRequiredException>().Which.Message;
        message.Should().Contain("'password'").And.Contain("different Windows user").And.NotContain("topsecret");
    }

    [Fact]
    public void Resolve_InvalidBase64_ThrowsAuthRequired()
    {
        var act = () => SecretReferences.Resolve("token", "dpapi:***not-base64***", new FakeProtector());

        act.Should().Throw<StorageAuthRequiredException>().Which.Message.Should().Contain("'token'");
    }

    [Fact]
    public void Resolve_DpapiWhenUnavailable_ThrowsPlatformMessage()
    {
        var act = () => SecretReferences.Resolve("token", "dpapi:AAAA", new FakeProtector(available: false));

        act.Should().Throw<StorageAuthRequiredException>().Which.Message.Should().Contain("not supported on this platform");
    }

    [Fact]
    public void Protect_WhenUnavailable_ThrowsPlatformNotSupported()
    {
        var act = () => SecretReferences.Protect("x", new FakeProtector(available: false));

        act.Should().Throw<PlatformNotSupportedException>();
    }

    [Theory]
    [InlineData("plain-value")]
    [InlineData("env:")]
    [InlineData("env:not valid")]
    [InlineData("%SOME_VAR%")]
    public void Resolve_PlainValues_PassThrough(string value)
    {
        SecretReferences.Resolve("password", value, new FakeProtector()).Should().Be(value);
    }

    [Fact]
    public void ProtectSecretOptions_EncryptsOnlyLiteralSecrets()
    {
        var options = new Dictionary<string, string>
        {
            ["accessKeyId"] = "AKIA",
            ["secretAccessKey"] = "literal",
            ["password"] = "env:PW",
            ["token"] = "%TOKEN%",
            ["passphrase"] = "",
            ["client_secret"] = "your_client_secret",
            ["sessionToken"] = SecretReferences.Protect("already", new FakeProtector())
        };
        var before = options["sessionToken"];

        var changed = SecretReferences.ProtectSecretOptions(options, new FakeProtector());

        changed.Should().Equal("secretAccessKey");
        options["secretAccessKey"].Should().StartWith("dpapi:");
        options["accessKeyId"].Should().Be("AKIA");
        options["password"].Should().Be("env:PW");
        options["token"].Should().Be("%TOKEN%");
        options["client_secret"].Should().Be("your_client_secret");
        options["sessionToken"].Should().Be(before);
    }

    private const string Config = """
    {
      "configSchemaVersion": 4,
      "futureTopLevel": 1,
      "watchDirectories": [],
      "globalStorageType": "s3",
      "backupInterval": "01:00:00",
      "sizeThresholdMB": 500,
      "storageSources": {
        "s3": { "path": "s3://b", "futureKey": 7, "options": { "accessKeyId": "AKIA", "secretAccessKey": "literal-secret", "region": "eu", "extra": "keep" } },
        "sftp": { "options": { "host": "h", "password": "env:SFTP_PW", "passphrase": "dpapi:AAECAw==", "privateKeyPath": "k" } }
      }
    }
    """;

    [Fact]
    public void RedactConfigJson_MasksLiteralSecrets_KeepsReferencesAndOtherValues()
    {
        var redacted = ConfigSecretsCommands.RedactConfigJson(Config);

        redacted.Should().NotContain("literal-secret").And.NotContain("AAECAw==");
        var root = JsonNode.Parse(redacted)!;
        var s3 = root["storageSources"]!["s3"]!["options"]!;
        s3["secretAccessKey"]!.GetValue<string>().Should().Be("***");
        s3["accessKeyId"]!.GetValue<string>().Should().Be("AKIA");
        s3["extra"]!.GetValue<string>().Should().Be("keep");
        var sftp = root["storageSources"]!["sftp"]!["options"]!;
        sftp["password"]!.GetValue<string>().Should().Be("env:SFTP_PW");
        sftp["passphrase"]!.GetValue<string>().Should().Be("dpapi:…");
        root["futureTopLevel"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task ProtectAsync_RewritesPlainSecrets_AndRoundTripsReferencesAndUnknownMembers()
    {
        var previousRoot = AppPaths.Root;
        AppPaths.OverrideRoot(_root);
        try
        {
            var manager = new ConfigManager(new TestLogger());
            File.WriteAllText(manager.GetConfigFilePath(), Config);
            await manager.LoadAsync();
            var protector = new FakeProtector();

            var changed = await ConfigSecretsCommands.ProtectAsync(manager, protector);

            changed.Should().Equal("s3.secretAccessKey");
            var saved = JsonNode.Parse(File.ReadAllText(manager.GetConfigFilePath()))!;
            var s3 = saved["storageSources"]!["s3"]!;
            s3["options"]!["secretAccessKey"]!.GetValue<string>().Should().StartWith("dpapi:");
            s3["options"]!["extra"]!.GetValue<string>().Should().Be("keep");
            s3["futureKey"]!.GetValue<int>().Should().Be(7);
            saved["futureTopLevel"]!.GetValue<int>().Should().Be(1);
            saved["storageSources"]!["sftp"]!["options"]!["password"]!.GetValue<string>().Should().Be("env:SFTP_PW");
            File.ReadAllText(manager.GetConfigFilePath()).Should().NotContain("literal-secret");

            // Reloading keeps the reference and it still resolves to the original value.
            var reloaded = new ConfigManager(new TestLogger());
            await reloaded.LoadAsync();
            ConfigManager.BuildProviderConfig(reloaded.StorageSources["s3"], protector)
                .Options["secretAccessKey"].Should().Be("literal-secret");

            // Second run is a no-op.
            (await ConfigSecretsCommands.ProtectAsync(reloaded, protector)).Should().BeEmpty();
        }
        finally
        {
            AppPaths.OverrideRoot(previousRoot);
        }
    }

    [Fact]
    public async Task DpapiDataStore_RoundTrips_EncryptsOnDisk_AndMigratesLegacyTokens()
    {
        var protector = new FakeProtector();
        var legacy = new Google.Apis.Util.Store.FileDataStore(Path.Combine(_root, "legacy"), fullPath: true);
        await legacy.StoreAsync("user", new TokenResponse { AccessToken = "ACCESS-123", RefreshToken = "REFRESH-456" });
        var folder = Path.Combine(_root, "tokens");
        var store = new DpapiDataStore(folder, protector, legacy);

        // First read migrates from the legacy plaintext store.
        var migrated = await store.GetAsync<TokenResponse>("user");
        migrated.RefreshToken.Should().Be("REFRESH-456");
        (await legacy.GetAsync<TokenResponse>("user")).Should().BeNull("the plaintext copy is removed after migration");

        var onDisk = string.Concat(Directory.GetFiles(folder).Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))));
        onDisk.Should().NotContain("REFRESH-456").And.NotContain("ACCESS-123");

        await store.StoreAsync("user", new TokenResponse { AccessToken = "NEW", RefreshToken = "R2" });
        (await new DpapiDataStore(folder, protector).GetAsync<TokenResponse>("user")).AccessToken.Should().Be("NEW");

        // A different user cannot read it: interactive behaves as "no token" (consent runs again) and warns.
        var log = new TestLogger();
        (await new DpapiDataStore(folder, new FakeProtector(key: 9), null, log, () => false).GetAsync<TokenResponse>("user")).Should().BeNull();

        // Unattended: fail fast with an actionable message instead of waiting for interactive consent.
        var unattended = async () => await new DpapiDataStore(folder, new FakeProtector(key: 9), null, log, () => true).GetAsync<TokenResponse>("user");
        (await unattended.Should().ThrowAsync<StorageAuthRequiredException>()).Which.Message.Should().Contain("different Windows user");

        await store.DeleteAsync<TokenResponse>("user");
        (await store.GetAsync<TokenResponse>("user")).Should().BeNull();
    }

    /// <summary>Option names that look sensitive, so a newly added provider field cannot silently stay plaintext.</summary>
    private static bool LooksSensitive(string name) =>
        name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase) && !name.Contains("folder", StringComparison.OrdinalIgnoreCase)
        || name.Contains("applicationKey", StringComparison.OrdinalIgnoreCase)
        || name.Contains("connectionString", StringComparison.OrdinalIgnoreCase)
        || name.Equals("appKey", StringComparison.OrdinalIgnoreCase);

    private static string FindRepoDirectory(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(relative);
    }

    [Fact]
    public void SecretOptionKeys_CoverEverySensitiveOptionProvidersReadAndSettingsPasswordFields()
    {
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Every option name a provider reads (options["x"], TryGetValue("x"), GetValueOrDefault("x"), ContainsKey("x")).
        var providers = FindRepoDirectory(Path.Combine("ReStore.Core", "src", "storage"));
        var optionName = new System.Text.RegularExpressions.Regex("(?:options\\[|TryGetValue\\(|GetValueOrDefault\\(|ContainsKey\\()\"([A-Za-z_]+)\"");
        foreach (var file in Directory.GetFiles(providers, "*.cs", SearchOption.AllDirectories))
        {
            foreach (System.Text.RegularExpressions.Match m in optionName.Matches(File.ReadAllText(file)))
            {
                if (LooksSensitive(m.Groups[1].Value) && !SecretReferences.IsSecretOption(m.Groups[1].Value)) missing.Add(m.Groups[1].Value);
            }
        }

        // 2. Every Settings field shown in a PasswordBox.
        var settings = File.ReadAllText(FindRepoDirectory(Path.Combine("ReStore", "Views", "Pages", "SettingsPage.xaml.cs")));
        var passwordField = new System.Text.RegularExpressions.Regex("\\[\"([A-Za-z_]+)\"\\]\\s*=\\s*\\w+\\.Password");
        var passwordFields = passwordField.Matches(settings).Select(m => m.Groups[1].Value).ToList();
        passwordFields.Should().NotBeEmpty();
        foreach (var name in passwordFields)
        {
            if (!SecretReferences.IsSecretOption(name)) missing.Add(name);
        }

        missing.Should().BeEmpty("these sensitive provider options must be listed in SecretReferences.SecretOptionKeys");
    }

    [Fact]
    public void DpapiDataStore_ResolveFolder_RootedKept_RelativeUnderAppRoot()
    {
        var previousRoot = AppPaths.Root;
        AppPaths.OverrideRoot(_root);
        try
        {
            DpapiDataStore.ResolveFolder(Path.Combine(_root, "abs")).Should().Be(Path.Combine(_root, "abs"));
            DpapiDataStore.ResolveFolder("Drive.Storage.Token").Should().Be(Path.Combine(_root, "tokens", "Drive.Storage.Token"));
        }
        finally
        {
            AppPaths.OverrideRoot(previousRoot);
        }
    }
}
