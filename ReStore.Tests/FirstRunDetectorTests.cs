using System.Text.Json;
using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

[Collection("GlobalState")]
public class FirstRunDetectorTests
{
    private static Dictionary<string, StorageConfig> TemplateStorageSources()
    {
        // Mirrors config.example.json, which is what config auto-creation writes on a clean machine.
        return new Dictionary<string, StorageConfig>
        {
            ["gdrive"] = new()
            {
                Path = "./backups",
                Options = new()
                {
                    ["client_id"] = "",
                    ["client_secret"] = "",
                    ["token_folder"] = "",
                    ["backup_folder_name"] = "ReStoreBackups"
                }
            },
            ["s3"] = new()
            {
                Path = "./backups",
                Options = new()
                {
                    ["accessKeyId"] = "",
                    ["secretAccessKey"] = "",
                    ["region"] = "",
                    ["bucketName"] = ""
                }
            },
            ["github"] = new()
            {
                Path = "./backups",
                Options = new() { ["token"] = "", ["repo"] = "", ["owner"] = "" }
            },
            ["local"] = new()
            {
                Path = "%USERPROFILE%\\ReStoreBackups",
                Options = []
            },
            ["azure"] = new()
            {
                Path = "my-container",
                Options = new() { ["connectionString"] = "", ["containerName"] = "my-container" }
            },
            ["gcp"] = new()
            {
                Path = "my-bucket",
                Options = new() { ["bucketName"] = "my-bucket", ["credentialPath"] = "C:\\path\\to\\key.json" }
            },
            ["dropbox"] = new()
            {
                Path = "/Backups",
                Options = new()
                {
                    ["accessToken"] = "your-access-token",
                    ["refreshToken"] = "optional-refresh-token",
                    ["appKey"] = "optional-app-key"
                }
            },
            ["b2"] = new()
            {
                Path = "my-bucket",
                Options = new()
                {
                    ["keyId"] = "your-key-id",
                    ["applicationKey"] = "your-app-key",
                    ["serviceUrl"] = "https://s3.us-west-000.backblazeb2.com"
                }
            },
            ["sftp"] = new()
            {
                Path = "/home/user/backups",
                Options = new()
                {
                    ["host"] = "sftp.example.com",
                    ["port"] = "22",
                    ["username"] = "user",
                    ["password"] = "",
                    ["privateKeyPath"] = "C:\\path\\to\\private_key",
                    ["passphrase"] = "optional-passphrase"
                }
            }
        };
    }

    [Fact]
    public void FreshTemplateConfigNeedsSetup()
    {
        var needsSetup = FirstRunDetector.NeedsSetup(
            TemplateStorageSources(),
            localProviderIsConfigured: false,
            totalBackupCount: 0);

        needsSetup.Should().BeTrue();
    }

    [Fact]
    public void ConfiguredProviderDoesNotNeedSetup()
    {
        var sources = TemplateStorageSources();
        sources["s3"].Options["accessKeyId"] = "AKIAREALKEY";
        sources["s3"].Options["secretAccessKey"] = "realsecret";

        var needsSetup = FirstRunDetector.NeedsSetup(sources, localProviderIsConfigured: false, totalBackupCount: 0);

        needsSetup.Should().BeFalse();
    }

    [Fact]
    public void ExistingBackupsDoNotNeedSetup()
    {
        var needsSetup = FirstRunDetector.NeedsSetup(
            TemplateStorageSources(),
            localProviderIsConfigured: false,
            totalBackupCount: 4);

        needsSetup.Should().BeFalse();
    }

    [Fact]
    public void ConfiguredLocalProviderDoesNotNeedSetup()
    {
        var needsSetup = FirstRunDetector.NeedsSetup(
            TemplateStorageSources(),
            localProviderIsConfigured: true,
            totalBackupCount: 0);

        needsSetup.Should().BeFalse();
    }

    [Fact]
    public void EmptyStorageSourcesNeedSetup()
    {
        var needsSetup = FirstRunDetector.NeedsSetup(
            new Dictionary<string, StorageConfig>(),
            localProviderIsConfigured: false,
            totalBackupCount: 0);

        needsSetup.Should().BeTrue();
    }

    [Theory]
    [InlineData("your-access-token")]
    [InlineData("my-bucket")]
    [InlineData("C:\\path\\to\\key.json")]
    [InlineData("  ")]
    [InlineData("")]
    public void PlaceholderCredentialsDoNotCount(string value)
    {
        var config = new StorageConfig { Options = new() { ["accessToken"] = value } };

        FirstRunDetector.HasUsableCredentials("dropbox", config).Should().BeFalse();
    }

    [Fact]
    public void LocationOnlyOptionsDoNotCountAsCredentials()
    {
        // A bucket name alone is not proof of setup; the template ships one.
        var config = new StorageConfig
        {
            Options = new() { ["bucketName"] = "user-picked-bucket", ["path"] = "./backups" }
        };

        FirstRunDetector.HasUsableCredentials("s3", config).Should().BeFalse();
    }

    [Fact]
    public void LocalProviderIsNeverCredentialConfigured()
    {
        var config = new StorageConfig { Path = "%USERPROFILE%\\ReStoreBackups" };

        FirstRunDetector.HasUsableCredentials("local", config).Should().BeFalse();
    }

    [Fact]
    public void RealCredentialCounts()
    {
        var config = new StorageConfig { Options = new() { ["token"] = "ghp_realtokenvalue" } };

        FirstRunDetector.HasUsableCredentials("github", config).Should().BeTrue();
    }

    [Fact]
    public void ShippedExampleConfigIsDetectedAsNeedingSetup()
    {
        // The hand-built dictionary above omitted sftp, which is exactly how the original
        // wizard-suppressing bug slipped through: one unlisted placeholder in a provider the
        // test never modelled. This reads every provider from the real file instead.
        var sources = LoadExampleStorageSources();

        sources.Should().HaveCountGreaterThan(1);

        FirstRunDetector.NeedsSetup(sources, localProviderIsConfigured: false, totalBackupCount: 0)
            .Should().BeTrue();
    }

    [Fact]
    public void NoShippedProviderLooksConfigured()
    {
        // Pinpoints the offending provider when the guard above fails.
        var sources = LoadExampleStorageSources();

        var configured = sources
            .Where(source => FirstRunDetector.HasUsableCredentials(source.Key, source.Value))
            .Select(source => source.Key);

        configured.Should().BeEmpty();
    }

    [Theory]
    [InlineData("s3", "accessKeyId", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("sftp", "password", "hunter2")]
    [InlineData("sftp", "privateKeyPath", "C:\\Users\\me\\.ssh\\id_rsa")]
    [InlineData("sftp", "passphrase", "correct horse battery staple")]
    [InlineData("github", "token", "ghp_abc123")]
    [InlineData("dropbox", "accessToken", "sl.real-token-value")]
    [InlineData("gcp", "credentialPath", "D:\\keys\\gcp-service-account.json")]
    public void RealCredentialInShippedConfigSuppressesSetup(string provider, string option, string value)
    {
        var sources = LoadExampleStorageSources();
        sources[provider].Options[option] = value;

        FirstRunDetector.NeedsSetup(sources, localProviderIsConfigured: false, totalBackupCount: 0)
            .Should().BeFalse();
    }

    private static Dictionary<string, StorageConfig> LoadExampleStorageSources()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateExampleConfig()));

        // Mirrors ConfigManager's own read options; a case-sensitive read silently loses
        // options and would make stub entries look empty rather than placeholder-filled.
        return JsonSerializer.Deserialize<Dictionary<string, StorageConfig>>(
            document.RootElement.GetProperty("storageSources").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static string LocateExampleConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "ReStore.Core", "config", "config.example.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate config.example.json from the test output directory.");
    }
}
