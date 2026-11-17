using System.Runtime.CompilerServices;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Points <see cref="AppPaths"/> at a unique per-run temp directory so tests never read or write
/// the real user profile (~/ReStore). The directory is removed when the test host exits.
/// </summary>
internal static class TestEnvironment
{
    public static string RootDirectory { get; } = Path.Combine(
        Path.GetTempPath(), "ReStoreTests", "home_" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(RootDirectory);
        AppPaths.OverrideRoot(RootDirectory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(RootDirectory, recursive: true);
                var parent = Path.GetDirectoryName(RootDirectory);
                if (parent != null && !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        };
    }
}
