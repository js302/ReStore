using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;

namespace ReStore.Interop
{
    public static class WindowEffects
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>Matches the window's system menu and caption area to the current app theme.</summary>
        public static void SetImmersiveDarkMode(Window window)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                int useDark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? 1 : 0;
                _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
            catch (EntryPointNotFoundException)
            {
                // dwmapi attribute not supported on this Windows version.
            }
            catch (DllNotFoundException)
            {
            }
        }
    }
}
