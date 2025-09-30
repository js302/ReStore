using System;
using System.IO;
using System.Text.Json;
using Wpf.Ui.Appearance;

namespace ReStore.Gui.Wpf.Services
{
    public enum ThemePreference
    {
        System,
        Light,
        Dark
    }

    public class ThemeSettings
    {
        private const string SETTINGS_FILE = "theme.json";
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReStore",
            SETTINGS_FILE);

        public ThemePreference Preference { get; set; } = ThemePreference.System;

        public static ThemeSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var s = JsonSerializer.Deserialize<ThemeSettings>(json);
                    if (s != null) return s;
                }
            }
            catch { }
            return new ThemeSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }

        public void Apply()
        {
            switch (Preference)
            {
                case ThemePreference.Light:
                    ApplicationThemeManager.Apply(ApplicationTheme.Light);
                    break;
                case ThemePreference.Dark:
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                    break;
                default:
                    ApplicationThemeManager.ApplySystemTheme(true);
                    break;
            }
        }
    }
}
