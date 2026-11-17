using FluentAssertions;
using Moq;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class ProtectionFingerprintTests
{
    private readonly ConfigManager _config = new(new TestLogger());

    private string Fp() => ProtectionFingerprint.Compute(_config);

    private static void Set<T>(ConfigManager c, string property, T value) =>
        typeof(ConfigManager).GetProperty(property)!.SetValue(c, value);

    [Fact]
    public void UnrelatedSettings_DoNotChangeIt()
    {
        var before = Fp();

        _config.ExcludedPatterns.Add("*.tmp");
        _config.ExcludedPaths.Add("C:/x");
        _config.Retention.Enabled = true;
        _config.Notifications.Enabled = false;

        Fp().Should().Be(before);
    }

    [Fact]
    public void RelevantSettings_ChangeIt()
    {
        var seen = new HashSet<string> { Fp() };

        _config.WatchDirectories.Add(new WatchDirectoryConfig { Path = "C:/data", StorageType = null });
        seen.Add(Fp()).Should().BeTrue("watch directories");

        Set(_config, nameof(ConfigManager.BackupInterval), TimeSpan.FromMinutes(7));
        seen.Add(Fp()).Should().BeTrue("backup interval");

        Set(_config, nameof(ConfigManager.GlobalStorageType), "s3");
        seen.Add(Fp()).Should().BeTrue("global storage");

        _config.StorageSources["local"] = new StorageConfig { Path = "D:/b", Options = new() { ["path"] = "D:/b" } };
        seen.Add(Fp()).Should().BeTrue("storage sources");

        _config.Encryption.Enabled = true;
        seen.Add(Fp()).Should().BeTrue("encryption");

        _config.SystemBackup.BackupInterval = TimeSpan.FromHours(3);
        seen.Add(Fp()).Should().BeTrue("system backup interval");

        _config.Protection.AutoStart = false;
        seen.Add(Fp()).Should().BeTrue("auto-start");
    }

    [Fact]
    public void SchedulesAndTheBackupWindow_ChangeIt()
    {
        _config.WatchDirectories.Add(new WatchDirectoryConfig { Path = "C:/data" });
        var seen = new HashSet<string> { Fp() };

        _config.WatchDirectories[0].Schedule = "daily 02:00";
        seen.Add(Fp()).Should().BeTrue("a folder schedule");

        _config.WatchDirectories[0].Schedule = "daily 03:00";
        seen.Add(Fp()).Should().BeTrue("a changed folder schedule");

        _config.SystemBackup.Schedule = "weekly sun 03:00";
        seen.Add(Fp()).Should().BeTrue("the system backup schedule");

        _config.BackupWindow = new BackupWindowConfig { Enabled = true, Start = "22:00", End = "06:00", Days = "all" };
        seen.Add(Fp()).Should().BeTrue("the backup window");

        _config.BackupWindow.End = "07:00";
        seen.Add(Fp()).Should().BeTrue("a changed window");

        _config.BackupWindow.Enabled = false;
        seen.Add(Fp()).Should().BeTrue("a disabled window");
    }

    [Fact]
    public void StorageOptionOrder_DoesNotMatter()
    {
        _config.StorageSources["a"] = new StorageConfig { Options = new() { ["x"] = "1", ["y"] = "2" } };
        var first = Fp();
        _config.StorageSources["a"] = new StorageConfig { Options = new() { ["y"] = "2", ["x"] = "1" } };

        Fp().Should().Be(first);
    }
}
