namespace ReStore.Core.src.utils;

/// <summary>
/// True when nobody can answer prompts (CLI <c>--service</c>, or a process without an interactive desktop).
/// Providers use it to fail fast with an actionable message instead of waiting for interactive sign-in.
/// </summary>
public static class UnattendedMode
{
    private static volatile bool _forced;

    public static bool IsActive => _forced || !Environment.UserInteractive;

    public static void Force(bool value) => _forced = value;
}
