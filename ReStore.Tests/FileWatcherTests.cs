using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;
using System.Collections.Concurrent;
using System.Reflection;

namespace ReStore.Tests;

public class FileWatcherTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _watchDir;
    private readonly TestLogger _logger;
    private readonly Mock<IConfigManager> _configMock;
    private readonly SystemState _state;
    private readonly SizeAnalyzer _sizeAnalyzer;

    public FileWatcherTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreFileWatcherTests_" + Guid.NewGuid());
        _watchDir = Path.Combine(_testRoot, "watch");
        Directory.CreateDirectory(_watchDir);

        _logger = new TestLogger();
        _configMock = new Mock<IConfigManager>();
        _state = new SystemState(_logger);
        _sizeAnalyzer = new SizeAnalyzer();

        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = _watchDir, StorageType = null }
        ]);
        _configMock.SetupGet(c => c.ExcludedPatterns).Returns([]);
        _configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        _configMock.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        _configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig());
        _configMock.SetupGet(c => c.GlobalStorageType).Returns("local");
        _configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _configMock.SetupGet(c => c.SizeThresholdMB).Returns(500);
        _configMock.SetupGet(c => c.BackupType).Returns(BackupType.Incremental);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task StartAsync_ShouldLogWarning_WhenWatchDirectoryMissing()
    {
        var missing = Path.Combine(_testRoot, "missing-dir");
        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = missing, StorageType = null }
        ]);

        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer);
        await watcher.StartAsync();

        _logger.Messages.Should().Contain(m => m.Contains("Directory not found, cannot watch"));
    }

    [Fact]
    public async Task StartAsync_ShouldLogWatchingDirectory_WhenDirectoryExists()
    {
        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer);
        await watcher.StartAsync();

        _logger.Messages.Should().Contain(m => m.Contains($"Watching directory: {_watchDir}"));
    }

    [Fact]
    public async Task OnChanged_ShouldTrackFile_WhenNotExcluded()
    {
        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var changedFile = Path.Combine(_watchDir, "file.txt");
        var args = new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, "file.txt");
        method!.Invoke(watcher, [this, args]);

        var changed = GetChangedFiles(watcher);
        changed.ContainsKey(changedFile).Should().BeTrue();
    }

    [Fact]
    public async Task OnChanged_ShouldIgnoreFile_WhenPatternExcluded()
    {
        _configMock.SetupGet(c => c.ExcludedPatterns).Returns(["*.tmp"]);

        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var excludedFile = Path.Combine(_watchDir, "skip.tmp");
        var args = new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, "skip.tmp");
        method!.Invoke(watcher, [this, args]);

        var changed = GetChangedFiles(watcher);
        changed.ContainsKey(excludedFile).Should().BeFalse();
    }

    [Fact]
    public async Task OnRenamed_ShouldTrackNewFilePath_WhenNotExcluded()
    {
        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("OnRenamed", BindingFlags.Instance | BindingFlags.NonPublic);
        var renamedFile = Path.Combine(_watchDir, "renamed.txt");
        var args = new RenamedEventArgs(WatcherChangeTypes.Renamed, _watchDir, "renamed.txt", "old.txt");
        method!.Invoke(watcher, [this, args]);

        var changed = GetChangedFiles(watcher);
        changed.ContainsKey(renamedFile).Should().BeTrue();
    }

    [Fact]
    public async Task FindWatchedRoot_ShouldReturnMostSpecificRoot()
    {
        var parent = Path.Combine(_testRoot, "parent");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);

        _configMock.SetupGet(c => c.WatchDirectories).Returns([
            new WatchDirectoryConfig { Path = parent, StorageType = null },
            new WatchDirectoryConfig { Path = child, StorageType = null }
        ]);

        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("FindWatchedRoot", BindingFlags.Instance | BindingFlags.NonPublic);
        var fileInChild = Path.Combine(child, "nested", "file.txt");
        var root = (string?)method!.Invoke(watcher, [fileInChild]);

        root.Should().Be(Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    [Fact]
    public async Task FindWatchedRoot_ShouldReturnNull_WhenPathNotUnderWatchedRoots()
    {
        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("FindWatchedRoot", BindingFlags.Instance | BindingFlags.NonPublic);
        var externalPath = Path.Combine(_testRoot, "external", "file.txt");
        var root = (string?)method!.Invoke(watcher, [externalPath]);

        root.Should().BeNull();
    }

    [Fact]
    public async Task OnError_ShouldLogWatcherException()
    {
        using var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer);
        await watcher.StartAsync();

        var method = typeof(FileWatcher).GetMethod("OnError", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var exception = new IOException("watcher boom");
        var args = new ErrorEventArgs(exception);
        method!.Invoke(watcher, [this, args]);

        _logger.Messages.Should().Contain(m => m.Contains("File watcher error") && m.Contains("watcher boom"));
    }

    [Fact]
    public async Task Dispose_ShouldBeIdempotent()
    {
        var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer);
        await watcher.StartAsync();

        watcher.Dispose();
        var action = () => watcher.Dispose();

        action.Should().NotThrow();
    }

    [Fact]
    public async Task OnChanged_ShouldDoNothing_AfterDispose()
    {
        var watcher = new FileWatcher(_configMock.Object, _logger, _state, _sizeAnalyzer);
        await watcher.StartAsync();
        watcher.Dispose();

        var method = typeof(FileWatcher).GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var args = new FileSystemEventArgs(WatcherChangeTypes.Changed, _watchDir, "after-dispose.txt");
        method!.Invoke(watcher, [this, args]);

        var changed = GetChangedFiles(watcher);
        changed.Count.Should().Be(0);
    }

    private static ConcurrentDictionary<string, DateTime> GetChangedFiles(FileWatcher watcher)
    {
        var field = typeof(FileWatcher).GetField("_changedFiles", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return (ConcurrentDictionary<string, DateTime>)field!.GetValue(watcher)!;
    }
}
