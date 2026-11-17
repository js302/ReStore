using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>F-4: per-run report, recorded by Backup into SystemState (bounded, persisted).</summary>
public class BackupRunReportTests : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "runreport_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();
    private readonly string _storageDir;
    private readonly string _source;
    private readonly SystemState _state;
    private readonly Mock<IConfigManager> _config = new();
    private readonly RepositoryCoordinator _coordinator;

    public BackupRunReportTests()
    {
        _storageDir = Path.Combine(_root, "storage");
        _source = Path.GetFullPath(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_storageDir);
        Directory.CreateDirectory(_source);
        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_root, "state.json"));

        _config.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        _config.Setup(c => c.GlobalStorageType).Returns("local");
        _config.Setup(c => c.SizeThresholdMB).Returns(100);
        _config.Setup(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _config.Setup(c => c.ExcludedPatterns).Returns([]);
        _config.Setup(c => c.ExcludedPaths).Returns([]);
        _config.Setup(c => c.BackupType).Returns(BackupType.Incremental);
        _config.Setup(c => c.WatchDirectories).Returns([]);
        _config.Setup(c => c.MaxFileSizeMB).Returns(100);
        _config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async () =>
        {
            var storage = new LocalStorage(_logger);
            await storage.InitializeAsync(new Dictionary<string, string> { { "path", _storageDir } });
            return storage;
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private Backup NewBackup(Func<string, Stream>? opener = null) =>
        new(_logger, _state, new SizeAnalyzer(), _config.Object, null, _coordinator) { SourceFileOpener = opener };

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Backup_RecordsAddedChangedRemovedAndBytes()
    {
        var a = Write("a.txt", "alpha");
        Write("b.txt", "bravo");
        await NewBackup().BackupDirectoryAsync(_source);

        var first = _state.GetRecentBackupRuns().Should().ContainSingle().Subject;
        first.FilesAdded.Should().Be(2);
        first.FilesChanged.Should().Be(0);
        first.BytesUploaded.Should().BeGreaterThan(0);
        first.Group.Should().Be(_source);

        File.WriteAllText(a, "alpha changed");
        Write("c.txt", "charlie");
        File.Delete(Path.Combine(_source, "b.txt"));
        await NewBackup().BackupDirectoryAsync(_source);

        var second = _state.GetRecentBackupRuns()[0];
        second.FilesAdded.Should().Be(1);
        second.FilesChanged.Should().Be(1);
        second.FilesRemoved.Should().Be(1);
        _state.GetRecentBackupRuns().Should().HaveCount(2);
    }

    [Fact]
    public async Task FailedBackup_IsRecordedInActivityLog()
    {
        var missing = Path.Combine(_root, "nope");

        var act = async () => await NewBackup().BackupDirectoryAsync(missing);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        var entry = _state.GetRecentActivity().Should().ContainSingle().Subject;
        entry.Type.Should().Be(ActivityType.Backup);
        entry.Result.Should().Be(ActivityResult.Failed);
        entry.Message.Should().Contain("not found");
        entry.EndUtc.Should().BeOnOrAfter(entry.StartUtc);
    }

    [Fact]
    public async Task CancelledBackup_IsRecordedAsCancelled()
    {
        Write("a.txt", "alpha");
        using var cts = new CancellationTokenSource();

        // Cancelled once the backup is running (after it holds the lease), as a user pressing Cancel would.
        var act = async () => await NewBackup(path =>
        {
            cts.Cancel();
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }).BackupDirectoryAsync(_source, null, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _state.GetRecentActivity().Should().ContainSingle().Which.Result.Should().Be(ActivityResult.Cancelled);
    }

    [Fact]
    public async Task Backup_WithSkippedFile_RecordsReason()
    {
        Write("good.txt", "good");
        Write("locked.pst", "x");

        await NewBackup(path => path.EndsWith("locked.pst", StringComparison.Ordinal)
            ? throw new IOException("in use")
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)).BackupDirectoryAsync(_source);

        var report = _state.GetRecentBackupRuns().Single();
        report.FilesSkipped.Should().Be(1);
        report.SkipReasons.Should().HaveCount(1);
        report.Summary.Should().Contain("1 skipped");
    }

    private string[] StoredChunkFiles() => Directory.GetFiles(Path.Combine(_storageDir, "chunks"), "*", SearchOption.AllDirectories);

    [Fact]
    public async Task Backup_WithTruncatedStoredChunk_ReuploadsItAndCountsItInTheReport()
    {
        var content = string.Join("\n", Enumerable.Range(0, 4000).Select(i => "line " + i * 7919));
        Write("a.txt", content);
        await NewBackup().BackupDirectoryAsync(_source);
        var chunkFile = StoredChunkFiles().OrderByDescending(f => new FileInfo(f).Length).First();
        var good = File.ReadAllBytes(chunkFile);
        File.WriteAllBytes(chunkFile, good[..(good.Length / 2)]);
        Write("b.txt", content);

        await NewBackup().BackupDirectoryAsync(_source);

        File.ReadAllBytes(chunkFile).Should().Equal(good, "the truncated object was uploaded again");
        var report = _state.GetRecentBackupRuns().OrderBy(r => r.TimestampUtc).Last();
        report.ChunksReplaced.Should().Be(1);
        report.BytesUploaded.Should().BeGreaterThan(0);
        report.Summary.Should().Contain("1 damaged chunk(s) re-uploaded");
        _logger.Messages.Should().Contain(m => m.Contains("replacing it"));
    }

    [Fact]
    public async Task Backup_WithIntactStoredChunk_ReusesItWithoutUploading()
    {
        var content = string.Join("\n", Enumerable.Range(0, 4000).Select(i => "line " + i * 7919));
        Write("a.txt", content);
        await NewBackup().BackupDirectoryAsync(_source);
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var file in StoredChunkFiles())
        {
            File.SetLastWriteTimeUtc(file, old);
        }

        Write("b.txt", content);

        await NewBackup().BackupDirectoryAsync(_source);

        StoredChunkFiles().Should().OnlyContain(f => File.GetLastWriteTimeUtc(f) == old, "an intact chunk is not uploaded again");
        var report = _state.GetRecentBackupRuns().OrderBy(r => r.TimestampUtc).Last();
        report.ChunksReplaced.Should().Be(0);
        report.Summary.Should().NotContain("damaged");
    }

    [Fact]
    public async Task NothingToBackUp_RecordsNoReport_ButCountsAsSuccessfulRun()
    {
        Write("a.txt", "alpha");
        await NewBackup().BackupDirectoryAsync(_source);
        var before = _state.GetLastSuccessfulRunUtc(_source);

        await Task.Delay(100); // beyond the coarse timer resolution so the second run's timestamp is strictly later
        await NewBackup().BackupDirectoryAsync(_source);

        _state.GetRecentBackupRuns().Should().ContainSingle();
        _state.GetLastSuccessfulRunUtc(_source).Should().BeAfter(before);
    }

    [Fact]
    public void Reports_AreBounded_NewestFirst()
    {
        for (var i = 0; i < SystemState.MaxBackupRunReports + 10; i++)
        {
            _state.RecordBackupRun(new BackupRunReport { Group = _source, TimestampUtc = DateTime.UtcNow, FilesAdded = i });
        }

        var runs = _state.GetRecentBackupRuns();
        runs.Should().HaveCount(SystemState.MaxBackupRunReports);
        runs[0].FilesAdded.Should().Be(SystemState.MaxBackupRunReports + 9);
    }

    [Fact]
    public async Task Reports_SurviveSaveAndLoad()
    {
        _state.RecordBackupRun(new BackupRunReport
        {
            Group = _source,
            TimestampUtc = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
            FilesAdded = 3,
            SkipReasons = { ["Access denied"] = 2 },
            FilesSkipped = 2
        });
        await _state.SaveStateAsync();

        var loaded = new SystemState(_logger);
        loaded.SetStateFilePath(Path.Combine(_root, "state.json"));
        await loaded.LoadStateAsync();

        var run = loaded.GetRecentBackupRuns().Should().ContainSingle().Subject;
        run.FilesAdded.Should().Be(3);
        run.SkipReasons["Access denied"].Should().Be(2);
        loaded.GetLastSuccessfulRunUtc(_source).Should().Be(new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Create_FoldsManyDistinctReasonsIntoOther()
    {
        var skipped = Enumerable.Range(0, 9).Select(i => new SkippedFile($"/f{i}", $"reason {i}", false)).ToList();

        var report = BackupRunReport.Create("/g", "local", [], [], 0, skipped, TimeSpan.FromSeconds(2), 10, DateTime.UtcNow);

        report.FilesSkipped.Should().Be(9);
        report.SkipReasons.Should().HaveCount(BackupRunReport.MaxSkipReasons);
        report.SkipReasons.Values.Sum().Should().Be(9);
        report.DurationMs.Should().Be(2000);
    }

    [Fact]
    public void GetRecentBackups_LimitsPerGroupAndDropsChunkLists()
    {
        for (var i = 0; i < 5; i++)
        {
            _state.AddSnapshotBackup(_source, $"s{i}", $"{_source}/s{i}.manifest.json", "local", ["c1", "c2"], 10, "root", false, null);
        }

        var recent = _state.GetRecentBackups(2);

        recent[_source].Should().HaveCount(2);
        recent[_source].Should().OnlyContain(b => b.ChunkIds.Count == 0);
    }
}
