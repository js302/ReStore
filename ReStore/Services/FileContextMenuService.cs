using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace ReStore.Services
{
    public static class FileContextMenuService
    {
        private const string REGISTRY_KEY_PATH = @"Software\Classes\*\shell\ReStoreShare";
        private const string COMMAND_SUBKEY = "command";
        private const string MENU_ITEM_TEXT = "Share with ReStore";

        public static bool IsContextMenuEnabled()
        {
            if (!OperatingSystem.IsWindows()) return false;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY_PATH, false);
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        public static (bool Success, string? ErrorMessage) RegisterContextMenu()
        {
            if (!OperatingSystem.IsWindows())
            {
                return (false, "Context menu registration is only supported on Windows.");
            }

            try
            {
                var exePath = GetExecutablePath();
                if (string.IsNullOrEmpty(exePath))
                {
                    return (false, "Unable to determine application path.");
                }

                var command = $"\"{exePath}\" --share \"%1\"";

                using var shellKey = Registry.CurrentUser.CreateSubKey(REGISTRY_KEY_PATH, true);
                if (shellKey == null)
                {
                    return (false, "Unable to create registry key for context menu.");
                }

                shellKey.SetValue(null, MENU_ITEM_TEXT);
                shellKey.SetValue("Icon", exePath);

                using var commandKey = shellKey.CreateSubKey(COMMAND_SUBKEY, true);
                if (commandKey == null)
                {
                    return (false, "Unable to create command registry key.");
                }

                commandKey.SetValue(null, command);

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to register context menu: {ex.Message}");
            }
        }

        public static (bool Success, string? ErrorMessage) UnregisterContextMenu()
        {
            if (!OperatingSystem.IsWindows())
            {
                return (false, "Context menu unregistration is only supported on Windows.");
            }

            try
            {
                using var parentKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell", true);
                if (parentKey == null)
                {
                    return (true, null);
                }

                try
                {
                    parentKey.DeleteSubKeyTree("ReStoreShare", false);
                }
                catch (ArgumentException)
                {
                    // Key doesn't exist, nothing to delete - this is fine
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to unregister context menu: {ex.Message}");
            }
        }

        private static string? GetExecutablePath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var installedPath = System.IO.Path.Combine(localAppData, "Microsoft", "WindowsApps", "restore.exe");

            if (System.IO.File.Exists(installedPath))
            {
                return installedPath;
            }

            return Process.GetCurrentProcess().MainModule?.FileName;
        }
    }
}
