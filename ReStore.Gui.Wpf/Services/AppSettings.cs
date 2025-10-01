using System;
using System.IO;
using System.Text.Json;

namespace ReStore.Gui.Wpf.Services
{
    public class AppSettings
    {
        private const string SETTINGS_FILE = "appsettings.json";
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReStore",
            SETTINGS_FILE);

        public string? DefaultStorage { get; set; }
        public bool ShowOnlyConfiguredProviders { get; set; }
        public bool MinimizeToTray { get; set; } = true;

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) return s;
                }
            }
            catch { }
            return new AppSettings();
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
    }
}
