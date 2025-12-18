using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ReStore.Tests;

public class RetentionManagerTests
{
    private sealed class FakeStorage : IStorage
    {
        private readonly HashSet<string> _existingPaths;

        public List<string> DeletedPaths { get; } = [];

        public FakeStorage(IEnumerable<string> existingPaths)
        {
            _existingPaths = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        }

        public Task InitializeAsync(Dictionary<string, string> options)
        {
            return Task.CompletedTask;
        }

        public Task UploadAsync(string localPath, string remotePath)
        {
            _existingPaths.Add(remotePath);
            return Task.CompletedTask;
        }

        public Task DownloadAsync(string remotePath, string localPath)
        {
            throw new NotImplementedException();
        }

        public Task<bool> ExistsAsync(string remotePath)
        {
            return Task.FromResult(_existingPaths.Contains(remotePath));
        }

        public Task DeleteAsync(string remotePath)
        {
            DeletedPaths.Add(remotePath);
            _existingPaths.Remove(remotePath);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void SelectBackupsToDelete_AlwaysKeepsNewest_EvenIfOlderThanMaxAge()
    {
        var now = DateTime.UtcNow;
        var backups = new List<BackupInfo>
        {
            new() { Path = "backups/A/newest.zip", Timestamp = now.AddDays(-10) },
            new() { Path = "backups/A/older.zip", Timestamp = now.AddDays(-20) }
        };

        var retention = new RetentionConfig
        {
            Enabled = true,
            KeepLastPerDirectory = 1,
            MaxAgeDays = 1
        };

        var toDelete = RetentionManager.SelectBackupsToDelete(backups, retention);

        toDelete.Select(b => b.Path).Should().BeEquivalentTo(["backups/A/older.zip"]);
    }

    [Fact]
    public void SelectBackupsToDelete_RespectsKeepLast_WhenAgeRuleDisabled()
    {
        var now = DateTime.UtcNow;
        var backups = new List<BackupInfo>
        {
            new() { Path = "b1", Timestamp = now.AddMinutes(-1) },
            new() { Path = "b2", Timestamp = now.AddMinutes(-2) },
            new() { Path = "b3", Timestamp = now.AddMinutes(-3) },
            new() { Path = "b4", Timestamp = now.AddMinutes(-4) }
        };

        var retention = new RetentionConfig
        {
            Enabled = true,
            KeepLastPerDirectory = 2,
            MaxAgeDays = 0
        };

        var toDelete = RetentionManager.SelectBackupsToDelete(backups, retention);

        toDelete.Select(b => b.Path).Should().BeEquivalentTo(["b3", "b4"]);
    }

    [Fact]
    public void SelectBackupsToDelete_UsesUnionOfKeepLastAndMaxAge()
    {
        var now = DateTime.UtcNow;
        var backups = new List<BackupInfo>
        {
            // Newest is always kept by KeepLast.
            new() { Path = "b_newest", Timestamp = now.AddDays(-5) },
            // This is within MaxAgeDays and should be kept too.
            new() { Path = "b_recent", Timestamp = now.AddDays(-10) },
            // This is outside MaxAgeDays and should be deleted.
            new() { Path = "b_old", Timestamp = now.AddDays(-40) }
        };

        var retention = new RetentionConfig
        {
            Enabled = true,
            KeepLastPerDirectory = 1,
            MaxAgeDays = 30
        };

        var toDelete = RetentionManager.SelectBackupsToDelete(backups, retention);
        toDelete.Select(b => b.Path).Should().BeEquivalentTo(["b_old"]);
    }

    [Fact]
    public async Task ApplyGroupAsync_DeletesOldBackupsAndPrunesState_AndDeletesEncMetadata()
    {
        var loggerMock = new Mock<ILogger>();
        var logger = loggerMock.Object;

        var retention = new RetentionConfig
        {
            Enabled = true,
            KeepLastPerDirectory = 1,
            MaxAgeDays = 0
        };

        var group = "C:/Test/Dir";
        var now = DateTime.UtcNow;

        var newest = new BackupInfo
        {
            Path = "backups/Dir/newest.zip.enc",
            Timestamp = now.AddMinutes(-1),
            StorageType = null
        };

        var old1 = new BackupInfo
        {
            Path = "backups/Dir/old1.zip.enc",
            Timestamp = now.AddMinutes(-2),
            StorageType = null
        };

        var old2 = new BackupInfo
        {
            Path = "backups/Dir/old2.zip",
            Timestamp = now.AddMinutes(-3),
            StorageType = null
        };

        var existing = new[]
        {
            newest.Path,
            newest.Path + ".meta",
            old1.Path,
            old1.Path + ".meta",
            old2.Path
        };

        var storage = new FakeStorage(existing);

        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.Retention).Returns(retention);
        configMock.Setup(c => c.GlobalStorageType).Returns("local");
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = new SystemState(logger);
        state.SetStateFilePath(Path.Combine(Path.GetTempPath(), $"ReStoreTests_state_{Guid.NewGuid():N}.json"));
        state.BackupHistory[group] = new List<BackupInfo> { newest, old1, old2 };

        var manager = new RetentionManager(logger, configMock.Object, state);

        await manager.ApplyGroupAsync(group);

        storage.DeletedPaths.Should().BeEquivalentTo([
            old1.Path,
            old1.Path + ".meta",
            old2.Path
        ]);

        state.BackupHistory.Should().ContainKey(group);
        state.BackupHistory[group].Should().HaveCount(1);
        state.BackupHistory[group][0].Path.Should().Be(newest.Path);
    }

    [Fact]
    public async Task ApplyGroupAsync_WhenRetentionDisabled_DoesNothing()
    {
        var loggerMock = new Mock<ILogger>();
        var logger = loggerMock.Object;

        var retention = new RetentionConfig
        {
            Enabled = false,
            KeepLastPerDirectory = 1,
            MaxAgeDays = 0
        };

        var group = "C:/Test/Dir";
        var now = DateTime.UtcNow;

        var newest = new BackupInfo
        {
            Path = "backups/Dir/newest.zip",
            Timestamp = now.AddMinutes(-1),
            StorageType = null
        };

        var old1 = new BackupInfo
        {
            Path = "backups/Dir/old1.zip",
            Timestamp = now.AddMinutes(-2),
            StorageType = null
        };

        var existing = new[] { newest.Path, old1.Path };
        var storage = new FakeStorage(existing);

        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.Retention).Returns(retention);
        configMock.Setup(c => c.GlobalStorageType).Returns("local");
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = new SystemState(logger);
        state.SetStateFilePath(Path.Combine(Path.GetTempPath(), $"ReStoreTests_state_{Guid.NewGuid():N}.json"));
        state.BackupHistory[group] = new List<BackupInfo> { newest, old1 };

        var manager = new RetentionManager(logger, configMock.Object, state);

        await manager.ApplyGroupAsync(group);

        storage.DeletedPaths.Should().BeEmpty();
        state.BackupHistory.Should().ContainKey(group);
        state.BackupHistory[group].Select(b => b.Path).Should().BeEquivalentTo([newest.Path, old1.Path]);
    }
}
