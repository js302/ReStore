namespace ReStore.Core.src.utils;

/// <summary>
/// Single source of truth for where ReStore keeps its per-user data.
/// Root precedence: <see cref="OverrideRoot"/> (tests), then the <c>RESTORE_HOME</c> environment
/// variable, then the default <c>&lt;UserProfile&gt;/ReStore</c>. Paths are resolved on every access
/// so an override applied after startup is honoured.
/// </summary>
public static class AppPaths
{
    public const string RootEnvironmentVariable = "RESTORE_HOME";

    private static volatile string? _overrideRoot;

    /// <summary>Sets (or, with null/blank, clears) the in-process root override.</summary>
    public static void OverrideRoot(string? root)
    {
        _overrideRoot = string.IsNullOrWhiteSpace(root) ? null : root;
    }

    /// <summary>True while a test (or other in-process caller) has overridden the root with <see cref="OverrideRoot"/>.</summary>
    public static bool IsRootOverridden => _overrideRoot != null;

    /// <summary>The ReStore data root (default <c>%USERPROFILE%\ReStore</c>).</summary>
    public static string Root
    {
        get
        {
            var overrideRoot = _overrideRoot;
            if (overrideRoot != null)
            {
                return overrideRoot;
            }

            var fromEnvironment = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "ReStore");
        }
    }

    public static string ConfigDirectory => Root;
    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string ExampleConfigFile => Path.Combine(Root, "config.example.json");
    public static string AppSettingsFile => Path.Combine(Root, "appsettings.json");

    /// <summary>Directory for persisted runtime state (system_state.json, locks, ...).</summary>
    public static string StateDirectory => Path.Combine(Root, "state");
    public static string SystemStateFile => Path.Combine(StateDirectory, "system_state.json");

    public static string LogsDirectory => Path.Combine(Root, "logs");
    public static string LogFile => Path.Combine(LogsDirectory, "restore.log");

    /// <summary>
    /// Directory for GUI-local settings (theme.json). Default is <c>%LOCALAPPDATA%\ReStore</c>;
    /// when the root is overridden it moves under the root so tests never touch the real profile.
    /// </summary>
    public static string LocalSettingsDirectory
    {
        get
        {
            if (_overrideRoot != null || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootEnvironmentVariable)))
            {
                return Path.Combine(Root, "local");
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ReStore");
        }
    }

    /// <summary>
    /// Private scratch space for system restores (downloaded and decrypted archives). Default
    /// <c>%LOCALAPPDATA%\ReStore\restore-work</c>, never <c>%TEMP%</c>; moves under the root when it is overridden.
    /// Each restore uses its own GUID subdirectory and deletes it afterwards.
    /// </summary>
    public static string RestoreWorkDirectory => Path.Combine(LocalSettingsDirectory, "restore-work");

    /// <summary>Undo points written before a system restore applies changes (<c>&lt;Root&gt;/restore-undo/&lt;timestamp&gt;</c>).</summary>
    public static string RestoreUndoDirectory => Path.Combine(Root, "restore-undo");

    public static string ThemeSettingsFile => Path.Combine(LocalSettingsDirectory, "theme.json");
}
