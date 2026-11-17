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
    public async Task Uninitialized_RefusesEveryPath_InsteadOfUsingTheWorkingDirectory()
    {
        var storage = new LocalStorage(_logger);
        var source = Path.Combine(Path.GetTempPath(), "ReStoreUninit_" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(source, "x");
        var stray = Path.Combine(Directory.GetCurrentDirectory(), "uninit-probe", "x.txt");

        try
        {
            await FluentActions.Awaiting(() => storage.ExistsAsync("uninit-probe/x.txt")).Should()
                .ThrowAsync<InvalidOperationException>().WithMessage("*not initialized*");
            await FluentActions.Awaiting(() => storage.UploadAsync(source, "uninit-probe/x.txt")).Should()
                .ThrowAsync<InvalidOperationException>().WithMessage("*not initialized*");
            File.Exists(stray).Should().BeFalse("nothing may be written relative to the working directory");
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task UploadAsync_ShouldThrow_WhenRemotePathEscapesStorageRoot()
    {
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageRoot });

        var sourceFile = Path.Combine(_testRoot, "source.txt");
        await File.WriteAllTextAsync(sourceFile, "content");

        var escapedPath = "..\\outside\\escaped.txt";

        var action = () => storage.UploadAsync(sourceFile, escapedPath);

        await Assert.ThrowsAsync<InvalidOperationException>(action);
        File.Exists(Path.Combine(_outsideRoot, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_ShouldThrow_WhenRemotePathIsRooted()
    {
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageRoot });

        var rootedPath = Path.Combine(Path.GetPathRoot(_storageRoot)!, "Windows", "Temp", "evil.txt");

        var action = () => storage.ExistsAsync(rootedPath);

        await Assert.ThrowsAsync<InvalidOperationException>(action);
    }
}
