using System.Reflection;

namespace ReStore.Core.src.utils;

/// <summary>A crash report file found in the logs directory.</summary>
public sealed record CrashReportFile(string Path, string Name, DateTime LastWriteUtc, long Length);

/// <summary>A labelled ReStore data location shown on the About page.</summary>
public sealed record DataPathInfo(string Label, string Path, bool IsDirectory);

/// <summary>Facts for the About / diagnostics page, free of UI so they can be unit tested.</summary>
public static class AboutInfo
{
    public const string CrashFilePattern = "crash-*.txt";
    public const int DefaultMaxCrashReports = 10;

    /// <summary>The informational version (e.g. <c>1.7.0</c>), without the <c>+commit</c> build metadata; "unknown" when absent.</summary>
    public static string FormatVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "unknown";
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var version = plus >= 0 ? informationalVersion[..plus] : informationalVersion;
        return string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
    }

    public static string VersionOf(Assembly assembly) =>
        FormatVersion(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The newest crash reports first; an absent or unreadable directory yields an empty list.</summary>
    public static List<CrashReportFile> FindCrashReports(string? logsDirectory = null, int max = DefaultMaxCrashReports)
    {
        var directory = logsDirectory ?? AppPaths.LogsDirectory;
        try
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            return new DirectoryInfo(directory)
                .EnumerateFiles(CrashFilePattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Take(Math.Max(0, max))
                .Select(f => new CrashReportFile(f.FullName, f.Name, f.LastWriteTimeUtc, f.Length))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Where ReStore keeps its data, for "Open" buttons.</summary>
    public static List<DataPathInfo> DataPaths() =>
    [
        new("Data folder", AppPaths.Root, true),
        new("Configuration", AppPaths.ConfigFile, false),
        new("State", AppPaths.StateDirectory, true),
        new("Logs", AppPaths.LogsDirectory, true),
        new("Local settings", AppPaths.LocalSettingsDirectory, true)
    ];
}
