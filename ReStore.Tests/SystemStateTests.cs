using Moq;
using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class SystemStateTests : IDisposable
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly string _testDir;
    private readonly string _stateFile;

    public SystemStateTests()
    {
        _loggerMock = new Mock<ILogger>();
        _testDir = Path.Combine(Path.GetTempPath(), "ReStoreStateTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
        _stateFile = Path.Combine(_testDir, "state.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task AddOrUpdateFileMetadataAsync_ShouldCalculateHash_WhenFileExists()
    {
        var state = new SystemState(_loggerMock.Object);
        state.SetStateFilePath(_stateFile);

        var filePath = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "content");

        await state.AddOrUpdateFileMetadataAsync(filePath);

        state.FileMetadata.Should().ContainKey(filePath);
        state.FileMetadata[filePath].Hash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetChangedFiles_ShouldReturnFile_WhenHashChanges()
    {
        var state = new SystemState(_loggerMock.Object);
        state.SetStateFilePath(_stateFile);

        var filePath = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "content1");

        await state.AddOrUpdateFileMetadataAsync(filePath);

        await File.WriteAllTextAsync(filePath, "content2");

        var changedFiles = state.GetChangedFiles(new List<string> { filePath }, BackupType.Incremental);

        changedFiles.Should().Contain(filePath);
    }

    [Fact]
    public async Task SaveAndLoadState_ShouldPersistData()
    {
        var state = new SystemState(_loggerMock.Object);
        state.SetStateFilePath(_stateFile);

        var filePath = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "content");
        await state.AddOrUpdateFileMetadataAsync(filePath);
        state.AddBackup(_testDir, "remote/path", false);

        await state.SaveStateAsync();

        var newState = new SystemState(_loggerMock.Object);
        newState.SetStateFilePath(_stateFile);
        await newState.LoadStateAsync();

        newState.FileMetadata.Should().ContainKey(filePath);
        newState.BackupHistory.Should().ContainKey(_testDir);
        newState.FileMetadata[filePath].Hash.Should().Be(state.FileMetadata[filePath].Hash);
    }

    [Fact]
    public void GetPreviousBackupPath_ShouldReturnMostRecentBackup()
    {
        var state = new SystemState(_loggerMock.Object)
        {
            BackupHistory = new Dictionary<string, List<BackupInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                [_testDir] =
                [
                    new BackupInfo { Path = "backups/older.zip", Timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                    new BackupInfo { Path = "backups/newer.zip", Timestamp = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) }
                ]
            }
        };

        state.GetPreviousBackupPath(_testDir).Should().Be("backups/newer.zip");
    }

    [Fact]
    public void GetBaseBackupPath_ShouldReturnLatestFullBackupBeforeDiff()
    {
        var state = new SystemState(_loggerMock.Object)
        {
            BackupHistory = new Dictionary<string, List<BackupInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                ["group"] =
                [
                    new BackupInfo
                    {
                        Path = "backups/backup_docs_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_20240101000000.zip",
                        Timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        IsDiff = false
                    },
                    new BackupInfo
                    {
                        Path = "backups/backup_docs_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb_20240115000000.zip",
                        Timestamp = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc),
                        IsDiff = false
                    },
                    new BackupInfo
                    {
                        Path = "backups/backup_docs_cccccccccccccccccccccccccccccccc_20240201000000.diff",
                        Timestamp = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                        IsDiff = true
                    }
                ]
            }
        };

        var result = state.GetBaseBackupPath("backups/backup_docs_dddddddddddddddddddddddddddddddd_20240202000000.diff");

        result.Should().Be("backups/backup_docs_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb_20240115000000.zip");
    }

    [Fact]
    public void RemoveBackupsFromGroup_ShouldRemoveGroup_WhenAllBackupsAreRemoved()
    {
        var state = new SystemState(_loggerMock.Object)
        {
            BackupHistory = new Dictionary<string, List<BackupInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                ["group"] =
                [
                    new BackupInfo { Path = "backups/only.zip", Timestamp = DateTime.UtcNow }
                ]
            }
        };

        state.RemoveBackupsFromGroup("group", ["backups/only.zip"]);

        state.BackupHistory.Should().NotContainKey("group");
    }

    [Fact]
    public async Task LoadStateAsync_ShouldInitializeEmptyState_WhenStateFileContainsInvalidJson()
    {
        await File.WriteAllTextAsync(_stateFile, "{ invalid json }");

        var state = new SystemState(_loggerMock.Object)
        {
            LastBackupTime = DateTime.UtcNow,
            BackupHistory = new Dictionary<string, List<BackupInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                ["group"] = [new BackupInfo { Path = "backups/file.zip" }]
            },
            FileMetadata = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["file"] = new FileMetadata { FilePath = "file", Hash = "hash" }
            }
        };
        state.SetStateFilePath(_stateFile);

        await state.LoadStateAsync();

        state.LastBackupTime.Should().Be(DateTime.MinValue);
        state.BackupHistory.Should().BeEmpty();
        state.FileMetadata.Should().BeEmpty();
    }

    [Fact]
    public void GetTrackedFilesInDirectory_ShouldIncludeNestedFilesOnly()
    {
        var trackedFile = Path.Combine(_testDir, "root.txt");
        var nestedFile = Path.Combine(_testDir, "Nested", "nested.txt");
        var otherFile = Path.Combine(_testDir, "..", "outside.txt");

        var state = new SystemState(_loggerMock.Object)
        {
            FileMetadata = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [trackedFile] = new FileMetadata { FilePath = trackedFile, Hash = "a" },
                [nestedFile] = new FileMetadata { FilePath = nestedFile, Hash = "b" },
                [otherFile] = new FileMetadata { FilePath = otherFile, Hash = "c" }
            }
        };

        var trackedFiles = state.GetTrackedFilesInDirectory(_testDir);

        trackedFiles.Should().Contain(trackedFile);
        trackedFiles.Should().Contain(nestedFile);
        trackedFiles.Should().NotContain(otherFile);
    }

    [Fact]
    public void GetChangedFiles_ShouldReturnAllFiles_ForFullBackup()
    {
        var state = new SystemState(_loggerMock.Object);
        var files = new List<string> { "a.txt", "b.txt" };

        var changedFiles = state.GetChangedFiles(files, BackupType.Full);

        changedFiles.Should().Equal(files);
    }

    [Fact]
    public async Task GetChangedFiles_ShouldIncludeFile_WhenTimestampChangesAndHashIsMissing()
    {
        var state = new SystemState(_loggerMock.Object);
        var filePath = Path.Combine(_testDir, "timestamp-only.txt");
        await File.WriteAllTextAsync(filePath, "abc");

        var fileInfo = new FileInfo(filePath);
        state.FileMetadata[filePath] = new FileMetadata
        {
            FilePath = filePath,
            Size = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc.AddMinutes(-5),
            Hash = string.Empty
        };

        var changedFiles = state.GetChangedFiles([filePath], BackupType.Incremental);

        changedFiles.Should().Contain(filePath);
    }

    [Fact]
    public async Task GetChangedFiles_ShouldUseChunkSnapshotChangeDetection()
    {
        var state = new SystemState(_loggerMock.Object);
        var fileA = Path.Combine(_testDir, "group-a.txt");
        var fileB = Path.Combine(_testDir, "group-b.txt");

        await File.WriteAllTextAsync(fileA, "A");
        await File.WriteAllTextAsync(fileB, "B");

        var fileAInfo = new FileInfo(fileA);
        var fileBInfo = new FileInfo(fileB);

        state.FileMetadata[fileA] = new FileMetadata
        {
            FilePath = fileA,
            Size = fileAInfo.Length,
            LastModified = fileAInfo.LastWriteTimeUtc,
            Hash = await FileHasher.ComputeHashAsync(fileA)
        };
        state.FileMetadata[fileB] = new FileMetadata
        {
            FilePath = fileB,
            Size = fileBInfo.Length,
            LastModified = fileBInfo.LastWriteTimeUtc.AddMinutes(-5),
            Hash = "outdated-hash"
        };

        var changedFilesForGroupA = state.GetChangedFiles([fileA], BackupType.ChunkSnapshot, "group-a");
        var changedFilesForGroupB = state.GetChangedFiles([fileB], BackupType.ChunkSnapshot, "group-b");

        changedFilesForGroupA.Should().BeEmpty();
        changedFilesForGroupB.Should().Contain(fileB);
    }

    [Fact]
    public void AddSnapshotBackup_AndUnregisterChunkReferences_ShouldTrackReferenceCounts()
    {
        var state = new SystemState(_loggerMock.Object);

        state.AddSnapshotBackup("group", "snapshot-1", "snapshots/group/s1.manifest.json", "local", ["chunk-a", "chunk-b"]);
        state.AddSnapshotBackup("group", "snapshot-2", "snapshots/group/s2.manifest.json", "local", ["chunk-a"]);

        var firstUnregister = state.UnregisterChunkReferences("local", ["chunk-a"]);
        firstUnregister.Should().BeEmpty("chunk-a is still referenced by snapshot-1");

        var secondUnregister = state.UnregisterChunkReferences("local", ["chunk-a"]);
        secondUnregister.Should().ContainSingle().Which.Should().Be("chunk-a");

        var thirdUnregister = state.UnregisterChunkReferences("local", ["chunk-b"]);
        thirdUnregister.Should().ContainSingle().Which.Should().Be("chunk-b");
    }

    [Fact]
    public void RecordRestoreTelemetry_ShouldAggregateFailureCategoriesCaseInsensitively()
    {
        var state = new SystemState(_loggerMock.Object);

        state.RecordRestoreTelemetry(
            success: false,
            filesExpected: 2,
            filesRestored: 1,
            chunkReferencesExpected: 5,
            chunkReferencesProcessed: 3,
            chunkDownloads: 2,
            chunkCacheHits: 1,
            failureCategory: "unexpected-error",
            validationFailures: 1);

        state.RecordRestoreTelemetry(
            success: false,
            filesExpected: 1,
            filesRestored: 0,
            chunkReferencesExpected: 2,
            chunkReferencesProcessed: 0,
            chunkDownloads: 0,
            chunkCacheHits: 0,
            failureCategory: "Unexpected-Error",
            validationFailures: 1);

        state.Telemetry.Restore.AttemptCount.Should().Be(2);
        state.Telemetry.Restore.ValidationFailureCount.Should().Be(2);
        state.Telemetry.Restore.FailureCategoryCounts.TryGetValue("unexpected-error", out var count).Should().BeTrue();
        count.Should().Be(2);
    }

    [Fact]
    public async Task SaveAndLoadState_ShouldPersistSnapshotTelemetryAggregates()
    {
        var state = new SystemState(_loggerMock.Object);
        state.SetStateFilePath(_stateFile);

        state.RecordSnapshotBackupTelemetry(
            fileCount: 3,
            chunkReferences: 9,
            uniqueChunks: 7,
            uploadedChunks: 4,
            uniqueReusedChunks: 3,
            storageHitChunks: 2,
            candidateChunks: 6);

        state.RecordRestoreTelemetry(
            success: true,
            filesExpected: 3,
            filesRestored: 3,
            chunkReferencesExpected: 9,
            chunkReferencesProcessed: 9,
            chunkDownloads: 4,
            chunkCacheHits: 5,
            failureCategory: null,
            validationFailures: 0);

        state.RecordVerificationTelemetry(
            success: false,
            fileCount: 3,
            chunkReferences: 9,
            uniqueChunks: 7,
            downloadedChunks: 7,
            missingChunks: 1,
            invalidChunks: 0,
            invalidFiles: 1,
            validationFailures: 2);

        await state.SaveStateAsync();

        var reloadedState = new SystemState(_loggerMock.Object);
        reloadedState.SetStateFilePath(_stateFile);
        await reloadedState.LoadStateAsync();

        reloadedState.Telemetry.Backup.SnapshotCount.Should().Be(1);
        reloadedState.Telemetry.Backup.FileCount.Should().Be(3);
        reloadedState.Telemetry.Backup.UniqueChunks.Should().Be(7);

        reloadedState.Telemetry.Restore.AttemptCount.Should().Be(1);
        reloadedState.Telemetry.Restore.SuccessCount.Should().Be(1);
        reloadedState.Telemetry.Restore.ChunkCacheHits.Should().Be(5);

        reloadedState.Telemetry.Verification.RunCount.Should().Be(1);
        reloadedState.Telemetry.Verification.SuccessCount.Should().Be(0);
        reloadedState.Telemetry.Verification.ValidationFailureCount.Should().Be(2);
        reloadedState.Telemetry.Verification.InvalidFiles.Should().Be(1);
    }

    [Fact]
    public void HasFileChanged_ShouldReturnFalse_WhenStoredHashMatchesCurrentHash()
    {
        var state = new SystemState(_loggerMock.Object);
        var filePath = Path.Combine(_testDir, "same-hash.txt");
        state.FileMetadata[filePath] = new FileMetadata { FilePath = filePath, Hash = "hash-1" };

        state.HasFileChanged(filePath, "hash-1").Should().BeFalse();
    }
}
