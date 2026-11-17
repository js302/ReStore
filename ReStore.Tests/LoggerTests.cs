using FluentAssertions;
using ReStore.Core.src.utils;
using System.Reflection;

namespace ReStore.Tests;

[Collection("GlobalState")]
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
        var content = ReadSharedOrEmpty(logPath);
        content.Should().Contain(marker);
        content.Should().Contain("[Warning]");
    }

    [Fact]
    public void Log_RedactsSecretsInTheMessage()
    {
        var logger = CreateIsolatedLogger("redact", out _, out var logPath);

        logger.Log("upload failed: secretAccessKey=TopSecretValue99 https://u:pw0rd@host/x", LogLevel.Warning);

        var content = ReadSharedOrEmpty(logPath);
        content.Should().Contain("upload failed").And.NotContain("TopSecretValue99").And.NotContain("pw0rd");
    }

    [Fact]
    public void Log_ShouldSupportConcurrentWrites()
    {
        var logger = CreateIsolatedLogger("concurrent", out _, out var logPath);
        var prefix = "LOGGER_CONCURRENT_" + Guid.NewGuid().ToString("N");
        logger.MinimumLevel = LogLevel.Debug;

        Parallel.For(0, 20, i =>
        {
            logger.Log($"{prefix}_{i}", LogLevel.Debug);
        });

        logger.Flush();

        var content = ReadSharedOrEmpty(logPath);
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
        logger.Flush();
        File.Exists(logPath).Should().BeTrue();
    }

    [Fact]
    public void Log_ShouldDropLinesBelowMinimumLevel()
    {
        var logger = CreateIsolatedLogger("level-filter", out _, out var logPath);
        var marker = Guid.NewGuid().ToString("N");

        logger.MinimumLevel.Should().Be(LogLevel.Info);
        logger.Log("debug-" + marker, LogLevel.Debug);
        logger.Log("info-" + marker, LogLevel.Info);
        logger.Log("warn-" + marker, LogLevel.Warning);
        logger.Flush();

        var content = ReadSharedOrEmpty(logPath);
        content.Should().NotContain("debug-" + marker);
        content.Should().Contain("info-" + marker);
        content.Should().Contain("warn-" + marker);

        logger.MinimumLevel = LogLevel.Error;
        logger.Log("info2-" + marker, LogLevel.Info);
        logger.Log("error-" + marker, LogLevel.Error);
        logger.Flush();

        content = ReadSharedOrEmpty(logPath);
        content.Should().NotContain("info2-" + marker);
        content.Should().Contain("error-" + marker);

        logger.MinimumLevel = LogLevel.Debug;
        logger.Log("debug2-" + marker, LogLevel.Debug);
        logger.Flush();
        ReadSharedOrEmpty(logPath).Should().Contain("debug2-" + marker);
    }

    [Fact]
    public void Log_ShouldBufferInfoLines_UntilFlushOrDispose_ButWriteWarningsImmediately()
    {
        var logger = CreateIsolatedLogger("buffered", out _, out var logPath);
        var marker = Guid.NewGuid().ToString("N");

        logger.Log("first-" + marker, LogLevel.Info);
        ReadSharedOrEmpty(logPath).Should().NotContain("first-" + marker, "info lines are buffered");

        logger.Flush();
        ReadSharedOrEmpty(logPath).Should().Contain("first-" + marker);

        logger.Log("second-" + marker, LogLevel.Info);
        logger.Log("warn-" + marker, LogLevel.Warning);
        var content = ReadSharedOrEmpty(logPath);
        content.Should().Contain("warn-" + marker, "warnings flush immediately");
        content.Should().Contain("second-" + marker, "the warning flushes the lines before it");

        logger.Log("third-" + marker, LogLevel.Info);
        logger.Dispose();
        ReadSharedOrEmpty(logPath).Should().Contain("third-" + marker, "dispose flushes");

        logger.Log("after-dispose-" + marker, LogLevel.Error);
        ReadSharedOrEmpty(logPath).Should().NotContain("after-dispose-" + marker);
    }

    [Fact]
    public async Task TwoLoggersOnOneFile_ShouldNotOverwriteEachOther()
    {
        var first = CreateIsolatedLogger("two-writers", out _, out var logPath);
        var second = new Logger();
        SetPrivateField(second, "_logFilePath", logPath);
        SetPrivateField(second, "_logDir", Path.GetDirectoryName(logPath)!);
        const int perWriter = 300;

        async Task Write(Logger logger, string tag)
        {
            await Task.Yield();
            for (var i = 0; i < perWriter; i++)
            {
                logger.Log($"{tag}-{i:D4}", i % 25 == 0 ? LogLevel.Warning : LogLevel.Info);
                if (i % 40 == 0)
                {
                    logger.Flush();
                }
            }

            logger.Flush();
        }

        if (OperatingSystem.IsWindows())
        {
            await Task.WhenAll(Task.Run(() => Write(first, "AAA")), Task.Run(() => Write(second, "BBB")));
        }
        else
        {
            // .NET appends with pwrite off Windows, so truly concurrent writers can race; interleave deterministically instead
            // (each logger flushes in turn, which is exactly what a long-lived stale-offset handle would corrupt).
            for (var i = 0; i < perWriter; i++)
            {
                first.Log($"AAA-{i:D4}");
                second.Log($"BBB-{i:D4}");
                if (i % 7 == 0)
                {
                    first.Flush();
                }

                if (i % 11 == 0)
                {
                    second.Flush();
                }
            }

            first.Flush();
            second.Flush();
        }

        var lines = ReadSharedOrEmpty(logPath).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        lines.Should().HaveCount(2 * perWriter);
        for (var i = 0; i < perWriter; i++)
        {
            lines.Should().ContainSingle(l => l.EndsWith($"AAA-{i:D4}") && l.StartsWith("["));
            lines.Should().ContainSingle(l => l.EndsWith($"BBB-{i:D4}") && l.StartsWith("["));
        }
    }

    [Fact]
    public void Flush_ShouldKeepLinesAndRetry_WhenTheFileIsTemporarilyLocked()
    {
        var logger = CreateIsolatedLogger("locked", out _, out var logPath);
        File.WriteAllText(logPath, "");
        using (new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            logger.Log("kept-line", LogLevel.Info);
            logger.Flush();
        }

        ReadSharedOrEmpty(logPath).Should().NotContain("kept-line");
        logger.Flush();
        ReadSharedOrEmpty(logPath).Should().Contain("kept-line");
    }

    private static string ReadSharedOrEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
