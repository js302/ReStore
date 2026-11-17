using FluentAssertions;
using Moq;
using ReStore.Core;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;
using System.Reflection;

namespace ReStore.Tests;

[Collection("GlobalState")]
public class VerifiedPasswordProviderTests : IDisposable
{
    private const string ENV_KEY = "RESTORE_ENCRYPTION_PASSWORD";
    private const int ITERATIONS = 1000;

    private readonly TestLogger _logger = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreVppTests_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousEnv = Environment.GetEnvironmentVariable(ENV_KEY);

    public VerifiedPasswordProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ENV_KEY, _previousEnv);
        try { Directory.Delete(_root, true); } catch { }
    }

    private (Mock<IConfigManager> Config, EncryptionConfig Encryption, byte[] Salt) CreateConfig(string? tokenPassword)
    {
        var salt = EncryptionService.GenerateSalt();
        var encryption = new EncryptionConfig
        {
            Enabled = true,
            Salt = Convert.ToBase64String(salt),
            KeyDerivationIterations = ITERATIONS,
            VerificationToken = tokenPassword == null
                ? null
                : new EncryptionService(_logger).CreatePasswordVerificationToken(tokenPassword, salt, ITERATIONS)
        };
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.Encryption).Returns(encryption);
        config.Setup(c => c.SaveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        return (config, encryption, salt);
    }

    private static Func<PasswordPromptRequest, Task<string?>> Script(Queue<string?> answers, List<PasswordPromptRequest>? seen = null)
    {
        return request =>
        {
            seen?.Add(request);
            return Task.FromResult(answers.Count > 0 ? answers.Dequeue() : null);
        };
    }

    [Fact]
    public async Task Encrypt_WrongPasswordThreeTimes_ReturnsNullAndNeverCaches()
    {
        var (config, _, _) = CreateConfig("right-password");
        var seen = new List<PasswordPromptRequest>();
        var notices = new List<string>();
        var provider = new VerifiedPasswordProvider(config.Object, _logger,
            Script(new Queue<string?>(["bad1", "bad2", "bad3", "right-password"]), seen), notices.Add);

        (await provider.GetPasswordAsync(PasswordPurpose.Encrypt)).Should().BeNull();

        seen.Should().HaveCount(3);
        notices.Should().HaveCount(3);
        provider.IsPasswordSet().Should().BeFalse();
    }

    [Fact]
    public async Task Encrypt_WrongThenRightPassword_CachesVerifiedPassword()
    {
        var (config, _, _) = CreateConfig("right-password");
        var seen = new List<PasswordPromptRequest>();
        var provider = new VerifiedPasswordProvider(config.Object, _logger,
            Script(new Queue<string?>(["typo", "right-password"]), seen));

        (await provider.GetPasswordAsync(PasswordPurpose.Encrypt)).Should().Be("right-password");
        (await provider.GetPasswordAsync(PasswordPurpose.Encrypt)).Should().Be("right-password");

        seen.Should().HaveCount(2);
        provider.IsPasswordSet().Should().BeTrue();
    }

    [Fact]
    public async Task Decrypt_Password_IsNeverReturnedToEncryptCaller()
    {
        var (config, _, _) = CreateConfig("right-password");
        var provider = new VerifiedPasswordProvider(config.Object, _logger,
            Script(new Queue<string?>(["old-snapshot-password", "right-password"])));

        (await provider.GetPasswordAsync(PasswordPurpose.Decrypt)).Should().Be("old-snapshot-password");
        provider.IsPasswordSet().Should().BeFalse();

        (await provider.GetPasswordAsync(PasswordPurpose.Encrypt)).Should().Be("right-password");
        (await provider.GetPasswordAsync(PasswordPurpose.Decrypt)).Should().Be("old-snapshot-password");
    }

    [Fact]
    public async Task ClearPassword_ClearsDecryptCacheOnly()
    {
        var (config, _, _) = CreateConfig("right-password");
        var provider = new VerifiedPasswordProvider(config.Object, _logger,
            Script(new Queue<string?>(["right-password", "d1", "d2"])));

        await provider.GetPasswordAsync(PasswordPurpose.Encrypt);
        (await provider.GetPasswordAsync(PasswordPurpose.Decrypt)).Should().Be("d1");

        provider.ClearPassword();

        provider.IsPasswordSet().Should().BeTrue();
        (await provider.GetPasswordAsync(PasswordPurpose.Decrypt)).Should().Be("d2");
    }

    [Fact]
    public async Task Migration_RequiresTwoMatchingEntries_AndSavesToken()
    {
        var (config, encryption, salt) = CreateConfig(tokenPassword: null);
        var seen = new List<PasswordPromptRequest>();
        var provider = new VerifiedPasswordProvider(config.Object, _logger,
            Script(new Queue<string?>(["typo-one", "typo-two", "good-password", "good-password"]), seen));

        (await provider.GetPasswordAsync(PasswordPurpose.Encrypt)).Should().Be("good-password");

        seen.Select(r => r.IsConfirmation).Should().Equal(false, true, false, true);
        encryption.VerificationToken.Should().NotBeNullOrEmpty();
        new EncryptionService(_logger).VerifyPassword("good-password", salt, encryption.VerificationToken!, ITERATIONS).Should().BeTrue();
        config.Verify(c => c.SaveAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Migration_MismatchedEntriesThreeTimes_FailsWithoutToken()
    {
        var (config, encryption, _) = CreateConfig(tokenPassword: null);
        var provider = new VerifiedPasswordProvider(config.Object, _logger,
            Script(new Queue<string?>(["a", "b", "c", "d", "e", "f"])));

        (await provider.GetPasswordAsync(PasswordPurpose.Encrypt)).Should().BeNull();

        encryption.VerificationToken.Should().BeNull();
        provider.IsPasswordSet().Should().BeFalse();
        config.Verify(c => c.SaveAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_ReturnsNull()
    {
        var (config, _, _) = CreateConfig("right-password");
        var provider = new VerifiedPasswordProvider(config.Object, _logger, Script(new Queue<string?>()));

        (await provider.GetPasswordAsync(PasswordPurpose.Encrypt)).Should().BeNull();
        (await provider.GetPasswordAsync(PasswordPurpose.Decrypt)).Should().BeNull();
    }

    [Fact]
    public void EncryptionConfigurator_Enable_CreatesVerifiableToken()
    {
        var config = new Mock<IConfigManager>();
        var encryption = new EncryptionConfig { KeyDerivationIterations = ITERATIONS };
        config.SetupGet(c => c.Encryption).Returns(encryption);
        var salt = EncryptionService.GenerateSalt();

        EncryptionConfigurator.Enable(config.Object, _logger, "wizard-password", salt);

        encryption.Enabled.Should().BeTrue();
        encryption.Salt.Should().Be(Convert.ToBase64String(salt));
        encryption.VerificationToken.Should().NotBeNullOrEmpty();
        var service = new EncryptionService(_logger);
        service.VerifyPassword("wizard-password", salt, encryption.VerificationToken!, ITERATIONS).Should().BeTrue();
        service.VerifyPassword("other", salt, encryption.VerificationToken!, ITERATIONS).Should().BeFalse();
    }

    [Fact]
    public async Task CliRestore_UsesSnapshotSalt_WhenConfigSaltDiffers()
    {
        const string password = "Snapshot-Password-1!";
        var sourceDir = Path.Combine(_root, "src");
        var storageDir = Path.Combine(_root, "storage");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(storageDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "data.txt"), "secret content");

        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDir });

        var backupConfig = new Mock<IConfigManager>();
        backupConfig.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        backupConfig.SetupGet(c => c.GlobalStorageType).Returns("local");
        backupConfig.SetupGet(c => c.SizeThresholdMB).Returns(100);
        backupConfig.SetupGet(c => c.ExcludedPatterns).Returns([]);
        backupConfig.SetupGet(c => c.ExcludedPaths).Returns([]);
        backupConfig.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        backupConfig.SetupGet(c => c.WatchDirectories).Returns([]);
        backupConfig.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        backupConfig.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        backupConfig.SetupGet(c => c.Encryption).Returns(new EncryptionConfig
        {
            Enabled = true,
            Salt = Convert.ToBase64String(EncryptionService.GenerateSalt()),
            KeyDerivationIterations = ITERATIONS
        });
        backupConfig.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_root, "state.json"));
        await new Backup(_logger, state, new SizeAnalyzer(), backupConfig.Object, new StaticPasswordProvider(password))
            .BackupDirectoryAsync(sourceDir);
        var remotePath = state.GetPreviousBackupPath(sourceDir)!;

        // CLI config now disagrees with the snapshot: encryption off, and a different salt.
        var cliConfig = new Mock<IConfigManager>();
        cliConfig.SetupGet(c => c.Encryption).Returns(new EncryptionConfig
        {
            Enabled = false,
            Salt = Convert.ToBase64String(EncryptionService.GenerateSalt()),
            KeyDerivationIterations = 5
        });

        Environment.SetEnvironmentVariable(ENV_KEY, password);
        var method = typeof(Program).GetMethod("CreateCliPasswordProvider", BindingFlags.NonPublic | BindingFlags.Static)!;
        var provider = (IPasswordProvider)method.Invoke(null, [cliConfig.Object])!;

        var restoreDir = Path.Combine(_root, "restored");
        await new Restore(_logger, storage, provider).RestoreFromBackupAsync(remotePath, restoreDir);

        (await File.ReadAllTextAsync(Path.Combine(restoreDir, "data.txt"))).Should().Be("secret content");
    }
}
