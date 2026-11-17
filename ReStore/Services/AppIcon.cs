using System.IO;

namespace ReStore.Services
{
    /// <summary>
    /// The application icon for tray icons. <c>icon.ico</c> ships as a Content file under Assets (not a WPF
    /// resource), so it is read from disk; the icon embedded in the exe is the fallback.
    /// </summary>
    internal static class AppIcon
    {
        public static System.Drawing.Icon Load()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
                if (File.Exists(path))
                {
                    return new System.Drawing.Icon(path);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Could not load Assets/icon.ico: {ex.Message}");
            }

            try
            {
                if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon)
                {
                    return icon;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Could not extract the exe icon: {ex.Message}");
            }

            return System.Drawing.SystemIcons.Application;
        }
    }
}
