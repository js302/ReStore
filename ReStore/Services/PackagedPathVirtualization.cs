using System.IO;
namespace ReStore.Services
{
    /// <summary>
    /// A packaged (MSIX) app that writes under %APPDATA% or %LOCALAPPDATA% is redirected to its private copy of those
    /// folders, so a restore "into" them is invisible to other programs. This decides when to warn about it.
    /// </summary>
    public static class PackagedPathVirtualization
    {
        public static bool IsAffected(string? targetPath, bool isPackaged, string? roamingAppData, string? localAppData)
        {
            if (!isPackaged || string.IsNullOrWhiteSpace(targetPath))
            {
                return false;
            }

            return IsUnder(targetPath, roamingAppData) || IsUnder(targetPath, localAppData);
        }

        public static bool IsAffected(string? targetPath, bool isPackaged) =>
            IsAffected(
                targetPath,
                isPackaged,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        public const string Warning =
            "ReStore is installed as a Store (MSIX) app. Windows redirects files this app writes under %APPDATA% or " +
            "%LOCALAPPDATA% into the app's private copy of those folders, so other programs may not see the restored files. " +
            "Restore to another folder and copy the files across, or use the installer version of ReStore.";

        private static bool IsUnder(string path, string? root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\', '/');
                var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
                return full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(rootFull + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }
    }
}
