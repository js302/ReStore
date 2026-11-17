using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Win32;
using ReStore.Core.src.cli;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Real Windows behaviour that mocks cannot show: child-process quoting, timeout/cancel tree kills, Task Scheduler, reg.exe, DPAPI and
/// the user environment. They show as skipped off Windows and, on Windows, only run when RESTORE_WINDOWS_SYSTEM_TESTS=1 (the windows
/// CI job sets it). Every test uses a throwaway, uniquely named object and removes it in a finally block.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessRunnerWindowsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReStoreProcWin_" + Guid.NewGuid().ToString("N"));
    private string? _script;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string ScriptText = """
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $mode = $args[0]
        if ($mode -eq 'echo') { [Console]::Out.Write((($args | Select-Object -Skip 1) -join '|')); exit 0 }
        if ($mode -eq 'streams') { [Console]::Out.Write('to-out'); [Console]::Error.Write('to-err'); exit 7 }
        if ($mode -eq 'hang') {
            $p = Start-Process ping -ArgumentList '-n','30','127.0.0.1' -PassThru -WindowStyle Hidden
            Set-Content -LiteralPath $args[1] -Value $p.Id
            Wait-Process -Id $p.Id
        }
        """;

    private string Script()
    {
        if (_script == null)
        {
            Directory.CreateDirectory(_dir);
            _script = Path.Combine(_dir, "probe.ps1");
            File.WriteAllText(_script, ScriptText, new UTF8Encoding(true));
        }

        return _script;
    }

    private string[] Powershell(params string[] scriptArguments) =>
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script(), .. scriptArguments];

    [WindowsSystemFact]
    public async Task ExitCode_IsReturned()
    {
        var result = await ProcessRunner.RunAsync("cmd.exe", ["/c", "exit 3"], TimeSpan.FromSeconds(30));

        result.ExitCode.Should().Be(3);
        result.TimedOut.Should().BeFalse();
    }

    [WindowsSystemFact]
    public async Task StdoutAndStderr_AreCapturedSeparately()
    {
        var result = await ProcessRunner.RunAsync("powershell.exe", Powershell("streams"), TimeSpan.FromSeconds(60), standardOutputEncoding: Encoding.UTF8);

        result.ExitCode.Should().Be(7);
        result.StandardOutput.TrimStart('\uFEFF').Should().Be("to-out");
        result.StandardError.Should().Contain("to-err");
    }

    [WindowsSystemFact]
    public async Task Arguments_SurviveSpacesQuotesSemicolonsPercentCaretAndUnicode()
    {
        string[] arguments = ["a b", "say \"hi\"", "x;y", "%PATH%", "a^b", "caf\u00e9 \u65e5\u672c\u8a9e", "back\\slash", @"C:\Program Files\x\"];

        var result = await ProcessRunner.RunAsync(
            "powershell.exe", Powershell(["echo", .. arguments]), TimeSpan.FromSeconds(60), standardOutputEncoding: new UTF8Encoding(false));

        result.ExitCode.Should().Be(0, result.StandardError);
        // Joined with '|': every argument arrives as exactly one entry, with no shell expansion of %PATH% or ^.
        result.StandardOutput.TrimStart('\uFEFF').Should().Be(string.Join("|", arguments));
    }

    [WindowsSystemFact]
    public async Task Timeout_KillsTheProcess_AndReportsTimedOut()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync("cmd.exe", ["/c", "ping -n 30 127.0.0.1"], TimeSpan.FromMilliseconds(700));
        stopwatch.Stop();

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "the child ran for 30 s; the timeout must cut it short");
    }

    [WindowsSystemFact]
    public async Task Cancellation_KillsTheWholeProcessTree()
    {
        var pidFile = Path.Combine(_dir, "child.pid");
        Directory.CreateDirectory(_dir);
        using var cts = new CancellationTokenSource();
        var run = ProcessRunner.RunAsync("powershell.exe", Powershell("hang", pidFile), TimeSpan.FromSeconds(120), cts.Token);

        var childPid = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && childPid == 0)
        {
            await Task.Delay(200);
            if (File.Exists(pidFile))
            {
                try { int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim().TrimStart('\uFEFF'), out childPid); } catch (IOException) { }
            }
        }

        childPid.Should().BeGreaterThan(0, "the script should have started its ping child");
        cts.Cancel();
        var act = () => run;
        await act.Should().ThrowAsync<OperationCanceledException>();

        var gone = false;
        for (var i = 0; i < 50 && !gone; i++)
        {
            try
            {
                using var child = Process.GetProcessById(childPid);
                gone = child.HasExited;
            }
            catch (ArgumentException)
            {
                gone = true;
            }

            if (!gone)
            {
                await Task.Delay(100);
            }
        }

        gone.Should().BeTrue("Kill(entireProcessTree) must also end the grandchild");
    }
}

[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskWindowsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReStore Task Test " + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Task<ProcessResult> Schtasks(params string[] arguments) =>
        ProcessRunner.RunAsync("schtasks.exe", arguments, TimeSpan.FromSeconds(60));

    [WindowsSystemFact]
    public async Task CreateQueryDelete_RoundTrip_ThroughTheRealTaskScheduler()
    {
        var taskName = @"\ReStoreTests\" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_dir);
        var throwawayExe = Path.Combine(_dir, "restore.exe"); // a path with spaces exercises the quoting in the task XML
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), throwawayExe);
        var xmlFile = Path.Combine(_dir, "task.xml");
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        await File.WriteAllTextAsync(xmlFile, ScheduledTaskManager.BuildTaskXml(throwawayExe, 30, user, DateTime.Now.AddHours(1)), new UnicodeEncoding(false, true));

        try
        {
            var create = await Schtasks("/Create", "/TN", taskName, "/XML", xmlFile, "/F");
            create.ExitCode.Should().Be(0, create.StandardError + create.StandardOutput);

            var xml = await Schtasks("/Query", "/TN", taskName, "/XML");
            xml.ExitCode.Should().Be(0, xml.StandardError);
            var verbose = await Schtasks("/Query", "/TN", taskName, "/V", "/FO", "CSV");
            verbose.ExitCode.Should().Be(0, verbose.StandardError);

            var status = ScheduledTaskManager.ParseStatus(xml.StandardOutput, verbose.StandardOutput);
            status.Installed.Should().BeTrue();
            status.Enabled.Should().BeTrue();
            status.IntervalMinutes.Should().Be(30);
            status.Executable.Should().Be(throwawayExe);
            status.Arguments.Should().Be(ScheduledTaskManager.TaskArguments);
            status.LastResult.Should().NotBeNull("the verbose CSV row should have been parsed");

            // Re-creating with /F replaces the task instead of failing.
            await File.WriteAllTextAsync(xmlFile, ScheduledTaskManager.BuildTaskXml(throwawayExe, 60, user, DateTime.Now.AddHours(1)), new UnicodeEncoding(false, true));
            (await Schtasks("/Create", "/TN", taskName, "/XML", xmlFile, "/F")).ExitCode.Should().Be(0);
            var replaced = await Schtasks("/Query", "/TN", taskName, "/XML");
            ScheduledTaskManager.ParseStatus(replaced.StandardOutput, null).IntervalMinutes.Should().Be(60);
        }
        finally
        {
            await Schtasks("/Delete", "/TN", taskName, "/F");
        }

        var after = await Schtasks("/Query", "/TN", taskName, "/XML");
        after.ExitCode.Should().NotBe(0, "the task must be gone after /Delete");
    }

    [WindowsSystemFact]
    public async Task QueryingAMissingTask_ExitsNonZero_WithoutThrowing()
    {
        var result = await Schtasks("/Query", "/TN", @"\ReStoreTests\missing-" + Guid.NewGuid().ToString("N"), "/XML");

        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().NotBe(0, "the manager relies on a non-zero exit code (not localized text) for 'not installed'");
    }
}

[SupportedOSPlatform("windows")]
public sealed class RegExeWindowsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReStoreRegTest_" + Guid.NewGuid().ToString("N"));
    private readonly string _subKey = @"Software\ReStoreTests\RegExe_" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    [WindowsSystemFact]
    public async Task ExportThenImport_RoundTripsValues_IncludingUnicode()
    {
        Directory.CreateDirectory(_dir);
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey))
        {
            key.SetValue("Text", "hello world");
            key.SetValue("Unicode", "caf\u00e9 \u65e5\u672c\u8a9e");
            key.SetValue("Number", 42, RegistryValueKind.DWord);
            key.SetValue("Expand", @"%USERPROFILE%\x", RegistryValueKind.ExpandString);
            key.SetValue("Bytes", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            using var child = key.CreateSubKey("Child");
            child.SetValue("Inner", "v");
        }

        var file = Path.Combine(_dir, "export.reg");
        var exported = await new DefaultRegistryKeyExporter(new TestLogger()).ExportKeyAsync(@"HKEY_CURRENT_USER\" + _subKey, file, CancellationToken.None);
        exported.Should().BeTrue();
        File.Exists(file).Should().BeTrue();

        Registry.CurrentUser.DeleteSubKeyTree(_subKey);
        Registry.CurrentUser.OpenSubKey(_subKey).Should().BeNull();

        var import = await ProcessRunner.RunAsync("reg.exe", ["import", file], TimeSpan.FromSeconds(60));
        import.ExitCode.Should().Be(0, import.StandardError);

        using var restored = Registry.CurrentUser.OpenSubKey(_subKey);
        restored.Should().NotBeNull();
        restored!.GetValue("Text").Should().Be("hello world");
        restored.GetValue("Unicode").Should().Be("caf\u00e9 \u65e5\u672c\u8a9e");
        restored.GetValue("Number").Should().Be(42);
        restored.GetValueKind("Expand").Should().Be(RegistryValueKind.ExpandString);
        restored.GetValue("Expand", null, RegistryValueOptions.DoNotExpandEnvironmentNames).Should().Be(@"%USERPROFILE%\x");
        ((byte[])restored.GetValue("Bytes")!).Should().Equal(1, 2, 3);
        using var restoredChild = restored.OpenSubKey("Child");
        restoredChild!.GetValue("Inner").Should().Be("v");
    }

    [WindowsSystemFact]
    public async Task Export_OfAMissingKey_ReturnsFalse_WithoutCallingRegExe()
    {
        var file = Path.Combine(Path.GetTempPath(), "never-written-" + Guid.NewGuid().ToString("N") + ".reg");

        var exported = await new DefaultRegistryKeyExporter(new TestLogger()).ExportKeyAsync(@"HKEY_CURRENT_USER\" + _subKey, file, CancellationToken.None);

        exported.Should().BeFalse();
        File.Exists(file).Should().BeFalse();
    }
}

[SupportedOSPlatform("windows")]
public sealed class DpapiWindowsTests
{
    [WindowsSystemFact]
    public void ProtectUnprotect_RoundTrips_AndTheBlobIsNotPlaintext()
    {
        var protector = DpapiSecretProtector.Instance;
        var secret = Encoding.UTF8.GetBytes("s3cret \u00fc\u65e5 value");

        var blob = protector.Protect(secret);

        protector.IsAvailable.Should().BeTrue();
        blob.Should().NotEqual(secret);
        Encoding.UTF8.GetString(blob).Should().NotContain("s3cret");
        protector.Unprotect(blob).Should().Equal(secret);
        protector.Protect(secret).Should().NotEqual(blob, "DPAPI blobs are salted, so two protections differ");
    }

    [WindowsSystemFact]
    public void Unprotect_OfATamperedBlob_Throws()
    {
        var blob = DpapiSecretProtector.Instance.Protect([1, 2, 3, 4]);
        blob[^1] ^= 0xFF;

        var act = () => DpapiSecretProtector.Instance.Unprotect(blob);

        act.Should().Throw<CryptographicException>();
    }
}

[SupportedOSPlatform("windows")]
public sealed class UserEnvironmentWindowsTests
{
    [WindowsSystemFact]
    public void SetGetRemove_UserScope_PreservesKind_AndCleansUp()
    {
        var name = "RESTORE_TEST_SYS_" + Guid.NewGuid().ToString("N");
        var store = new DefaultEnvironmentVariableStore();
        try
        {
            store.Set(name, @"%USERPROFILE%\a", EnvironmentVariableTarget.User, EnvironmentVariableKinds.ExpandString);
            var expand = store.Get(name, EnvironmentVariableTarget.User);
            expand.Should().NotBeNull();
            expand!.Value.Should().Be(@"%USERPROFILE%\a");
            expand.Kind.Should().Be(EnvironmentVariableKinds.ExpandString);

            store.Set(name, "plain", EnvironmentVariableTarget.User, EnvironmentVariableKinds.String);
            store.Get(name, EnvironmentVariableTarget.User)!.Should().Be(new StoredEnvironmentValue("plain", EnvironmentVariableKinds.String));
        }
        finally
        {
            store.Remove(name, EnvironmentVariableTarget.User);
        }

        store.Get(name, EnvironmentVariableTarget.User).Should().BeNull();
    }
}
