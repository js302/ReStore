using System.Diagnostics;

namespace ReStore.Core.src.updates;

/// <summary>
/// Starts the out-of-process MSIX installation. Isolated behind an interface because it cannot run off Windows: the unit tests
/// check the generated command line instead, and the real installation is a Windows smoke test.
/// </summary>
public interface IMsixInstallLauncher
{
    /// <param name="msixPath">The verified package file.</param>
    /// <param name="logPath">Where the installer script appends its outcome.</param>
    /// <param name="relaunchAppUserModelId"><c>PackageFamilyName!ReStore</c> to start again afterwards, or null to not relaunch.</param>
    void Launch(string msixPath, string logPath, string? relaunchAppUserModelId);
}

/// <summary>
/// A detached, hidden <c>powershell.exe</c> that waits for this process to exit, runs <c>Add-AppxPackage -ForceApplicationShutdown</c>,
/// logs the outcome and optionally relaunches the app through <c>explorer.exe shell:AppsFolder\...</c>.
/// Every path is embedded as a PowerShell single-quoted literal (nothing is expanded inside), with quote characters doubled.
/// <c>-ForceApplicationShutdown</c> can terminate a helper that is a child of the packaged app, so the helper is not started
/// directly: a short-lived powershell asks WMI (<c>Win32_Process.Create</c>) to start it, which creates the process under WmiPrvSE,
/// outside the package's process tree. The helper's script travels as <c>-EncodedCommand</c> (base64 of UTF-16LE), so no quoting survives to go wrong.
/// </summary>
public sealed class PowerShellMsixInstallLauncher : IMsixInstallLauncher
{
    public void Launch(string msixPath, string logPath, string? relaunchAppUserModelId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Installing an MSIX package needs Windows.");
        }

        using var process = Process.Start(BuildStartInfo(msixPath, logPath, relaunchAppUserModelId))
            ?? throw new InvalidOperationException("Could not start powershell.exe.");
    }

    public static ProcessStartInfo BuildStartInfo(string msixPath, string logPath, string? relaunchAppUserModelId)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in BuildArguments(msixPath, logPath, relaunchAppUserModelId))
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>The arguments after the executable: <c>-NoProfile ... -Command &lt;launcher script&gt;</c> (the script has no double quotes, so no argv escaping is involved).</summary>
    public static string[] BuildArguments(string msixPath, string logPath, string? relaunchAppUserModelId) =>
        ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-Command", BuildLauncherScript(msixPath, logPath, relaunchAppUserModelId)];

    /// <summary>The arguments of the helper itself: the install script as <c>-EncodedCommand</c>.</summary>
    public static string[] BuildHelperArguments(string msixPath, string logPath, string? relaunchAppUserModelId) =>
        ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-EncodedCommand", EncodeCommand(BuildScript(msixPath, logPath, relaunchAppUserModelId))];

    /// <summary>The command line WMI starts for the helper (<c>powershell.exe ... -EncodedCommand ...</c>).</summary>
    public static string BuildHelperCommandLine(string msixPath, string logPath, string? relaunchAppUserModelId) =>
        "powershell.exe " + string.Join(' ', BuildHelperArguments(msixPath, logPath, relaunchAppUserModelId));

    /// <summary>Base64 of the UTF-16LE text: what <c>powershell -EncodedCommand</c> expects.</summary>
    public static string EncodeCommand(string script) => Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// What the short-lived powershell runs: creates the helper through <c>Invoke-CimMethod Win32_Process Create</c> (so it is not in the
    /// package's process tree) and, only if WMI refuses, falls back to a plain <c>Start-Process</c> (better than not installing at all).
    /// </summary>
    public static string BuildLauncherScript(string msixPath, string logPath, string? relaunchAppUserModelId)
    {
        var helperArguments = BuildHelperArguments(msixPath, logPath, relaunchAppUserModelId);
        var script = new System.Text.StringBuilder();
        script.Append("try { ");
        script.Append($"$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{{ CommandLine = {Quote(BuildHelperCommandLine(msixPath, logPath, relaunchAppUserModelId))} }} -ErrorAction Stop; ");
        script.Append("if ($r.ReturnValue -ne 0) { throw ('Win32_Process.Create returned ' + $r.ReturnValue) } ");
        script.Append("} catch { ");
        script.Append($"Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @({string.Join(", ", helperArguments.Select(Quote))}) ");
        script.Append('}');
        return script.ToString();
    }

    public static string BuildScript(string msixPath, string logPath, string? relaunchAppUserModelId)
    {
        var script = new System.Text.StringBuilder();
        script.Append($"$log = {Quote(logPath)}; $pkg = {Quote(msixPath)}; ");
        script.Append("function Log($m) { try { New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null; Add-Content -LiteralPath $log -Value ((Get-Date).ToString('o') + ' ' + $m) } catch { } }; ");
        script.Append("Start-Sleep -Seconds 2; ");
        script.Append("try { Log ('Installing ' + $pkg); Add-AppxPackage -Path $pkg -ForceApplicationShutdown -ErrorAction Stop; Log 'Update installed.' } ");
        script.Append("catch { Log ('Update failed: ' + $_.Exception.Message) }");
        if (!string.IsNullOrWhiteSpace(relaunchAppUserModelId))
        {
            script.Append($"; Start-Process -FilePath 'explorer.exe' -ArgumentList {Quote(@"shell:AppsFolder\" + relaunchAppUserModelId)}");
        }

        return script.ToString();
    }

    /// <summary>A PowerShell single-quoted string: <c>'</c> and the typographic single quotes (which PowerShell also treats as quotes) are doubled.</summary>
    public static string Quote(string value)
    {
        var text = new System.Text.StringBuilder("'");
        foreach (var c in value)
        {
            text.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
            {
                text.Append(c);
            }
        }

        return text.Append('\'').ToString();
    }
}
