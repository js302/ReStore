using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using System.Runtime.Versioning;

namespace ReStore.Gui.Wpf.Interop
{
    public static class WindowEffects
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38; // Windows 11 22H2+
        private const int DWMWA_MICA_EFFECT = 1029;       // Older API (22000)

        // DWM_SYSTEMBACKDROP_TYPE values
        private const int DWMSBT_AUTO = 0;
        private const int DWMSBT_NONE = 1;
        private const int DWMSBT_MAINWINDOW = 2; // Mica
        private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
        private const int DWMSBT_TABBEDWINDOW = 4; // Mica Alt

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void ApplySystemBackdrop(Window window)
        {
            try
            {
                var handle = GetHandle(window);
                if (handle == IntPtr.Zero) return;

                // Try new backdrop API first (22H2+) - Mica for main window
                int backdrop = DWMSBT_MAINWINDOW;
                var result = DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

                // Fallback to legacy Mica attribute (22000) if new API not available
                if (result != 0)
                {
                    int micaEnabled = 1;
                    _ = DwmSetWindowAttribute(handle, DWMWA_MICA_EFFECT, ref micaEnabled, sizeof(int));
                }

                // Match title bar with app theme
                SetImmersiveDarkMode(window);
            }
            catch
            {
                // Ignore if not supported (Windows 10 or older)
            }
        }

        public static void SetImmersiveDarkMode(Window window)
        {
            try
            {
                var handle = GetHandle(window);
                if (handle == IntPtr.Zero) return;

                bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
                int useDark = dark ? 1 : 0;
                _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
            catch
            {
                // ignore
            }
        }

        private static IntPtr GetHandle(Window window)
        {
            if (!window.IsInitialized)
            {
                // Ensure handle after SourceInitialized
                return IntPtr.Zero;
            }
            var helper = new WindowInteropHelper(window);
            return helper.EnsureHandle();
        }

        // Ensure maximized window does not overlap taskbar
        public static void FixMaximizedBounds(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            HwndSource? source = HwndSource.FromHwnd(hwnd);
            if (source == null) return;
            source.AddHook(WndProc);
        }

        private const int WM_GETMINMAXINFO = 0x0024;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        private const int MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MONITORINFOEX
        {
            public int cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public char[] szDevice = new char[32];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var monitorInfo = new MONITORINFOEX();
                    if (GetMonitorInfo(monitor, ref monitorInfo))
                    {
                        var rcWork = monitorInfo.rcWork;
                        var rcMonitor = monitorInfo.rcMonitor;
                        mmi.ptMaxPosition.X = Math.Abs(rcWork.Left - rcMonitor.Left);
                        mmi.ptMaxPosition.Y = Math.Abs(rcWork.Top - rcMonitor.Top);
                        mmi.ptMaxSize.X = Math.Abs(rcWork.Right - rcWork.Left);
                        mmi.ptMaxSize.Y = Math.Abs(rcWork.Bottom - rcWork.Top);
                        Marshal.StructureToPtr(mmi, lParam, true);
                        handled = true;
                    }
                }
            }
            return IntPtr.Zero;
        }
    }
}
