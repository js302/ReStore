using FluentAssertions;
using ReStore.Core.src.utils;
using System.Reflection;

namespace ReStore.Tests;

public class LoggerTests
{
    private const int MAX_LOG_SIZE_BYTES = 10 * 1024 * 1024;

    [Fact]
    public void Constructor_ShouldInitializeLogPath()
    {
        var logger = CreateIsolatedLogger("constructor", out var logDir, out var logPath);

        logPath.Should().NotBeNullOrWhiteSpace();
        logDir.Should().NotBeNullOrWhiteSpace();
        Path.GetFileName(logPath).Should().Be("restore.log");
        Directory.Exists(logDir).Should().BeTrue();
    }

    [Fact]
    public void Log_ShouldAppendMessageToLogFile()
    {
        var logger = CreateIsolatedLogger("append", out _, out var logPath);
        var marker = "LOGGER_TEST_" + Guid.NewGuid().ToString("N");

        logger.Log(marker, LogLevel.Warning);

        File.Exists(logPath).Should().BeTrue();
        var content = File.ReadAllText(logPath);
        content.Should().Contain(marker);
        content.Should().Contain("[Warning]");
    }

    [Fact]
    public void Log_ShouldSupportConcurrentWrites()
    {
        var logger = CreateIsolatedLogger("concurrent", out _, out var logPath);
        var prefix = "LOGGER_CONCURRENT_" + Guid.NewGuid().ToString("N");

        Parallel.For(0, 20, i =>
        {
            logger.Log($"{prefix}_{i}", LogLevel.Debug);
        });

        var content = File.ReadAllText(logPath);
        for (int i = 0; i < 20; i++)
        {
            content.Should().Contain($"{prefix}_{i}");
        }
    }

    [Fact]
    public void RotateLogIfNeeded_ShouldCreateBackup_WhenCurrentLogExceedsLimit()
    {
        var logger = CreateIsolatedLogger("rotate-create", out var logDir, out var logPath);
        var firstBackupPath = Path.Combine(logDir, "restore.log.1");

        File.WriteAllText(logPath, new string('X', MAX_LOG_SIZE_BYTES + 1024));

        InvokePrivateMethod(logger, "RotateLogIfNeeded");

        File.Exists(firstBackupPath).Should().BeTrue();
        File.Exists(logPath).Should().BeFalse();

        logger.Log("after-rotation", LogLevel.Info);
        File.Exists(logPath).Should().BeTrue();
    }

    [Fact]
    public void RotateLogIfNeeded_ShouldShiftBackups_WhenBackupFilesExist()
    {
        var logger = CreateIsolatedLogger("rotate-shift", out var logDir, out var logPath);

        var backup4Path = Path.Combine(logDir, "restore.log.4");
        var backup5Path = Path.Combine(logDir, "restore.log.5");
        var oldBackup4Marker = "OLD_BACKUP_4_" + Guid.NewGuid().ToString("N");
        var oldBackup5Marker = "OLD_BACKUP_5_" + Guid.NewGuid().ToString("N");

        File.WriteAllText(backup4Path, oldBackup4Marker);
        File.WriteAllText(backup5Path, oldBackup5Marker);
        File.WriteAllText(logPath, new string('Y', MAX_LOG_SIZE_BYTES + 2048));

        InvokePrivateMethod(logger, "RotateLogIfNeeded");

        File.Exists(backup5Path).Should().BeTrue();
        File.ReadAllText(backup5Path).Should().Contain(oldBackup4Marker);
        File.ReadAllText(backup5Path).Should().NotContain(oldBackup5Marker);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return (T)field!.GetValue(instance)!;
    }

    private static void InvokePrivateMethod(object instance, string methodName)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(instance, null);
    }

    private static Logger CreateIsolatedLogger(string testName, out string logDir, out string logPath)
    {
        var logger = new Logger();

        logDir = Path.Combine(Path.GetTempPath(), "ReStoreLoggerTests", $"{testName}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        logPath = Path.Combine(logDir, "restore.log");

        SetPrivateField(logger, "_logDir", logDir);
        SetPrivateField(logger, "_logFilePath", logPath);

        return logger;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        field!.SetValue(instance, value);
    }
}
