namespace ReStore.E2E;

/// <summary>Skips (visibly, instead of silently passing) when the platform a test needs is not available. Same pattern as ReStore.Tests/TestAttributes.cs.</summary>
internal static class TestPlatform
{
    /// <summary>Null when the test may run; otherwise the skip reason.</summary>
    public static string? WindowsSkip() => OperatingSystem.IsWindows() ? null : "Windows only.";

    public static string? NonWindowsSkip() => OperatingSystem.IsWindows() ? "Needs POSIX behaviour (SIGINT, symlinks, file modes); not applicable on Windows." : null;
}

/// <summary>A fact that only runs on Windows and shows as skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() => Skip = TestPlatform.WindowsSkip();
}

/// <summary>A fact that relies on POSIX signals or modes; skipped on Windows.</summary>
public sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute() => Skip = TestPlatform.NonWindowsSkip();
}
