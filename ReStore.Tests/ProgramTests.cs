using FluentAssertions;
using Moq;
using ReStore.Core;
using ReStore.Core.src.utils;
using System.Reflection;
using System.Text;

namespace ReStore.Tests;

public class ProgramTests
{
    private static readonly SemaphoreSlim CONSOLE_OUT_LOCK = new(1, 1);

    [Fact]
    public async Task CreateCliPasswordProvider_ShouldReturnUnsetProvider_WhenEncryptionDisabled()
    {
        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });

        var provider = InvokeProgramMethod("CreateCliPasswordProvider", configMock.Object)
            .Should().BeOfType<StaticPasswordProvider>().Subject;

        provider.IsPasswordSet().Should().BeFalse();
        (await provider.GetPasswordAsync()).Should().BeNull();
    }

    [Fact]
    public async Task CreateCliPasswordProvider_ShouldUseEnvironmentPassword_WhenEncryptionEnabled()
    {
        const string environmentKey = "RESTORE_ENCRYPTION_PASSWORD";
        const string expectedPassword = "test-password-123";

        var previousValue = Environment.GetEnvironmentVariable(environmentKey);

        try
        {
            Environment.SetEnvironmentVariable(environmentKey, expectedPassword);

            var configMock = new Mock<IConfigManager>();
            configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = true });

            var provider = InvokeProgramMethod("CreateCliPasswordProvider", configMock.Object)
                .Should().BeOfType<StaticPasswordProvider>().Subject;

            provider.IsPasswordSet().Should().BeTrue();
            (await provider.GetPasswordAsync()).Should().Be(expectedPassword);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentKey, previousValue);
        }
    }

    [Fact]
    public void PrintValidationResults_ShouldWriteAllSections_AndLogEntries()
    {
        var result = new ConfigValidationResult();
        result.AddError("Error 1");
        result.AddWarning("Warning 1");
        result.AddInfo("Info 1");

        var logger = new TestLogger();
        var output = CaptureConsoleOutput(() =>
        {
            InvokeProgramMethod("PrintValidationResults", result, logger);
        });

        output.Should().Contain("ERRORS:");
        output.Should().Contain("WARNINGS:");
        output.Should().Contain("INFO:");
        output.Should().Contain("Error 1");
        output.Should().Contain("Warning 1");
        output.Should().Contain("Info 1");

        logger.Messages.Should().Contain(m => m.Contains("Config Error: Error 1"));
        logger.Messages.Should().Contain(m => m.Contains("Config Warning: Warning 1"));
        logger.Messages.Should().Contain(m => m.Contains("Config Info: Info 1"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldPrintSuccess_WhenValidationPasses()
    {
        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.GetConfigFilePath()).Returns("C:/Users/Test/ReStore/config.json");

        var result = new ConfigValidationResult();
        result.AddWarning("Warning exists");
        configMock.Setup(c => c.ValidateConfiguration()).Returns(result);

        var logger = new TestLogger();
        var output = CaptureConsoleOutput(() =>
        {
            InvokeProgramMethod("ValidateConfiguration", configMock.Object, logger);
        });

        output.Should().Contain("Configuration is valid and ready to use!");
        output.Should().Contain("Found 1 warning(s) that should be reviewed.");
        output.Should().Contain("WARNINGS:");
        output.Should().Contain("Warning exists");
    }

    [Fact]
    public void ValidateConfiguration_ShouldPrintSuccessWithoutWarningCount_WhenNoWarnings()
    {
        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.GetConfigFilePath()).Returns("C:/Users/Test/ReStore/config.json");
        configMock.Setup(c => c.ValidateConfiguration()).Returns(new ConfigValidationResult());

        var logger = new TestLogger();
        var output = CaptureConsoleOutput(() =>
        {
            InvokeProgramMethod("ValidateConfiguration", configMock.Object, logger);
        });

        output.Should().Contain("Configuration is valid and ready to use!");
        output.Should().NotContain("warning(s) that should be reviewed");

        logger.Messages.Should().Contain(m => m.Contains("Configuration file location:"));
        logger.Messages.Should().Contain(m => m.Contains("Running comprehensive configuration validation"));
    }

    [Fact]
    public void PrintValidationResults_ShouldPrintOnlyWarnings_WhenOnlyWarningsExist()
    {
        var result = new ConfigValidationResult();
        result.AddWarning("Warning only");

        var logger = new TestLogger();
        var output = CaptureConsoleOutput(() =>
        {
            InvokeProgramMethod("PrintValidationResults", result, logger);
        });

        output.Should().Contain("WARNINGS:");
        output.Should().Contain("Warning only");
        output.Should().NotContain("ERRORS:");
        output.Should().NotContain("INFO:");
        logger.Messages.Should().Contain(m => m.Contains("Config Warning: Warning only"));
    }

    [Fact]
    public void PrintValidationResults_ShouldWriteNothing_WhenNoIssuesExist()
    {
        var logger = new TestLogger();
        var result = new ConfigValidationResult();
        var output = CaptureConsoleOutput(() =>
        {
            InvokeProgramMethod("PrintValidationResults", result, logger);
        });

        output.Should().NotContain("ERRORS:");
        output.Should().NotContain("WARNINGS:");
        output.Should().NotContain("INFO:");
        logger.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Main_ShouldPrintUsage_WhenNoArgumentsProvided()
    {
        var output = await CaptureConsoleOutputAsync(async () =>
        {
            await Program.Main([]);
        });

        output.Should().Contain("Usage:");
        output.Should().Contain("restore.exe --service");
    }

    [Fact]
    public async Task Main_ShouldPrintUsage_WhenUnknownCommandProvided()
    {
        var output = await CaptureConsoleOutputAsync(async () =>
        {
            await Program.Main(["unknown-command"]);
        });

        output.Should().Contain("Usage:");
        output.Should().Contain("restore.exe backup <sourceDir>");
    }

    [Fact]
    public async Task Main_ShouldPrintUsage_WhenBackupCommandMissingSourceDirectory()
    {
        var output = await CaptureConsoleOutputAsync(async () =>
        {
            await Program.Main(["backup"]);
        });

        output.Should().Contain("Usage:");
        output.Should().Contain("restore.exe backup <sourceDir>");
    }

    [Fact]
    public async Task Main_ShouldPrintUsage_WhenRestoreCommandMissingTargetDirectory()
    {
        var output = await CaptureConsoleOutputAsync(async () =>
        {
            await Program.Main(["restore", "backup.enc"]);
        });

        output.Should().Contain("Usage:");
        output.Should().Contain("restore.exe restore <backupPath> <targetDir>");
    }

    [Fact]
    public async Task Main_ShouldPrintUsage_WhenSystemRestoreCommandMissingBackupPath()
    {
        var output = await CaptureConsoleOutputAsync(async () =>
        {
            await Program.Main(["system-restore"]);
        });

        output.Should().Contain("Usage:");
        output.Should().Contain("restore.exe system-restore <backupPath>");
    }

    [Fact]
    public void GetStorageOverride_ShouldReturnFlagValue_WhenPresent()
    {
        var result = InvokeProgramMethod("GetStorageOverride", [new[] { "backup", "C:/Data", "--storage", "s3" }]);

        result.Should().Be("s3");
    }

    [Fact]
    public void GetStorageOverride_ShouldReturnNull_WhenFlagIsMissingValue()
    {
        var result = InvokeProgramMethod("GetStorageOverride", [new[] { "backup", "C:/Data", "--storage" }]);

        result.Should().BeNull();
    }

    [Fact]
    public void GetStorageOverride_ShouldReturnNull_WhenFlagNotPresent()
    {
        var result = InvokeProgramMethod("GetStorageOverride", [new[] { "backup", "C:/Data" }]);

        result.Should().BeNull();
    }

    private static string CaptureConsoleOutput(Action action)
    {
        CONSOLE_OUT_LOCK.Wait();
        var originalOut = Console.Out;
        try
        {
            var buffer = new StringBuilder();
            using var writer = new StringWriter(buffer);

            Console.SetOut(writer);
            action();
            Console.Out.Flush();

            return buffer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
            CONSOLE_OUT_LOCK.Release();
        }
    }

    private static async Task<string> CaptureConsoleOutputAsync(Func<Task> action)
    {
        await CONSOLE_OUT_LOCK.WaitAsync();
        var originalOut = Console.Out;
        try
        {
            var buffer = new StringBuilder();
            using var writer = new StringWriter(buffer);

            Console.SetOut(writer);
            await action();
            Console.Out.Flush();

            return buffer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
            CONSOLE_OUT_LOCK.Release();
        }
    }

    private static object? InvokeProgramMethod(string methodName, params object[] args)
    {
        var method = typeof(Program).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return method!.Invoke(null, args);
    }
}
