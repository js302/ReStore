using FluentAssertions;
using Moq;
using ReStore.Core.src.sharing;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class ShareServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<IConfigManager> _configMock;
    private readonly Mock<ILogger> _loggerMock;
    private readonly Mock<IStorage> _storageMock;

    public ShareServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreShareServiceTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_testRoot);

        _configMock = new Mock<IConfigManager>();
        _loggerMock = new Mock<ILogger>();
        _storageMock = new Mock<IStorage>();

        _configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>()))
            .ReturnsAsync(_storageMock.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task ShareFileAsync_ShouldThrow_WhenLocalFileDoesNotExist()
    {
        var service = new ShareService(_configMock.Object, _loggerMock.Object);
        var missingFile = Path.Combine(_testRoot, "missing.txt");

        var action = () => service.ShareFileAsync(missingFile, "local", TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<FileNotFoundException>(action);
        _configMock.Verify(c => c.CreateStorageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ShareFileAsync_ShouldUploadAndGenerateLink_WhenSuccessful()
    {
        var service = new ShareService(_configMock.Object, _loggerMock.Object);
        var localFile = Path.Combine(_testRoot, "share-me.txt");
        await File.WriteAllTextAsync(localFile, "share content");

        _storageMock.SetupGet(s => s.SupportsSharing).Returns(true);

        _storageMock.Setup(s => s.GenerateShareLinkAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync("https://example.com/share");

        var link = await service.ShareFileAsync(localFile, "local", TimeSpan.FromHours(2));

        link.Should().Be("https://example.com/share");
        _configMock.Verify(c => c.CreateStorageAsync("local"), Times.Once);
        _storageMock.Verify(s => s.UploadAsync(localFile, It.Is<string>(p => p.StartsWith("shared/") && p.EndsWith("/share-me.txt"))), Times.Once);
        _storageMock.Verify(s => s.GenerateShareLinkAsync(It.Is<string>(p => p.StartsWith("shared/") && p.EndsWith("/share-me.txt")), TimeSpan.FromHours(2)), Times.Once);
        _storageMock.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public async Task ShareFileAsync_ShouldCleanupUploadedFile_WhenLinkGenerationFails()
    {
        var service = new ShareService(_configMock.Object, _loggerMock.Object);
        var localFile = Path.Combine(_testRoot, "cleanup.txt");
        await File.WriteAllTextAsync(localFile, "share content");

        _storageMock.SetupGet(s => s.SupportsSharing).Returns(true);

        _storageMock.Setup(s => s.GenerateShareLinkAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new InvalidOperationException("link generation failed"));

        var action = () => service.ShareFileAsync(localFile, "s3", TimeSpan.FromMinutes(30));

        await Assert.ThrowsAsync<InvalidOperationException>(action);
        _storageMock.Verify(s => s.UploadAsync(localFile, It.IsAny<string>()), Times.Once);
        _storageMock.Verify(s => s.DeleteAsync(It.Is<string>(p => p.StartsWith("shared/") && p.EndsWith("/cleanup.txt"))), Times.Once);
        _storageMock.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public async Task ShareFileAsync_ShouldThrowOriginalException_WhenCleanupAlsoFails()
    {
        var service = new ShareService(_configMock.Object, _loggerMock.Object);
        var localFile = Path.Combine(_testRoot, "cleanup-fails.txt");
        await File.WriteAllTextAsync(localFile, "share content");

        _storageMock.SetupGet(s => s.SupportsSharing).Returns(true);

        _storageMock.Setup(s => s.GenerateShareLinkAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new InvalidOperationException("link generation failed"));
        _storageMock.Setup(s => s.DeleteAsync(It.IsAny<string>()))
            .ThrowsAsync(new IOException("delete failed"));

        var action = () => service.ShareFileAsync(localFile, "azure", TimeSpan.FromMinutes(15));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(action);
        exception.Message.Should().Be("link generation failed");
        _storageMock.Verify(s => s.DeleteAsync(It.Is<string>(p => p.StartsWith("shared/") && p.EndsWith("/cleanup-fails.txt"))), Times.Once);
        _storageMock.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public async Task ShareFileAsync_ShouldThrowBeforeUpload_WhenProviderDoesNotSupportSharing()
    {
        var service = new ShareService(_configMock.Object, _loggerMock.Object);
        var localFile = Path.Combine(_testRoot, "unsupported.txt");
        await File.WriteAllTextAsync(localFile, "share content");

        _storageMock.SetupGet(s => s.SupportsSharing).Returns(false);

        var action = () => service.ShareFileAsync(localFile, "github", TimeSpan.FromHours(1));

        await action.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*does not support file sharing*");

        _storageMock.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
