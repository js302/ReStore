using FluentAssertions;
using Moq;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class ConfigValidatorTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _watchDir;
    private readonly string _backupRoot;
    private readonly ConfigValidator _validator;

    public ConfigValidatorTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreConfigValidatorTests_" + Guid.NewGuid());
        _watchDir = Path.Combine(_testRoot, "watch");
        _backupRoot = Path.Combine(_testRoot, "backups");

        Directory.CreateDirectory(_watchDir);
        Directory.CreateDirectory(_backupRoot);

        _validator = new ConfigValidator(new TestLogger());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public void ValidateConfiguration_ShouldFail_WhenNoWatchDirectories()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.WatchDirectories).Returns([]);

        var result = _validator.ValidateConfiguration(config.Object);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("No watch directories specified"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldFail_WhenWatchDirectoriesDoNotExist()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = Path.Combine(_testRoot, "missing") }
        ]);

        var result = _validator.ValidateConfiguration(config.Object);

        result.IsValid.Should().BeFalse();
        result.Warnings.Should().Contain(w => w.Contains("Watch directory does not exist"));
        result.Errors.Should().Contain(e => e.Contains("No valid watch directories found"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldFail_WhenBackupIntervalIsNonPositive()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.BackupInterval).Returns(TimeSpan.Zero);

        var result = _validator.ValidateConfiguration(config.Object);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Backup interval must be greater than zero"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldFail_WhenRetentionIsInvalid()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig
        {
            Enabled = true,
            KeepLastPerDirectory = 0,
            MaxAgeDays = -1
        });

        var result = _validator.ValidateConfiguration(config.Object);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("keepLastPerDirectory is < 1"));
        result.Errors.Should().Contain(e => e.Contains("maxAgeDays cannot be negative"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldFail_WhenRetentionTiersNegativeOrModeUnknown()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = true, KeepDaily = -1, Mode = "sometimes" });

        var result = _validator.ValidateConfiguration(config.Object);

        result.Errors.Should().Contain(e => e.Contains("keepDaily"));
        result.Errors.Should().Contain(e => e.Contains("mode 'sometimes'"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldWarn_WhenAllModeHasNothingBeyondKeepLast()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = true, KeepLastPerDirectory = 5, MaxAgeDays = 0, Mode = "all" });

        _validator.ValidateConfiguration(config.Object).Warnings.Should().Contain(w => w.Contains("mode 'all'"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldAccept_GfsTiersInBothModes()
    {
        foreach (var mode in new[] { "any", "all" })
        {
            var config = CreateBaseConfig();
            config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = true, KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 12, KeepYearly = 3, Mode = mode });

            _validator.ValidateConfiguration(config.Object).Errors.Should().NotContain(e => e.Contains("etention"));
        }
    }

    [Fact]
    public void ValidateConfiguration_ShouldFail_WhenStorageSourcesMissing()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.StorageSources).Returns([]);

        var result = _validator.ValidateConfiguration(config.Object);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("No storage sources configured"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldAddError_ForInvalidS3Bucket()
    {
        var config = CreateBaseConfig();
        var sources = CreateLocalStorageSource();
        sources["s3"] = new StorageConfig
        {
            Path = "./backups",
            Options = new Dictionary<string, string>
            {
                ["accessKeyId"] = "AKIA_TEST",
                ["secretAccessKey"] = "SECRET_TEST",
                ["region"] = "us-east-1",
                ["bucketName"] = "Invalid_Bucket_Name"
            }
        };

        config.SetupGet(c => c.StorageSources).Returns(sources);
        UseStorage(config, "s3");

        var result = _validator.ValidateConfiguration(config.Object);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("invalid bucket name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateConfiguration_ShouldSkipUnconfiguredDropboxStorage()
    {
        var config = CreateBaseConfig();
        var sources = CreateLocalStorageSource();
        sources["dropbox"] = new StorageConfig
        {
            Path = "/Backups",
            Options = new Dictionary<string, string>
            {
                ["accessToken"] = "",
                ["refreshToken"] = "",
                ["appKey"] = "",
                ["appSecret"] = ""
            }
        };

        config.SetupGet(c => c.StorageSources).Returns(sources);
        UseStorage(config, "dropbox");

        var result = _validator.ValidateConfiguration(config.Object);

        result.Warnings.Should().NotContain(w => w.Contains("Dropbox storage", StringComparison.OrdinalIgnoreCase));
        result.Info.Should().Contain(i => i.Contains("Storage source 'dropbox' is not configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateConfiguration_ShouldWarn_ForLowBackupInterval()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.BackupInterval).Returns(TimeSpan.FromSeconds(30));

        var result = _validator.ValidateConfiguration(config.Object);

        result.Warnings.Should().Contain(w => w.Contains("less than 1 minute", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateConfiguration_ShouldWarn_WhenMaxFileSizeExceedsThreshold()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.SizeThresholdMB).Returns(100);
        config.SetupGet(c => c.MaxFileSizeMB).Returns(200);

        var result = _validator.ValidateConfiguration(config.Object);

        result.Warnings.Should().Contain(w => w.Contains("larger than size threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateConfiguration_ShouldWarn_ForSftpWithoutAuth()
    {
        var config = CreateBaseConfig();
        var sources = CreateLocalStorageSource();
        sources["sftp"] = new StorageConfig
        {
            Path = "/backups",
            Options = new Dictionary<string, string>
            {
                ["host"] = "sftp.example.com",
                ["username"] = "user"
            }
        };

        config.SetupGet(c => c.StorageSources).Returns(sources);
        UseStorage(config, "sftp");

        var result = _validator.ValidateConfiguration(config.Object);

        result.Warnings.Should().Contain(w => w.Contains("requires either 'password' or 'privateKeyPath'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateConfiguration_ShouldWarn_WhenGcpCredentialFileMissing()
    {
        var config = CreateBaseConfig();
        var sources = CreateLocalStorageSource();
        var missingCredential = Path.Combine(_testRoot, "missing-gcp-credentials.json");
        sources["gcp"] = new StorageConfig
        {
            Path = "my-bucket",
            Options = new Dictionary<string, string>
            {
                ["bucketName"] = "my-bucket",
                ["credentialPath"] = missingCredential
            }
        };

        config.SetupGet(c => c.StorageSources).Returns(sources);
        UseStorage(config, "gcp");

        var result = _validator.ValidateConfiguration(config.Object);

        result.Warnings.Should().Contain(w => w.Contains("GCP credential file not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateConfiguration_ShouldWarn_ForUnknownStorageType()
    {
        var config = CreateBaseConfig();
        var sources = CreateLocalStorageSource();
        sources["mystorage"] = new StorageConfig
        {
            Path = "./backups",
            Options = new Dictionary<string, string>
            {
                ["token"] = "abc"
            }
        };

        config.SetupGet(c => c.StorageSources).Returns(sources);
        UseStorage(config, "mystorage");

        var result = _validator.ValidateConfiguration(config.Object);

        result.Warnings.Should().Contain(w => w.Contains("Unknown storage type 'mystorage'"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldFail_WhenEncryptionSaltIsMissing()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig
        {
            Enabled = true,
            Salt = null,
            KeyDerivationIterations = 1000
        });

        var result = _validator.ValidateConfiguration(config.Object);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("no salt is configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateConfiguration_ShouldFail_WhenEncryptionSettingsAreInvalid()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig
        {
            Enabled = true,
            Salt = "not-base64",
            VerificationToken = "also-not-base64",
            KeyDerivationIterations = 0
        });

        var result = _validator.ValidateConfiguration(config.Object);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("salt must be a valid Base64", StringComparison.OrdinalIgnoreCase));
        result.Errors.Should().Contain(e => e.Contains("verification token must be a valid Base64", StringComparison.OrdinalIgnoreCase));
        result.Errors.Should().Contain(e => e.Contains("iterations must be greater than zero", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void ValidateConfiguration_AcceptsManifestVersion2And3Only(int version, bool valid)
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { ManifestVersion = version });

        var result = _validator.ValidateConfiguration(config.Object);

        result.Errors.Any(e => e.Contains("manifestVersion", StringComparison.OrdinalIgnoreCase)).Should().Be(!valid);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("OFF", true)]
    [InlineData("zstd", false)]
    [InlineData("", false)]
    public void ValidateConfiguration_AcceptsCompressionAutoOrOffOnly(string value, bool valid)
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { Compression = value });

        var result = _validator.ValidateConfiguration(config.Object);

        result.Errors.Any(e => e.Contains("compression", StringComparison.OrdinalIgnoreCase)).Should().Be(!valid);
    }

    [Fact]
    public void ValidateConfiguration_ShouldFail_WhenChunkDiffingSettingsAreInvalid()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig
        {
            ManifestVersion = 1,
            MinChunkSizeKB = 128,
            TargetChunkSizeKB = 64,
            MaxChunkSizeKB = 32,
            RollingHashWindowSize = 2,
            MaxChunksPerFile = 0,
            MaxFilesPerSnapshot = 0
        });

        var result = _validator.ValidateConfiguration(config.Object);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("manifestVersion", StringComparison.OrdinalIgnoreCase));
        result.Errors.Should().Contain(e => e.Contains("minChunkSizeKB", StringComparison.OrdinalIgnoreCase));
        result.Errors.Should().Contain(e => e.Contains("targetChunkSizeKB", StringComparison.OrdinalIgnoreCase));
        result.Errors.Should().Contain(e => e.Contains("rollingHashWindowSize", StringComparison.OrdinalIgnoreCase));
        result.Errors.Should().Contain(e => e.Contains("maxChunksPerFile", StringComparison.OrdinalIgnoreCase));
        result.Errors.Should().Contain(e => e.Contains("maxFilesPerSnapshot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateConfiguration_ShouldWarn_ForEmptyExclusionEntries()
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.ExcludedPatterns).Returns(["", "*.tmp"]);
        config.SetupGet(c => c.ExcludedPaths).Returns(["", Path.Combine(_testRoot, "does-not-exist")]);

        var result = _validator.ValidateConfiguration(config.Object);

        result.Warnings.Should().Contain(w => w.Contains("Empty exclusion pattern"));
        result.Warnings.Should().Contain(w => w.Contains("Empty exclusion path"));
        result.Info.Should().Contain(i => i.Contains("Exclusion path does not exist"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldPass_WithValidBaseConfig()
    {
        var config = CreateBaseConfig();

        var result = _validator.ValidateConfiguration(config.Object);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/abs", false)]
    [InlineData("a/../b", false)]
    [InlineData("..", false)]
    [InlineData("team/laptop", true)]
    [InlineData("team/laptop/", true)]
    [InlineData("", true)]
    public void ValidateConfiguration_ChecksStoragePrefixOption(string prefix, bool valid)
    {
        var config = CreateBaseConfig();
        var sources = CreateLocalStorageSource();
        sources["local"].Options["prefix"] = prefix;
        config.SetupGet(c => c.StorageSources).Returns(sources);

        var result = _validator.ValidateConfiguration(config.Object);

        result.Errors.Any(e => e.Contains("'prefix'")).Should().Be(!valid);
    }

    [Fact]
    public void ValidateConfiguration_RemoteSourceWithEmptyOptionalOptions_IsStillConfigured()
    {
        var config = CreateBaseConfig();
        var sources = CreateLocalStorageSource();
        sources["s3"] = new StorageConfig
        {
            Path = "backups",
            Options = new Dictionary<string, string>
            {
                ["accessKeyId"] = "AKIA123", ["secretAccessKey"] = "secret", ["region"] = "us-east-1",
                ["bucketName"] = "bucket", ["prefix"] = ""
            }
        };
        config.SetupGet(c => c.StorageSources).Returns(sources);

        var result = _validator.ValidateConfiguration(config.Object);

        result.Errors.Should().BeEmpty();
        result.Info.Should().NotContain(i => i.Contains("'s3'") && i.Contains("not configured"));
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("info")]
    [InlineData("WARNING")]
    [InlineData("Error")]
    public void ValidateConfiguration_ShouldAcceptKnownLoggingLevels(string level)
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.Logging).Returns(new LoggingConfig { Level = level });

        var result = _validator.ValidateConfiguration(config.Object);

        result.Errors.Should().NotContain(e => e.Contains("logging.level"));
    }

    [Theory]
    [InlineData("Verbose")]
    [InlineData("")]
    [InlineData("3")]
    public void ValidateConfiguration_ShouldRejectUnknownLoggingLevel(string level)
    {
        var config = CreateBaseConfig();
        config.SetupGet(c => c.Logging).Returns(new LoggingConfig { Level = level });

        var result = _validator.ValidateConfiguration(config.Object);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("logging.level"));
    }

    /// <summary>Only storages something writes to are validated; make <paramref name="storageType"/> the storage of the watched folder.</summary>
    private void UseStorage(Mock<IConfigManager> config, string storageType) =>
        config.SetupGet(c => c.WatchDirectories).Returns([new WatchDirectoryConfig { Path = _watchDir, StorageType = storageType }]);

    [Fact]
    public void ValidateConfiguration_DoesNotWarnAboutPlaceholderStoragesNothingUses()
    {
        var config = CreateBaseConfig();
        var sources = CreateLocalStorageSource();
        sources["gcp"] = new StorageConfig { Path = "x", Options = new Dictionary<string, string> { ["bucketName"] = "my-bucket", ["credentialPath"] = @"C:\path\to\key.json" } };
        sources["sftp"] = new StorageConfig { Path = "x", Options = new Dictionary<string, string> { ["host"] = "example.org", ["username"] = "u" } };
        sources["s3"] = new StorageConfig { Path = "x", Options = new Dictionary<string, string> { ["bucketName"] = "Bad_Bucket!", ["accessKeyId"] = "a", ["secretAccessKey"] = "b" } };
        config.SetupGet(c => c.StorageSources).Returns(sources);

        var result = _validator.ValidateConfiguration(config.Object);

        result.Warnings.Should().NotContain(w => w.Contains("GCP", StringComparison.OrdinalIgnoreCase) || w.Contains("sftp", StringComparison.OrdinalIgnoreCase));
        result.Errors.Should().BeEmpty();
        result.Info.Should().Contain(i => i.Contains("'gcp'") && i.Contains("not used"));
        result.Info.Should().Contain(i => i.Contains("'s3'") && i.Contains("not used"));
    }

    [Fact]
    public void ValidateConfiguration_StillValidatesAStorageUsedBySystemBackup()
    {
        var config = CreateBaseConfig();
        var sources = CreateLocalStorageSource();
        sources["gcp"] = new StorageConfig { Path = "x", Options = new Dictionary<string, string> { ["bucketName"] = "my-bucket", ["credentialPath"] = Path.Combine(_testRoot, "missing.json") } };
        config.SetupGet(c => c.StorageSources).Returns(sources);
        config.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig { Enabled = true, ProgramsStorageType = "gcp" });

        _validator.ValidateConfiguration(config.Object).Warnings.Should().Contain(w => w.Contains("GCP credential file not found", StringComparison.OrdinalIgnoreCase));
    }

    private Mock<IConfigManager> CreateBaseConfig()
    {
        var config = new Mock<IConfigManager>();

        config.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = null }
        ]);

        config.SetupGet(c => c.GlobalStorageType).Returns("local");
        config.SetupGet(c => c.BackupInterval).Returns(TimeSpan.FromHours(1));
        config.SetupGet(c => c.SizeThresholdMB).Returns(500);
        config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        config.SetupGet(c => c.BackupType).Returns(BackupType.Incremental);
        config.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig());
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig());
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 5, MaxAgeDays = 30 });
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        config.SetupGet(c => c.ExcludedPatterns).Returns(["*.tmp", "*.log"]);
        config.SetupGet(c => c.ExcludedPaths).Returns([]);
        config.SetupGet(c => c.StorageSources).Returns(CreateLocalStorageSource());

        return config;
    }

    private Dictionary<string, StorageConfig> CreateLocalStorageSource()
    {
        return new Dictionary<string, StorageConfig>
        {
            ["local"] = new StorageConfig
            {
                Path = Path.Combine(_backupRoot, "restore-backups"),
                Options = new Dictionary<string, string>()
            }
        };
    }
}
