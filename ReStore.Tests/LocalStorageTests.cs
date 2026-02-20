using FluentAssertions;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class LocalStorageTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _storageRoot;
    private readonly string _outsideRoot;
    private readonly ILogger _logger;

    public LocalStorageTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreLocalStorageTests_" + Guid.NewGuid());
        _storageRoot = Path.Combine(_testRoot, "storage");
        _outsideRoot = Path.Combine(_testRoot, "outside");

        Directory.CreateDirectory(_storageRoot);
        Directory.CreateDirectory(_outsideRoot);

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
    public async Task UploadAsync_ShouldThrow_WhenRemotePathEscapesStorageRoot()
    {
        // Arrange
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageRoot });

        var sourceFile = Path.Combine(_testRoot, "source.txt");
        await File.WriteAllTextAsync(sourceFile, "content");

        var escapedPath = "..\\outside\\escaped.txt";

        // Act
        var action = () => storage.UploadAsync(sourceFile, escapedPath);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(action);
        File.Exists(Path.Combine(_outsideRoot, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_ShouldThrow_WhenRemotePathIsRooted()
    {
        // Arrange
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageRoot });

        var rootedPath = Path.Combine(Path.GetPathRoot(_storageRoot)!, "Windows", "Temp", "evil.txt");

        // Act
        var action = () => storage.ExistsAsync(rootedPath);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(action);
    }
}
