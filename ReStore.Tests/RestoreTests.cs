using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class RestoreTests : IDisposable
{
    private readonly string _testRoot;
    private readonly TestLogger _logger;

    public RestoreTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreRestoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _logger = new TestLogger();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenBackupPathIsEmpty()
    {
        var storage = new Mock<IStorage>();
        var restore = new Restore(_logger, storage.Object);

        var action = () => restore.RestoreFromBackupAsync(" ", Path.Combine(_testRoot, "restore"));

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Backup path cannot be null or empty*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenTargetDirectoryIsEmpty()
    {
        var storage = new Mock<IStorage>();
        var restore = new Restore(_logger, storage.Object);

        var action = () => restore.RestoreFromBackupAsync("backups/sample.zip", " ");

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Target directory cannot be null or empty*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldPropagateFileNotFound_WhenDownloadFails()
    {
        var storage = new Mock<IStorage>();
        storage.Setup(s => s.DownloadAsync("backups/missing.zip", It.IsAny<string>()))
            .ThrowsAsync(new FileNotFoundException("missing backup"));

        var restore = new Restore(_logger, storage.Object);

        var action = () => restore.RestoreFromBackupAsync("backups/missing.zip", Path.Combine(_testRoot, "restore"));

        await action.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenEncryptedBackupMetadataIsMissing()
    {
        var password = "CorrectPassword123!";
        var (storage, remotePath) = await CreateEncryptedBackupAsync(password);

        File.Delete(Path.Combine(_testRoot, "storage", remotePath.Replace('/', Path.DirectorySeparatorChar) + ".meta"));

        var passwordProvider = new Mock<IPasswordProvider>();
        passwordProvider.Setup(p => p.GetPasswordAsync()).ReturnsAsync(password);

        var restore = new Restore(_logger, storage, passwordProvider.Object);

        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-missing-meta"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to download encryption metadata*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenEncryptedBackupHasNoPasswordProvider()
    {
        var (storage, remotePath) = await CreateEncryptedBackupAsync("CorrectPassword123!");
        var restore = new Restore(_logger, storage);

        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-no-provider"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no password provider available*");

        var leakedMetadataFiles = Directory
            .EnumerateFiles(Path.GetTempPath(), "*.meta", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(Path.GetFileName(remotePath) + ".meta", StringComparison.OrdinalIgnoreCase))
            .ToList();

        leakedMetadataFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldThrow_WhenPasswordProviderReturnsEmptyPassword()
    {
        var (storage, remotePath) = await CreateEncryptedBackupAsync("CorrectPassword123!");
        var passwordProvider = new Mock<IPasswordProvider>();
        passwordProvider.Setup(p => p.GetPasswordAsync()).ReturnsAsync((string?)null);

        var restore = new Restore(_logger, storage, passwordProvider.Object);

        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-empty-password"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Password required to decrypt backup*");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ShouldClearPassword_WhenDecryptionFails()
    {
        var (storage, remotePath) = await CreateEncryptedBackupAsync("CorrectPassword123!");
        var passwordProvider = new Mock<IPasswordProvider>();
        passwordProvider.Setup(p => p.GetPasswordAsync()).ReturnsAsync("WrongPassword!");

        var restore = new Restore(_logger, storage, passwordProvider.Object);

        var action = () => restore.RestoreFromBackupAsync(remotePath, Path.Combine(_testRoot, "restore-wrong-password"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to decrypt backup*");

        passwordProvider.Verify(p => p.ClearPassword(), Times.Once);
    }

    private async Task<(LocalStorage Storage, string RemotePath)> CreateEncryptedBackupAsync(string password)
    {
        var sourceDir = Path.Combine(_testRoot, "source-" + Guid.NewGuid().ToString("N"));
        var storageDir = Path.Combine(_testRoot, "storage");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(storageDir);

        var sourceFile = Path.Combine(sourceDir, "data.txt");
        await File.WriteAllTextAsync(sourceFile, "secret content");

        var zipPath = Path.Combine(_testRoot, $"backup_{Guid.NewGuid():N}.zip");
        await CompressionUtil.CompressFilesAsync([sourceFile], sourceDir, zipPath);

        var encryptedPath = await CompressionUtil.CompressAndEncryptAsync(
            zipPath,
            password,
            Convert.ToBase64String(EncryptionService.GenerateSalt()),
            _logger);

        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDir });

        var remotePath = $"backups/source/{Path.GetFileName(encryptedPath)}";
        await storage.UploadAsync(encryptedPath, remotePath);
        await storage.UploadAsync(encryptedPath + ".meta", remotePath + ".meta");

        File.Delete(encryptedPath);
        File.Delete(encryptedPath + ".meta");

        return (storage, remotePath);
    }
}
