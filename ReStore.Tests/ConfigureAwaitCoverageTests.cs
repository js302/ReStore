using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Core runs on pool threads under the GUI (see the threading model in the review, H1). These tests run the main
/// operations on a thread whose SynchronizationContext is never pumped and which blocks until the operation ends:
/// any await in Core that captured the context would post its continuation to that blocked thread and hang.
/// </summary>
public class ConfigureAwaitCoverageTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreCtxTests_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();

    public ConfigureAwaitCoverageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NeverPumpedContext : SynchronizationContext
    {
        public int PostCount;

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref PostCount);

        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref PostCount);
    }

    /// <summary>Runs <paramref name="operation"/> on a dedicated thread under the never-pumped context, blocking it.</summary>
    private static void RunUnderBlockedContext(Func<Task> operation, string what)
    {
        var context = new NeverPumpedContext();
        Exception? failure = null;
        var finished = false;

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var task = operation();
                finished = task.Wait(Timeout);
                if (finished)
                {
                    task.GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join(Timeout + TimeSpan.FromSeconds(10));

        failure.Should().BeNull($"{what} should not throw");
        finished.Should().BeTrue($"{what} must not capture the caller's SynchronizationContext (it would deadlock here)");
        context.PostCount.Should().Be(0, $"{what} must never post a continuation to the caller's context");
    }

    [Fact]
    public async Task BackupPreviewRestoreAndVerify_ShouldNotCaptureTheCallersSynchronizationContext()
    {
        var source = Path.Combine(_root, "source");
        var storageDirectory = Path.Combine(_root, "storage");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(storageDirectory);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        await File.WriteAllTextAsync(Path.Combine(source, "sub", "b.txt"), new string('b', 300_000));

        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDirectory });

        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        config.SetupGet(c => c.GlobalStorageType).Returns("local");
        config.SetupGet(c => c.SizeThresholdMB).Returns(500);
        config.SetupGet(c => c.ExcludedPatterns).Returns([]);
        config.SetupGet(c => c.ExcludedPaths).Returns([]);
        config.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        config.SetupGet(c => c.WatchDirectories).Returns([]);
        config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_root, "state.json"));

        RunUnderBlockedContext(
            () => new Backup(_logger, state, new SizeAnalyzer(), config.Object, null).BackupDirectoryAsync(source),
            "backup");

        var manifestPath = state.GetPreviousBackupPath(Path.GetFullPath(source));
        manifestPath.Should().NotBeNullOrWhiteSpace();

        RunUnderBlockedContext(
            () => new Restore(_logger, storage, null, state).PreviewRestoreAsync(manifestPath!, target),
            "restore preview");

        RunUnderBlockedContext(
            () => new Restore(_logger, storage, null, state).RestoreFromBackupAsync(manifestPath!, target, RestoreOptions.Default),
            "restore");

        RunUnderBlockedContext(
            () => new SnapshotIntegrityVerifier(_logger, storage, null, state).VerifyAsync(manifestPath!),
            "verify");

        (await File.ReadAllTextAsync(Path.Combine(target, "a.txt"))).Should().Be("alpha");
        new FileInfo(Path.Combine(target, "sub", "b.txt")).Length.Should().Be(300_000);
    }
}
