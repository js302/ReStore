using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;
using System.Reflection;

namespace ReStore.Tests;

public class SystemBackupManagerHelperTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger;
    private readonly Mock<IConfigManager> _configMock;
    private readonly SystemBackupManager _manager;

    public SystemBackupManagerHelperTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreSystemBackupManagerHelperTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);

        _logger = new TestLogger();
        _configMock = new Mock<IConfigManager>();
        _configMock.SetupGet(c => c.GlobalStorageType).Returns("global-storage");
        _configMock.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig
        {
            StorageType = "default-system-storage",
            ProgramsStorageType = "program-storage",
            EnvironmentStorageType = "environment-storage",
            SettingsStorageType = "settings-storage"
        });

        var state = new Mock<SystemState>(_logger).Object;
        _manager = new SystemBackupManager(_logger, _configMock.Object, state);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Theory]
    [InlineData("system_programs", "system_backups/anything.zip", "programs")]
    [InlineData("system_environment", "system_backups/anything.zip", "environment")]
    [InlineData("system_settings", "system_backups/anything.zip", "settings")]
    [InlineData("all", "system_backups/programs/p.zip", "programs")]
    [InlineData("all", "system_backups/environment/e.zip", "environment")]
    [InlineData("all", "system_backups/settings/s.zip", "settings")]
    [InlineData("programs", "x", "programs")]
    public void ResolveRestoreComponent_ShouldNormalizeExpectedValues(string backupType, string backupPath, string expected)
    {
        var component = InvokePrivateStatic<string>(typeof(SystemBackupManager), "ResolveRestoreComponent", backupType, backupPath);

        component.Should().Be(expected);
    }

    [Fact]
    public void ResolveRestoreComponent_ShouldThrowForUnsupportedType()
    {
        var action = () => InvokePrivateStatic<string>(typeof(SystemBackupManager), "ResolveRestoreComponent", "unknown", "backup.zip");

        action.Should().Throw<TargetInvocationException>()
            .WithInnerException<ArgumentException>();
    }

    [Fact]
    public void CreateRestoreTempDirectory_AndCleanup_ShouldCreateAndRemoveDirectory()
    {
        var tempDir = InvokePrivateStatic<string>(typeof(SystemBackupManager), "CreateRestoreTempDirectory");

        Directory.Exists(tempDir).Should().BeTrue();

        InvokePrivate(_manager, "CleanupRestoreTempDirectory", tempDir);

        Directory.Exists(tempDir).Should().BeFalse();
    }

    [Fact]
    public void GetStorageTypeForComponent_ShouldRespectComponentOverrides()
    {
        InvokePrivate<string>(_manager, "GetStorageTypeForComponent", "programs").Should().Be("program-storage");
        InvokePrivate<string>(_manager, "GetStorageTypeForComponent", "environment").Should().Be("environment-storage");
        InvokePrivate<string>(_manager, "GetStorageTypeForComponent", "settings").Should().Be("settings-storage");
        InvokePrivate<string>(_manager, "GetStorageTypeForComponent", "unknown").Should().Be("global-storage");
    }

    [Fact]
    public void GetStorageTypeForComponent_ShouldFallBackToSharedSystemStorage_AndGlobalStorage()
    {
        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.GlobalStorageType).Returns("global-storage");
        configMock.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig
        {
            StorageType = "shared-system-storage"
        });

        var state = new Mock<SystemState>(_logger).Object;
        var manager = new SystemBackupManager(_logger, configMock.Object, state);

        InvokePrivate<string>(manager, "GetStorageTypeForComponent", "programs").Should().Be("shared-system-storage");
        InvokePrivate<string>(manager, "GetStorageTypeForComponent", "environment").Should().Be("shared-system-storage");
        InvokePrivate<string>(manager, "GetStorageTypeForComponent", "settings").Should().Be("shared-system-storage");
        InvokePrivate<string>(manager, "GetStorageTypeForComponent", "other").Should().Be("global-storage");
    }

    [Fact]
    public async Task CreateWingetRestoreScriptAsync_ShouldContainOnlyWingetInstallCommands()
    {
        var programs = new List<InstalledProgram>
        {
            new() { Name = "Winget App", WingetId = "Contoso.WingetApp", IsWingetAvailable = true },
            new() { Name = "Manual App", IsWingetAvailable = false }
        };

        var scriptPath = Path.Combine(_testRoot, "restore_winget_programs.ps1");
        await InvokePrivateAsync(_manager, "CreateWingetRestoreScriptAsync", programs, scriptPath);

        var content = await File.ReadAllTextAsync(scriptPath);
        content.Should().Contain("Contoso.WingetApp");
        content.Should().Contain("Winget App");
        content.Should().NotContain("Manual App");
    }

    [Fact]
    public async Task CreateManualInstallListAsync_ShouldIncludeOnlyManualPrograms()
    {
        var programs = new List<InstalledProgram>
        {
            new() { Name = "Manual Z", Version = "3", Publisher = "P", IsWingetAvailable = false },
            new() { Name = "Winget A", WingetId = "A", IsWingetAvailable = true },
            new() { Name = "Manual A", Version = "1", Publisher = "Q", IsWingetAvailable = false }
        };

        var outputPath = Path.Combine(_testRoot, "manual_install_list.txt");
        await InvokePrivateAsync(_manager, "CreateManualInstallListAsync", programs, outputPath);

        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain("Total programs requiring manual installation: 2");
        content.Should().Contain("Manual A");
        content.Should().Contain("Manual Z");
        content.Should().NotContain("Winget A |");
    }

    [Fact]
    public async Task CreateFullRestoreScriptAsync_ShouldSummarizeManualOverflow()
    {
        var programs = new List<InstalledProgram>
        {
            new() { Name = "Winget App", WingetId = "Contoso.WingetApp", IsWingetAvailable = true }
        };

        for (int i = 0; i < 22; i++)
        {
            programs.Add(new InstalledProgram
            {
                Name = "Manual " + i,
                Version = "1.0",
                Publisher = "Publisher",
                IsWingetAvailable = false
            });
        }

        var scriptPath = Path.Combine(_testRoot, "restore_programs.ps1");
        await InvokePrivateAsync(_manager, "CreateFullRestoreScriptAsync", programs, scriptPath);

        var content = await File.ReadAllTextAsync(scriptPath);
        content.Should().Contain("Installing 1 programs via winget");
        content.Should().Contain("... and 2 more (see manual_install_list.txt)");
        content.Should().Contain("Manual installation required: 22");
    }

    [Fact]
    public async Task CreateRegistryBackupScriptAsync_ShouldWriteExpectedRegistryCommands()
    {
        var scriptPath = Path.Combine(_testRoot, "backup_env_registry.ps1");

        await InvokePrivateAsync(_manager, "CreateRegistryBackupScriptAsync", scriptPath);

        var content = await File.ReadAllTextAsync(scriptPath);
        content.Should().Contain("HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment");
        content.Should().Contain("HKCU\\Environment");
        content.Should().Contain("reg export");
    }

    [Fact]
    public async Task BackupSystemAsync_ShouldLogSkippedComponents_WhenSystemBackupComponentsAreDisabled()
    {
        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.GlobalStorageType).Returns("global-storage");
        configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        configMock.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig
        {
            IncludePrograms = false,
            IncludeEnvironmentVariables = false,
            IncludeWindowsSettings = false
        });

        var manager = new SystemBackupManager(_logger, configMock.Object, new Mock<SystemState>(_logger).Object);

        await manager.BackupSystemAsync();

        _logger.Messages.Should().Contain(message => message.Contains("Skipping programs backup"));
        _logger.Messages.Should().Contain(message => message.Contains("Skipping environment variables backup"));
        _logger.Messages.Should().Contain(message => message.Contains("Skipping Windows settings backup"));
        _logger.Messages.Should().Contain(message => message.Contains("System backup completed successfully"));
    }

    [Fact]
    public void EscapeDriveQueryValue_ShouldEscapeApostrophesAndBackslashes()
    {
        var escaped = InvokePrivateStatic<string>(
            typeof(ReStore.Core.src.storage.google.DriveStorage),
            "EscapeDriveQueryValue",
            "John's\\Folder");

        escaped.Should().Be("John\\'s\\\\Folder");
    }

    [Fact]
    public async Task RestoreProgramsAsync_ShouldLogWhenScriptAndJsonExist()
    {
        var extractDir = Path.Combine(_testRoot, "programs");
        Directory.CreateDirectory(extractDir);
        await File.WriteAllTextAsync(Path.Combine(extractDir, "restore_programs.ps1"), "# script");
        await File.WriteAllTextAsync(Path.Combine(extractDir, "installed_programs.json"), "{}");

        await InvokePrivateAsync(_manager, "RestoreProgramsAsync", extractDir);

        _logger.Messages.Should().Contain(m => m.Contains("Programs restore script found"));
        _logger.Messages.Should().Contain(m => m.Contains("Programs backup data available"));
    }

    [Fact]
    public async Task RestoreWindowsSettingsAsync_ShouldLogScriptManifestAndWarning()
    {
        var extractDir = Path.Combine(_testRoot, "settings");
        Directory.CreateDirectory(extractDir);
        await File.WriteAllTextAsync(Path.Combine(extractDir, "restore_windows_settings.ps1"), "# script");
        await File.WriteAllTextAsync(Path.Combine(extractDir, "settings_manifest.json"), "{}");

        await InvokePrivateAsync(_manager, "RestoreWindowsSettingsAsync", extractDir);

        _logger.Messages.Should().Contain(m => m.Contains("Windows settings restore script available"));
        _logger.Messages.Should().Contain(m => m.Contains("Settings manifest available"));
        _logger.Messages.Should().Contain(m => m.Contains("IMPORTANT: Review the script before running"));
    }

    [Fact]
    public async Task DownloadAndExtractRestoreBackupAsync_ShouldExtractFiles_ForUnencryptedBackup()
    {
        var storageDir = Path.Combine(_testRoot, "storage-unencrypted");
        var sourceDir = Path.Combine(_testRoot, "source-unencrypted");
        Directory.CreateDirectory(storageDir);
        Directory.CreateDirectory(sourceDir);

        var sourceFile = Path.Combine(sourceDir, "data.txt");
        await File.WriteAllTextAsync(sourceFile, "payload");

        var zipPath = Path.Combine(_testRoot, "plain.zip");
        await CompressionUtil.CompressFilesAsync([sourceFile], sourceDir, zipPath);

        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDir });
        await storage.UploadAsync(zipPath, "system_backups/environment/plain.zip");

        var tempDir = Path.Combine(_testRoot, "restore-unencrypted");
        Directory.CreateDirectory(tempDir);

        var extractDir = await InvokePrivateAsync<string>(_manager, "DownloadAndExtractRestoreBackupAsync", storage, "system_backups/environment/plain.zip", tempDir);

        File.Exists(Path.Combine(extractDir, "data.txt")).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(extractDir, "data.txt"))).Should().Be("payload");
    }

    [Fact]
    public async Task DownloadAndExtractRestoreBackupAsync_ShouldExtractFiles_ForEncryptedBackup()
    {
        var passwordProvider = new Mock<IPasswordProvider>();
        passwordProvider.Setup(p => p.GetPasswordAsync()).ReturnsAsync("CorrectPassword123!");

        var manager = new SystemBackupManager(_logger, _configMock.Object, new Mock<SystemState>(_logger).Object, passwordProvider.Object);
        var (storage, encryptedPath, _) = await CreateEncryptedRestoreBackupAsync("CorrectPassword123!");

        var tempDir = Path.Combine(_testRoot, "restore-encrypted");
        Directory.CreateDirectory(tempDir);

        var extractDir = await InvokePrivateAsync<string>(manager, "DownloadAndExtractRestoreBackupAsync", storage, encryptedPath, tempDir);

        File.Exists(Path.Combine(extractDir, "data.txt")).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(extractDir, "data.txt"))).Should().Be("payload");
    }

    [Fact]
    public async Task DownloadAndDecryptBackupAsync_ShouldThrow_WhenPasswordProviderMissing()
    {
        var (storage, encryptedPath, zipPath) = await CreateEncryptedRestoreBackupAsync("CorrectPassword123!");

        var action = () => InvokePrivateAsync(_manager, "DownloadAndDecryptBackupAsync", storage, encryptedPath, zipPath, Path.Combine(_testRoot, "extract-no-provider"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no password provider available*");
    }

    [Fact]
    public async Task DownloadAndDecryptBackupAsync_ShouldThrow_WhenPasswordProviderReturnsEmptyPassword()
    {
        var (storage, encryptedPath, zipPath) = await CreateEncryptedRestoreBackupAsync("CorrectPassword123!");
        var passwordProvider = new Mock<IPasswordProvider>();
        passwordProvider.Setup(p => p.GetPasswordAsync()).ReturnsAsync((string?)null);

        var manager = new SystemBackupManager(_logger, _configMock.Object, new Mock<SystemState>(_logger).Object, passwordProvider.Object);

        var action = () => InvokePrivateAsync(manager, "DownloadAndDecryptBackupAsync", storage, encryptedPath, zipPath, Path.Combine(_testRoot, "extract-empty-password"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Password required to decrypt backup*");
    }

    [Fact]
    public async Task DownloadAndDecryptBackupAsync_ShouldClearPassword_WhenDecryptionFails()
    {
        var (storage, encryptedPath, zipPath) = await CreateEncryptedRestoreBackupAsync("CorrectPassword123!");
        var passwordProvider = new Mock<IPasswordProvider>();
        passwordProvider.Setup(p => p.GetPasswordAsync()).ReturnsAsync("WrongPassword!");

        var manager = new SystemBackupManager(_logger, _configMock.Object, new Mock<SystemState>(_logger).Object, passwordProvider.Object);

        var action = () => InvokePrivateAsync(manager, "DownloadAndDecryptBackupAsync", storage, encryptedPath, zipPath, Path.Combine(_testRoot, "extract-wrong-password"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to decrypt backup*");

        passwordProvider.Verify(p => p.ClearPassword(), Times.Once);
    }

    [Fact]
    public async Task RestoreEnvironmentVariablesAsync_ShouldRestoreVariablesAndLogScriptPath_WhenArtifactsExist()
    {
        var variableName = "RESTORE_SYSTEM_BACKUP_" + Guid.NewGuid().ToString("N");
        var extractDir = Path.Combine(_testRoot, "environment-restore");
        Directory.CreateDirectory(extractDir);

        Environment.SetEnvironmentVariable(variableName, null, EnvironmentVariableTarget.User);

        try
        {
            var payload = new
            {
                variables = new[]
                {
                    new EnvironmentVariableEntry
                    {
                        Name = variableName,
                        Value = "restored-value",
                        Target = EnvironmentVariableTarget.User
                    }
                }
            };

            var jsonPath = Path.Combine(extractDir, "environment_variables.json");
            await File.WriteAllTextAsync(jsonPath, System.Text.Json.JsonSerializer.Serialize(payload));
            var scriptPath = Path.Combine(extractDir, "restore_environment_variables.ps1");
            await File.WriteAllTextAsync(scriptPath, "# restore script");

            await InvokePrivateAsync(_manager, "RestoreEnvironmentVariablesAsync", extractDir);

            Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.User).Should().Be("restored-value");
            _logger.Messages.Should().Contain(message => message.Contains("Environment variables restore script available"));
            _logger.Messages.Should().Contain(message => message.Contains(scriptPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null, EnvironmentVariableTarget.User);
        }
    }

    [Fact]
    public async Task BackupWindowsSettingsAsync_ShouldThrow_WhenEncryptionEnabledButPasswordProviderMissing()
    {
        var loggerMock = new Mock<ILogger>();
        var storageMock = new Mock<IStorage>();
        var systemStateMock = new Mock<SystemState>(loggerMock.Object);

        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.GlobalStorageType).Returns("global-storage");
        configMock.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig
        {
            SettingsStorageType = "settings-storage"
        });
        configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig
        {
            Enabled = true,
            Salt = Convert.ToBase64String(EncryptionService.GenerateSalt())
        });
        configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        configMock.Setup(c => c.CreateStorageAsync("settings-storage")).ReturnsAsync(storageMock.Object);

        var manager = new SystemBackupManager(loggerMock.Object, configMock.Object, systemStateMock.Object);

        var action = () => manager.BackupWindowsSettingsAsync();

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no password provider is available*");

        storageMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BackupEnvironmentVariablesAsync_ShouldThrow_WhenEncryptionEnabledButPasswordProviderMissing()
    {
        var loggerMock = new Mock<ILogger>();
        var storageMock = new Mock<IStorage>();
        var systemStateMock = new Mock<SystemState>(loggerMock.Object);

        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.GlobalStorageType).Returns("global-storage");
        configMock.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig
        {
            EnvironmentStorageType = "environment-storage"
        });
        configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig
        {
            Enabled = true,
            Salt = Convert.ToBase64String(EncryptionService.GenerateSalt())
        });
        configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        configMock.Setup(c => c.CreateStorageAsync("environment-storage")).ReturnsAsync(storageMock.Object);

        var manager = new SystemBackupManager(loggerMock.Object, configMock.Object, systemStateMock.Object);

        var action = () => manager.BackupEnvironmentVariablesAsync();

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no password provider is available*");

        storageMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private async Task<(LocalStorage Storage, string EncryptedPath, string ZipPath)> CreateEncryptedRestoreBackupAsync(string password)
    {
        var sourceDir = Path.Combine(_testRoot, "encrypted-source-" + Guid.NewGuid().ToString("N"));
        var storageDir = Path.Combine(_testRoot, "encrypted-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(storageDir);

        var sourceFile = Path.Combine(sourceDir, "data.txt");
        await File.WriteAllTextAsync(sourceFile, "payload");

        var sourceZip = Path.Combine(_testRoot, $"backup_{Guid.NewGuid():N}.zip");
        await CompressionUtil.CompressFilesAsync([sourceFile], sourceDir, sourceZip);

        var encryptedFile = await CompressionUtil.CompressAndEncryptAsync(
            sourceZip,
            password,
            Convert.ToBase64String(EncryptionService.GenerateSalt()),
            _logger);

        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDir });

        var remotePath = $"system_backups/environment/{Path.GetFileName(encryptedFile)}";
        await storage.UploadAsync(encryptedFile, remotePath);
        await storage.UploadAsync(encryptedFile + ".meta", remotePath + ".meta");

        var tempZipPath = Path.Combine(_testRoot, "downloaded", Path.GetFileName(encryptedFile));
        Directory.CreateDirectory(Path.GetDirectoryName(tempZipPath)!);

        File.Delete(encryptedFile);
        File.Delete(encryptedFile + ".meta");

        return (storage, remotePath, tempZipPath);
    }

    private static T InvokePrivate<T>(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (T)method!.Invoke(instance, args)!;
    }

    private static void InvokePrivate(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(instance, args);
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (T)method!.Invoke(null, args)!;
    }

    private static async Task InvokePrivateAsync(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var result = method!.Invoke(instance, args);
        if (result is Task task)
        {
            await task;
            return;
        }

        throw new InvalidOperationException($"Method {methodName} did not return Task.");
    }

    private static async Task<T> InvokePrivateAsync<T>(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var result = method!.Invoke(instance, args);
        if (result is Task<T> typedTask)
        {
            return await typedTask;
        }

        throw new InvalidOperationException($"Method {methodName} did not return Task<{typeof(T).Name}>.");
    }
}
