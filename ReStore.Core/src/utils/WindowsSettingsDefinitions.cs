namespace ReStore.Core.src.utils;

/// <summary>
/// The registry keys Windows settings backup exports. This is the single source of truth: the exporter
/// walks it, and <see cref="RegistryImportPolicy"/> derives its restore allow-list from it.
/// </summary>
public static class WindowsSettingsDefinitions
{
    public static Dictionary<string, List<RegistryKeyInfo>> CreateCategories()
    {
        return new Dictionary<string, List<RegistryKeyInfo>>
        {
            ["Personalization"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\Desktop", Category = "Personalization", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes", Category = "Personalization", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Category = "Personalization", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", Category = "Personalization", RequiresAdmin = false }
            },
            ["Explorer"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Category = "Explorer", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState", Category = "Explorer", RequiresAdmin = false }
            },
            ["Regional"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\International", Category = "Regional", RequiresAdmin = false }
            },
            ["Taskbar"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3", Category = "Taskbar", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", Category = "Taskbar", RequiresAdmin = false }
            },
            ["Mouse"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\Mouse", Category = "Mouse", RequiresAdmin = false },
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\Cursors", Category = "Mouse", RequiresAdmin = false }
            },
            ["Keyboard"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\Keyboard", Category = "Keyboard", RequiresAdmin = false }
            },
            ["Accessibility"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_CURRENT_USER\Control Panel\Accessibility", Category = "Accessibility", RequiresAdmin = false }
            },
            ["System"] = new List<RegistryKeyInfo>
            {
                new() { Path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\TimeZoneInformation", Category = "System", RequiresAdmin = true },
                new() { Path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", Category = "System", RequiresAdmin = true }
            }
        };
    }
}
