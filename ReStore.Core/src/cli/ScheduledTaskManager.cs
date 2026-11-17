using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary>Starts an external program; <see cref="ProcessRunner.RunAsync"/> in production, a fake in tests.</summary>
internal delegate Task<ProcessResult> ProcessInvoker(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

/// <summary>What <c>schedule status</c> found out about the task.</summary>
internal sealed record ScheduledTaskStatus(
    bool Installed,
    bool? Enabled = null,
    int? IntervalMinutes = null,
    string? Executable = null,
    string? Arguments = null,
    string? State = null,
    string? LastRun = null,
    DateTime? LastRunUtc = null,
    int? LastResult = null,
    string? NextRun = null,
    string? Detail = null)
{
    /// <summary>Human text for <see cref="LastResult"/> (0x41303, "has not run yet", is the common one).</summary>
    public string? LastResultText => LastResult switch
    {
        null => null,
        0 => "success",
        267011 => "the task has not run yet",
        267009 => "the task is currently running",
        267014 => "the task was terminated by the user",
        _ => $"exit code {LastResult} (0x{LastResult.Value:X})"
    };
}

/// <summary>
/// The Windows Task Scheduler task <c>\ReStore\run-due</c> that runs <c>restore run-due --quiet</c> every N minutes as the current
/// user, only while that user is logged on (an interactive token: no Windows password is stored), hidden, on battery too.
/// The console program is started through <c>conhost.exe --headless</c> so no console window flashes (the task's Hidden flag does
/// not hide a console app). conhost does not pass the exit code on: <c>run-due</c> records its own outcome (see <see cref="RunDueRecord"/>).
/// Everything goes through <c>schtasks.exe</c> (never the COM API), started by an injectable <see cref="ProcessInvoker"/> so the
/// command lines and the parsing are unit-tested without a Windows machine.
/// </summary>
internal sealed class ScheduledTaskManager(ProcessInvoker runner, Func<string>? currentUser = null, Func<bool>? conhostAvailable = null)
{
    public const string TaskName = @"\ReStore\run-due";
    public const string TaskArguments = "run-due --quiet";

    /// <summary>The console host that runs a console program without a window (<c>--headless</c>); Task Scheduler expands <c>%SystemRoot%</c>.</summary>
    public const string ConhostCommand = @"%SystemRoot%\System32\conhost.exe";
    public const string ConhostSwitch = "--headless";
    public const int DefaultIntervalMinutes = 15;
    public const int MaxIntervalMinutes = 1440;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);
    private const string SchedulerNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private readonly Func<bool> _conhostAvailable = conhostAvailable ?? DefaultConhostAvailable;

    private readonly Func<string> _currentUser = currentUser ?? (() => $"{Environment.UserDomainName}\\{Environment.UserName}");

    public static string SchtasksExecutable => "schtasks.exe";

    /// <summary>False on a very old Windows without <c>conhost.exe</c> (and off Windows): the task then runs the exe directly.</summary>
    public bool UsesConhost => _conhostAvailable();

    private static bool DefaultConhostAvailable()
    {
        try
        {
            return OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.SystemDirectory, "conhost.exe"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Install = create or replace (<c>/F</c>), so running it again with another interval just changes the interval.</summary>
    public static IReadOnlyList<string> BuildCreateArguments(string xmlFile) => ["/Create", "/TN", TaskName, "/XML", xmlFile, "/F"];

    public static IReadOnlyList<string> BuildDeleteArguments() => ["/Delete", "/TN", TaskName, "/F"];

    /// <summary>The definition as XML: language-independent, so it also reads back on a localized Windows.</summary>
    public static IReadOnlyList<string> BuildQueryXmlArguments() => ["/Query", "/TN", TaskName, "/XML"];

    public static IReadOnlyList<string> BuildQueryVerboseArguments() => ["/Query", "/TN", TaskName, "/V", "/FO", "CSV"];

    /// <summary>
    /// The task definition. Runs as the current user with an interactive token (only when logged on, no stored password) at
    /// least privilege; hidden; allowed on battery; a missed run starts when possible; a still-running previous run is not doubled.
    /// </summary>
    /// <param name="useConhost">Run <c>conhost.exe --headless "exe" run-due --quiet</c> (no window) instead of the exe directly.</param>
    public static string BuildTaskXml(string executablePath, int intervalMinutes, string? user, DateTime startLocal, bool useConhost = false)
    {
        var principal = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(user))
        {
            principal.Append(CultureInfo.InvariantCulture, $"<UserId>{SecurityElement.Escape(user)}</UserId>");
        }

        // The command is quoted so a path with spaces survives; the quotes are part of the value Task Scheduler runs.
        var command = SecurityElement.Escape(useConhost ? ConhostCommand : $"\"{executablePath}\"");
        var arguments = SecurityElement.Escape(useConhost ? $"{ConhostSwitch} \"{executablePath}\" {TaskArguments}" : TaskArguments);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="{SchedulerNamespace}">
              <RegistrationInfo>
                <Description>ReStore: runs the due backups, verification and retention (restore run-due). Created by 'restore schedule install'; remove it with 'restore schedule remove'.</Description>
              </RegistrationInfo>
              <Triggers>
                <TimeTrigger>
                  <Repetition>
                    <Interval>PT{intervalMinutes.ToString(CultureInfo.InvariantCulture)}M</Interval>
                    <StopAtDurationEnd>false</StopAtDurationEnd>
                  </Repetition>
                  <StartBoundary>{startLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}</StartBoundary>
                  <Enabled>true</Enabled>
                </TimeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  {principal}
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>true</Hidden>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT12H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{arguments}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Creates (or replaces) the task. Returns null on success, otherwise the failure text.</summary>
    public async Task<string?> InstallAsync(string executablePath, int intervalMinutes, CancellationToken cancellationToken)
    {
        // schtasks reads the XML from a file; UTF-16 with a BOM is what it expects (and what the declaration says).
        var xmlFile = Path.Combine(Path.GetTempPath(), $"restore-run-due-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(
                xmlFile, BuildTaskXml(executablePath, intervalMinutes, _currentUser(), DateTime.Now, UsesConhost), new UnicodeEncoding(false, true), cancellationToken).ConfigureAwait(false);
            var result = await runner(SchtasksExecutable, BuildCreateArguments(xmlFile), CommandTimeout, cancellationToken).ConfigureAwait(false);
            return result.TimedOut ? "schtasks.exe did not finish in time." : result.ExitCode == 0 ? null : Describe(result);
        }
        finally
        {
            try { File.Delete(xmlFile); } catch (IOException) { /* temp file; best effort */ } catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>Deletes the task. Returns (removed, error): removing a task that is not there is not an error (removed false, no error).</summary>
    public async Task<(bool Removed, string? Error)> RemoveAsync(CancellationToken cancellationToken)
    {
        var existing = await QueryAsync(cancellationToken).ConfigureAwait(false);
        if (!existing.Installed)
        {
            return (false, null);
        }

        var result = await runner(SchtasksExecutable, BuildDeleteArguments(), CommandTimeout, cancellationToken).ConfigureAwait(false);
        return result.TimedOut ? (false, "schtasks.exe did not finish in time.") : result.ExitCode == 0 ? (true, null) : (false, Describe(result));
    }

    public async Task<ScheduledTaskStatus> QueryAsync(CancellationToken cancellationToken)
    {
        var xml = await runner(SchtasksExecutable, BuildQueryXmlArguments(), CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (xml.TimedOut)
        {
            return new ScheduledTaskStatus(false, Detail: "schtasks.exe did not finish in time.");
        }

        if (xml.ExitCode != 0)
        {
            // schtasks exits 1 for a task that does not exist; the text is localized, so only the code is trusted.
            return new ScheduledTaskStatus(false, Detail: Describe(xml));
        }

        var verbose = await runner(SchtasksExecutable, BuildQueryVerboseArguments(), CommandTimeout, cancellationToken).ConfigureAwait(false);
        return ParseStatus(xml.StandardOutput, verbose.ExitCode == 0 ? verbose.StandardOutput : null);
    }

    /// <summary>Reads the definition (<c>/Query /XML</c>) and, when given, the <c>/V /FO CSV</c> row of the installed task.</summary>
    internal static ScheduledTaskStatus ParseStatus(string definitionXml, string? verboseCsv)
    {
        bool? enabled = null;
        int? interval = null;
        string? executable = null, arguments = null;
        try
        {
            var document = XDocument.Parse(definitionXml.TrimStart('\uFEFF', ' ', '\r', '\n'));
            XNamespace ns = document.Root?.Name.Namespace ?? SchedulerNamespace;
            var settingsEnabled = document.Descendants(ns + "Settings").Elements(ns + "Enabled").FirstOrDefault()?.Value;
            // Windows omits <Enabled> from an exported definition when it has the default value (true).
            enabled = settingsEnabled == null || !settingsEnabled.Equals("false", StringComparison.OrdinalIgnoreCase);
            interval = ParseIsoMinutes(document.Descendants(ns + "Repetition").Elements(ns + "Interval").FirstOrDefault()?.Value);
            var exec = document.Descendants(ns + "Exec").FirstOrDefault();
            executable = exec?.Element(ns + "Command")?.Value.Trim().Trim('"');
            arguments = exec?.Element(ns + "Arguments")?.Value.Trim();
            (executable, arguments) = UnwrapConhost(executable, arguments);
        }
        catch (System.Xml.XmlException)
        {
            // The task exists (schtasks answered); only its details are unknown.
        }

        string? state = null, lastRun = null, nextRun = null;
        DateTime? lastRunUtc = null;
        int? lastResult = null;
        if (!string.IsNullOrWhiteSpace(verboseCsv))
        {
            var rows = ParseCsv(verboseCsv);
            if (rows.Count >= 2)
            {
                var header = rows[0];
                var row = rows[1];

                // Column titles are localized; the order is not, so fall back to the English order.
                string? Column(string title, int fallbackIndex)
                {
                    var index = header.FindIndex(h => h.Equals(title, StringComparison.OrdinalIgnoreCase));
                    if (index < 0 && header.Count > fallbackIndex && header.Count >= 9)
                    {
                        index = fallbackIndex;
                    }

                    return index >= 0 && index < row.Count ? row[index] : null;
                }

                nextRun = Column("Next Run Time", 2);
                state = Column("Status", 3);
                lastRun = Column("Last Run Time", 5);
                lastResult = int.TryParse(Column("Last Result", 6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
                lastRunUtc = DateTime.TryParse(lastRun, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed) && parsed.Year > 1999
                    ? parsed.ToUniversalTime()
                    : null;
            }
        }

        return new ScheduledTaskStatus(true, enabled, interval, executable, arguments, state, lastRun, lastRunUtc, lastResult, nextRun);
    }

    /// <summary>A task started through <c>conhost.exe --headless "exe" args</c> is reported as running <c>exe args</c>.</summary>
    internal static (string? Executable, string? Arguments) UnwrapConhost(string? executable, string? arguments)
    {
        if (executable == null || arguments == null
            || !Path.GetFileName(executable.Replace('\\', '/')).Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)
            || !arguments.StartsWith(ConhostSwitch + " ", StringComparison.OrdinalIgnoreCase))
        {
            return (executable, arguments);
        }

        var rest = arguments[(ConhostSwitch.Length + 1)..].TrimStart();
        if (rest.StartsWith('"'))
        {
            var close = rest.IndexOf('"', 1);
            if (close > 0)
            {
                return (rest[1..close], rest[(close + 1)..].Trim());
            }
        }

        var space = rest.IndexOf(' ');
        return space < 0 ? (rest, string.Empty) : (rest[..space], rest[(space + 1)..].Trim());
    }

    /// <summary>Whole minutes of an ISO 8601 duration such as <c>PT15M</c> or <c>PT1H30M</c>; null when it cannot be read.</summary>
    internal static int? ParseIsoMinutes(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return null;
        }

        try
        {
            return (int)System.Xml.XmlConvert.ToTimeSpan(iso.Trim()).TotalMinutes;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Minimal RFC 4180 reader for the output of <c>schtasks /FO CSV</c> (quoted fields, doubled quotes).</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    any = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    if (any || field.Length > 0)
                    {
                        row.Add(field.ToString());
                        rows.Add(row);
                    }

                    row = [];
                    field.Clear();
                    any = false;
                    break;
                default:
                    field.Append(c);
                    any = true;
                    break;
            }
        }

        if (any || field.Length > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static string Describe(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return $"schtasks.exe exited with {result.ExitCode}: {text.Trim()}".TrimEnd(' ', ':');
    }
}
