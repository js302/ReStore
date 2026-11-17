using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>P6-E: storage usage math (shared chunks, compression, reclaimable) and the activity log.</summary>
public class StorageUsageTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "usage_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();
    private readonly SystemState _state;

    public StorageUsageTests()
    {
        Directory.CreateDirectory(_root);
        _state = NewState();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private SystemState NewState()
    {
        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_root, "state.json"));
        return state;
    }

    private static string Id(char c) => new(c, 64);

    private void Snap(string group, string name, int daysAgo, string chunks, long logical, long stored, long plain, string ns = "ns", string storage = "local")
    {
        _state.AdoptSnapshotBackup(
            group, name, $"snapshots/{group}/{name}.manifest.json", storage, Now.AddDays(-daysAgo),
            chunks.Select(Id), logical, "root", false, ns,
            stored == 0 ? null : new SnapshotChunkUsage(stored, plain));
    }

    private Task<StorageUsageReport> Compute(RetentionConfig? policy = null)
        => StorageUsageCalculator.ComputeAsync(_state, policy, "local", Now);

    [Fact]
    public async Task SharedChunksAcrossSnapshotsAreCountedOnce()
    {
        Snap("g", "a", 2, "ab", logical: 1000, stored: 200, plain: 400);
        Snap("g", "b", 1, "bc", logical: 1100, stored: 200, plain: 400);

        var report = await Compute();

        report.TotalSnapshots.Should().Be(2);
        report.TotalStoredBytes.Should().Be(300);
        report.TotalPlainBytes.Should().Be(600);
        report.TotalCompressionSavedBytes.Should().Be(300);
        report.TotalDedupSavedBytes.Should().Be(100);
        var row = report.Groups.Should().ContainSingle().Subject;
        row.SnapshotCount.Should().Be(2);
        row.LatestLogicalBytes.Should().Be(1100);
        row.AllSnapshotsLogicalBytes.Should().Be(2100);
        row.StoredBytes.Should().Be(300);
        row.Growth.Select(p => p.AddedStoredBytes).Should().Equal(200, 100);
        row.Growth.Select(p => p.CumulativeStoredBytes).Should().Equal(200, 300);
        report.Growth.Select(p => p.CumulativeStoredBytes).Should().Equal(200, 300);
        report.HasEstimates.Should().BeFalse();
    }

    [Fact]
    public async Task ChunksSharedBetweenGroupsInOneNamespaceCountOnceInTotals()
    {
        Snap("one", "a", 2, "xy", 100, 200, 200);
        Snap("two", "b", 1, "xz", 100, 200, 200);

        var report = await Compute();

        report.Groups.Should().HaveCount(2);
        report.Groups.Sum(g => g.StoredBytes).Should().Be(400);
        report.TotalStoredBytes.Should().Be(300);
    }

    [Fact]
    public async Task SameChunkIdInDifferentNamespacesOrStoragesIsNotShared()
    {
        Snap("g", "a", 2, "x", 100, 100, 100, ns: "n1");
        Snap("g", "b", 1, "x", 100, 100, 100, ns: "n2");
        Snap("g", "c", 1, "x", 100, 100, 100, ns: "n1", storage: "s3");

        var report = await Compute();

        report.TotalStoredBytes.Should().Be(300);
        report.Groups.Select(g => g.StorageType).Should().BeEquivalentTo("local", "s3");
    }

    [Fact]
    public async Task EncryptionOverheadIsNotACompressionSaving()
    {
        Snap("g", "a", 1, "ab", 100, stored: 300, plain: 200);

        var report = await Compute();

        report.TotalCompressionSavedBytes.Should().Be(0);
    }

    [Fact]
    public async Task SnapshotWithoutRecordedSizesIsEstimatedFromFileSize()
    {
        Snap("g", "old", 1, "ab", logical: 1000, stored: 0, plain: 0);

        var report = await Compute();

        report.HasEstimates.Should().BeTrue();
        report.Groups[0].HasEstimates.Should().BeTrue();
        report.TotalStoredBytes.Should().Be(1000);
    }

    [Fact]
    public async Task ReclaimableCountsOnlyChunksNoKeptSnapshotReferences()
    {
        Snap("g", "a", 3, "ab", 100, 200, 200);
        Snap("g", "b", 1, "bc", 100, 200, 200);
        var policy = new RetentionConfig { Enabled = true, KeepLastPerDirectory = 1, MaxAgeDays = 0 };

        var report = await Compute(policy);

        // 'a' is deleted; its chunk 'a' (100) is free, chunk 'b' is still used by the kept snapshot.
        report.TotalReclaimableSnapshots.Should().Be(1);
        report.TotalReclaimableBytes.Should().Be(100);
        report.Groups[0].ReclaimableBytes.Should().Be(100);
        report.Groups[0].ReclaimableSnapshots.Should().Be(1);
        report.RetentionEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ReclaimableKeepsChunksAnotherGroupStillUses()
    {
        Snap("one", "a", 3, "ab", 100, 200, 200);
        Snap("one", "a2", 1, "c", 100, 100, 100);
        Snap("two", "b", 1, "b", 100, 100, 100);
        var policy = new RetentionConfig { Enabled = true, KeepLastPerDirectory = 1, MaxAgeDays = 0 };

        var report = await Compute(policy);

        report.TotalReclaimableBytes.Should().Be(100); // only 'a'; 'b' lives on in group two
    }

    [Fact]
    public async Task NoPolicyMeansNothingReclaimable()
    {
        Snap("g", "a", 3, "ab", 100, 200, 200);
        Snap("g", "b", 1, "bc", 100, 200, 200);

        (await Compute()).TotalReclaimableBytes.Should().Be(0);
    }

    [Fact]
    public async Task ArchiveBackupsCountAtTheirSize()
    {
        _state.AddBackup("g", "backups/a.zip", false, "local", 500);

        var report = await Compute();

        report.TotalStoredBytes.Should().Be(500);
        report.Groups[0].AllSnapshotsLogicalBytes.Should().Be(500);
    }

    [Fact]
    public async Task UsageSurvivesSaveAndLoadAndSidecarsAreUsed()
    {
        Snap("g", "a", 2, "ab", 1000, 200, 400);
        Snap("g", "b", 1, "bc", 1100, 200, 400);
        await _state.SaveStateAsync();

        var reloaded = NewState();
        await reloaded.LoadStateAsync();
        var report = await StorageUsageCalculator.ComputeAsync(reloaded, null, "local", Now);

        report.TotalStoredBytes.Should().Be(300);
        report.SnapshotsWithoutChunkList.Should().Be(0);
    }

    [Fact]
    public async Task StateWithoutUsageFieldsStillLoads()
    {
        var json = """{"BackupHistory":{"g":[{"Path":"a.zip","Timestamp":"2026-05-01T00:00:00Z","SizeBytes":42}]}}""";
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "state.json"), json);
        var state = NewState();

        await state.LoadStateAsync();

        state.GetBackupsForGroup("g").Should().ContainSingle().Which.StoredBytes.Should().Be(0);
        state.GetRecentActivity().Should().BeEmpty();
    }

    [Fact]
    public async Task MeasureAsyncSumsChunkObjectsOnly()
    {
        var storage = new InMemoryStorage(_logger);
        storage.Store["chunks/ab/" + Id('a') + ".chunk"] = new byte[10];
        storage.Store["chunks/ns/cd/" + Id('c') + ".chunk"] = new byte[5];
        storage.Store["snapshots/g/x.manifest.json"] = new byte[1000];

        var measured = await StorageUsageCalculator.MeasureAsync(storage);

        measured.ChunkCount.Should().Be(2);
        measured.Bytes.Should().Be(15);
    }

    [Fact]
    public void ManifestUsageCountsEachChunkOnce()
    {
        var manifest = new SnapshotManifest
        {
            Files =
            [
                new SnapshotFileManifestEntry { Chunks = [new SnapshotChunkManifestEntry { ChunkId = "A", PlainSizeBytes = 100, StoredSizeBytes = 40 }] },
                new SnapshotFileManifestEntry { Chunks = [new SnapshotChunkManifestEntry { ChunkId = "a", PlainSizeBytes = 100, StoredSizeBytes = 40 }, new SnapshotChunkManifestEntry { ChunkId = "b", PlainSizeBytes = 50, StoredSizeBytes = 50 }] }
            ]
        };

        SnapshotChunkUsage.FromManifest(manifest).Should().Be(new SnapshotChunkUsage(90, 150));
    }

    // ---- activity log ----

    [Fact]
    public void ActivityIsBoundedNewestFirst()
    {
        for (var i = 0; i < SystemState.MaxActivityEntries + 25; i++)
        {
            _state.RecordActivity(new ActivityEntry { Target = "t" + i, StartUtc = Now.AddMinutes(i), EndUtc = Now.AddMinutes(i) });
        }

        var recent = _state.GetRecentActivity();

        recent.Should().HaveCount(SystemState.MaxActivityEntries);
        recent[0].Target.Should().Be("t" + (SystemState.MaxActivityEntries + 24));
        recent[^1].Target.Should().Be("t25");
    }

    [Fact]
    public async Task ActivityIsPersistedAndMessagesAreTruncated()
    {
        _state.RecordActivity(new ActivityEntry
        {
            Type = ActivityType.Verify, Target = "snap", StartUtc = Now, EndUtc = Now.AddSeconds(3),
            Result = ActivityResult.Warning, Message = new string('x', 5000)
        });
        await _state.SaveStateAsync();

        var reloaded = NewState();
        await reloaded.LoadStateAsync();

        var entry = reloaded.GetRecentActivity().Should().ContainSingle().Subject;
        entry.Type.Should().Be(ActivityType.Verify);
        entry.Result.Should().Be(ActivityResult.Warning);
        entry.Message.Length.Should().BeLessThan(1000);
        entry.Duration.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void ScopeRecordsOutcome()
    {
        using (var scope = ActivityRecorder.Begin(_state, ActivityType.Restore, "x"))
        {
            scope.Succeeded("done");
        }

        var entry = _state.GetRecentActivity().Should().ContainSingle().Subject;
        entry.Result.Should().Be(ActivityResult.Succeeded);
        entry.Message.Should().Be("done");
        entry.Target.Should().Be("x");
    }

    [Fact]
    public void ScopeWithoutOutcomeIsFailedOrCancelled()
    {
        using (ActivityRecorder.Begin(_state, ActivityType.Repair, "a"))
        {
        }

        using var cts = new CancellationTokenSource();
        using (ActivityRecorder.Begin(_state, ActivityType.Repair, "b", cts.Token))
        {
            cts.Cancel();
        }

        var recent = _state.GetRecentActivity();
        recent.Single(a => a.Target == "a").Result.Should().Be(ActivityResult.Failed);
        recent.Single(a => a.Target == "b").Result.Should().Be(ActivityResult.Cancelled);
    }

    [Fact]
    public void ScopeWithNoStateIsHarmless()
    {
        var act = () => ActivityRecorder.Begin(null, ActivityType.Verify, "x").Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void TimelineMergesRunsAndEntriesNewestFirstAndFilters()
    {
        var runs = new[]
        {
            new BackupRunReport { TimestampUtc = Now.AddHours(-1), Group = @"C:\docs", StorageType = "local", FilesAdded = 2, DurationMs = 4000, BytesUploaded = 2048 },
            new BackupRunReport { TimestampUtc = Now.AddHours(-5), Group = @"C:\pics", FilesSkipped = 1, SkipReasons = new() { ["Access denied"] = 1 } }
        };
        var entries = new[]
        {
            new ActivityEntry { Type = ActivityType.Verify, Target = "m", StartUtc = Now.AddHours(-3), EndUtc = Now.AddHours(-3), Result = ActivityResult.Failed, Message = "bad chunk" },
            new ActivityEntry { Type = ActivityType.Restore, Target = "r", StartUtc = Now.AddHours(-2), EndUtc = Now.AddHours(-2), Result = ActivityResult.Succeeded }
        };

        var all = ActivityTimeline.Build(runs, entries);

        all.Select(i => i.Type).Should().Equal(ActivityType.Backup, ActivityType.Restore, ActivityType.Verify, ActivityType.Backup);
        all[0].Result.Should().Be(ActivityResult.Succeeded);
        all[0].StartUtc.Should().Be(Now.AddHours(-1).AddSeconds(-4));
        all[0].Message.Should().Contain("2 added").And.Contain("2 KB uploaded");
        all[3].Result.Should().Be(ActivityResult.Warning);
        ActivityTimeline.Build(runs, entries, type: ActivityType.Backup).Should().HaveCount(2);
        ActivityTimeline.Build(runs, entries, result: ActivityResult.Failed).Should().ContainSingle().Which.Message.Should().Be("bad chunk");
        ActivityTimeline.Build(runs, entries, max: 1).Should().HaveCount(1);
        all[2].ToDetailsText().Should().Contain("Operation: Verify").And.Contain("Result: Failed").And.Contain("bad chunk");
    }

    // ---- global status bar text ----

    [Fact]
    public void StatusShowsRunningOperationWithElapsedTime()
    {
        var status = GlobalStatus.Build("Backing up C:\\docs", TimeSpan.FromSeconds(75), ProtectionState.Running, null, Now, null);

        status.Operation.Should().Be("Backing up C:\\docs (1:15)");
        status.Level.Should().Be(GlobalStatusLevel.Busy);
        status.Protection.Should().Be("Protection on");
        status.NextRun.Should().StartWith("Next run ");
        status.LastBackup.Should().Be("No backups yet");
    }

    [Fact]
    public void StatusSaysWhenTheNextRunIsHeldBackByTheBackupWindow()
    {
        var held = GlobalStatus.Build(null, TimeSpan.Zero, ProtectionState.Running, null, Now, null, nextRunHeldByWindow: true);
        var plain = GlobalStatus.Build(null, TimeSpan.Zero, ProtectionState.Running, null, Now, null, nextRunHeldByWindow: false);

        held.NextRun.Should().StartWith("Next run ").And.EndWith(" (waiting for the backup window)");
        plain.NextRun.Should().NotContain("backup window");
        held.NextRun.Should().Be(GlobalStatus.NextRunText(Now, true));
        held.NextRun.Replace(" (waiting for the backup window)", string.Empty).Should().Be(plain.NextRun);

        // Nothing is shown for a stopped protection, held or not.
        GlobalStatus.Build(null, TimeSpan.Zero, ProtectionState.Stopped, null, Now, null, nextRunHeldByWindow: true).NextRun.Should().Be("No run scheduled");
    }

    [Theory]
    [InlineData(ProtectionState.Stopped, GlobalStatusLevel.Idle, "Protection off", "No run scheduled")]
    [InlineData(ProtectionState.PausedNeedsPassword, GlobalStatusLevel.Warning, "Needs password", "Next run")]
    [InlineData(ProtectionState.Error, GlobalStatusLevel.Error, "Protection error", "No run scheduled")]
    public void StatusReflectsProtectionState(ProtectionState state, GlobalStatusLevel level, string text, string next)
    {
        var status = GlobalStatus.Build(null, TimeSpan.Zero, state, null, state == ProtectionState.Stopped ? null : Now, null);

        status.Operation.Should().Be("Idle");
        status.Level.Should().Be(level);
        status.Protection.Should().Be(text);
        status.NextRun.Should().StartWith(next);
    }

    [Fact]
    public void StatusShowsPauseAndLastBackupResult()
    {
        var run = new BackupRunReport { TimestampUtc = Now, FilesSkipped = 2 };

        var status = GlobalStatus.Build(null, TimeSpan.Zero, ProtectionState.Stopped, Now.AddHours(1), null, run);

        status.Protection.Should().Be("Protection paused");
        status.NextRun.Should().StartWith("Resumes ");
        status.LastBackup.Should().EndWith("done, 2 skipped");
        GlobalStatus.FormatElapsed(TimeSpan.FromSeconds(3725)).Should().Be("1:02:05");
    }

    [Fact]
    public void RecordFailureMapsExceptionsAndSkipsBusy()
    {
        var start = Now;
        ActivityRecorder.RecordFailure(_state, ActivityType.Backup, "a", start, new IOException("disk full"));
        ActivityRecorder.RecordFailure(_state, ActivityType.Backup, "b", start, null);
        ActivityRecorder.RecordFailure(_state, ActivityType.Backup, "c", start, new OperationCanceledException());
        ActivityRecorder.RecordFailure(_state, ActivityType.Backup, "d", start, new RepositoryBusyException("busy"));

        var recent = _state.GetRecentActivity();
        recent.Should().HaveCount(3);
        recent.Single(a => a.Target == "a").Result.Should().Be(ActivityResult.Failed);
        recent.Single(a => a.Target == "a").Message.Should().Be("disk full");
        recent.Single(a => a.Target == "a").StartUtc.Should().Be(start);
        recent.Single(a => a.Target == "b").Result.Should().Be(ActivityResult.Cancelled);
        recent.Single(a => a.Target == "c").Result.Should().Be(ActivityResult.Cancelled);
    }

    [Fact]
    public async Task TryPersistAsyncSavesActivityAndSkipsWhenBusy()
    {
        var coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        _state.RecordActivity(new ActivityEntry { Target = "kept", StartUtc = Now, EndUtc = Now });

        await using (var held = await coordinator.AcquireMutationAsync("busy"))
        {
            await ActivityRecorder.TryPersistAsync(_state, coordinator, TimeSpan.FromMilliseconds(50));
            File.Exists(Path.Combine(_root, "state.json")).Should().BeFalse();
        }

        await ActivityRecorder.TryPersistAsync(_state, coordinator);

        var reloaded = NewState();
        await reloaded.LoadStateAsync();
        reloaded.GetRecentActivity().Should().ContainSingle().Which.Target.Should().Be("kept");
    }

    [Fact]
    public async Task SnapshotWithoutChunkListAddsNothingToDedup()
    {
        Snap("g", "a", 1, "ab", 100, 200, 200);
        _state.AdoptSnapshotBackup("g", "x", "snapshots/g/x.manifest.json", "local", Now, [], 5000, "r", false, "ns", new SnapshotChunkUsage(900, 900));

        var report = await Compute();

        report.TotalDedupSavedBytes.Should().Be(0);
        report.TotalStoredBytes.Should().Be(200);
    }
}
