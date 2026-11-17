namespace ReStore.Tests;

/// <summary>Skips (visibly, instead of silently passing) when the platform or privilege a test needs is not available.</summary>
internal static class TestPlatform
{
    public const string SystemTestsVariable = "RESTORE_WINDOWS_SYSTEM_TESTS";

    public static bool SystemTestsEnabled => Environment.GetEnvironmentVariable(SystemTestsVariable) == "1";

    /// <summary>Null when the test may run; otherwise the skip reason.</summary>
    public static string? WindowsSkip() => OperatingSystem.IsWindows() ? null : "Windows only.";

    public static string? NonWindowsSkip() => OperatingSystem.IsWindows() ? "Needs POSIX behaviour (sh, symlinks, file modes); not applicable on Windows." : null;

    public static string? PosixPermissionsSkip() =>
        NonWindowsSkip() ?? (Environment.IsPrivilegedProcess ? "Needs POSIX permissions that are enforced for this user (not root)." : null);

    public static string? WindowsSystemSkip() =>
        WindowsSkip() ?? (SystemTestsEnabled ? null : $"Touches real Windows state (Task Scheduler, registry, DPAPI, user environment): set {SystemTestsVariable}=1 to run.");
}

/// <summary>A fact that only runs on Windows and shows as skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() => Skip = TestPlatform.WindowsSkip();
}

/// <summary>A theory that only runs on Windows and shows as skipped elsewhere.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute() => Skip = TestPlatform.WindowsSkip();
}

/// <summary>A fact that relies on /bin/sh, symlinks or POSIX modes; skipped on Windows.</summary>
public sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute() => Skip = TestPlatform.NonWindowsSkip();
}

/// <summary>A fact that needs POSIX permission enforcement (non-Windows, not root).</summary>
public sealed class PosixPermissionsFactAttribute : FactAttribute
{
    public PosixPermissionsFactAttribute() => Skip = TestPlatform.PosixPermissionsSkip();
}

/// <summary>A Windows fact that changes real system state; also needs RESTORE_WINDOWS_SYSTEM_TESTS=1 (set by the windows CI job).</summary>
public sealed class WindowsSystemFactAttribute : FactAttribute
{
    public WindowsSystemFactAttribute() => Skip = TestPlatform.WindowsSystemSkip();
}

/// <summary>A Windows theory that changes real system state; also needs RESTORE_WINDOWS_SYSTEM_TESTS=1.</summary>
public sealed class WindowsSystemTheoryAttribute : TheoryAttribute
{
    public WindowsSystemTheoryAttribute() => Skip = TestPlatform.WindowsSystemSkip();
}

/// <summary>
/// A performance gate (always paired with <c>[Trait("Category", "Perf")]</c>). Timing and process-wide allocation numbers are only meaningful when
/// nothing else runs, so it is skipped unless <c>RESTORE_PERF_TESTS=1</c> (the nightly workflow sets it and runs the Perf category serially).
/// </summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RESTORE_PERF_TESTS") != "1")
        {
            Skip = "Perf gate: set RESTORE_PERF_TESTS=1 and run with --filter \"Category=Perf\" -- xUnit.ParallelizeTestCollections=false.";
        }
    }
}
