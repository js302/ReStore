namespace ReStore.Core.src.utils;

public enum StartupWindowMode
{
    Normal,

    /// <summary>Window hidden, only the tray icon visible.</summary>
    HiddenInTray,

    /// <summary>Window minimised to the taskbar (used when there is no tray icon to restore it from).</summary>
    MinimizedToTaskbar
}

/// <summary>What the GUI should do at launch and whether "run at startup" can be offered.</summary>
public sealed record StartupDecision(StartupWindowMode WindowMode, bool CanConfigureRunAtStartup);

public static class StartupMode
{
    /// <summary>Argument written into the HKCU Run entry so a login launch starts out of the way.</summary>
    public const string StartupArgument = "--startup";

    public static bool IsStartupArgument(string? arg) =>
        string.Equals(arg, StartupArgument, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A login launch (<c>--startup</c>) starts hidden in the tray when tray mode is enabled (otherwise the
    /// user could not reach the window), or minimised to the taskbar when it is not. The HKCU Run key only
    /// applies to unpackaged installs, so that toggle is offered only then; packaged builds use the MSIX
    /// startup task instead. <paramref name="activatedByStartupTask"/> marks a launch Windows made for the
    /// MSIX startup task (which cannot carry <c>--startup</c>).
    /// </summary>
    public static StartupDecision Decide(bool isPackaged, IReadOnlyList<string>? args, bool minimizeToTray, bool activatedByStartupTask = false)
    {
        var startupLaunch = activatedByStartupTask || (args != null && args.Any(IsStartupArgument));

        var mode = !startupLaunch
            ? StartupWindowMode.Normal
            : minimizeToTray ? StartupWindowMode.HiddenInTray : StartupWindowMode.MinimizedToTaskbar;

        return new StartupDecision(mode, CanConfigureRunAtStartup: !isPackaged);
    }
}

/// <summary>Mirror of <c>Windows.ApplicationModel.StartupTaskState</c> so the UI mapping is testable off-Windows.</summary>
public enum StartupTaskStatus
{
    Disabled,
    DisabledByUser,
    DisabledByPolicy,
    Enabled,
    EnabledByPolicy
}

/// <summary>How the "Run at startup" checkbox should look for a startup task state.</summary>
public sealed record StartupTaskUiState(bool IsChecked, bool CanToggle, string Note);

public static class StartupTaskUi
{
    public const string DefaultNote = "When enabled, ReStore will automatically start (minimised to the tray if tray mode is on) when you log in to Windows.";

    public static StartupTaskUiState Describe(StartupTaskStatus status) => status switch
    {
        StartupTaskStatus.Enabled => new(true, true, DefaultNote),
        StartupTaskStatus.Disabled => new(false, true, DefaultNote),
        StartupTaskStatus.DisabledByUser => new(false, false,
            "Startup was turned off in Windows. To allow it, turn ReStore on under Windows Settings > Apps > Startup."),
        StartupTaskStatus.DisabledByPolicy => new(false, false,
            "Starting at login is disabled by your organization's policy."),
        StartupTaskStatus.EnabledByPolicy => new(true, false,
            "Starting at login is enforced by your organization's policy."),
        _ => new(false, false, "The startup state could not be determined.")
    };
}
