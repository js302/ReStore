using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReStore.Services
{
    public class AppSettings
    {
        private static string SettingsPath => ReStore.Core.src.utils.AppPaths.AppSettingsFile;

        /// <summary>
        /// Legacy. The default storage now lives only in config.json (<c>globalStorageType</c>). This is read
        /// from old appsettings files so it can be dropped by <see cref="ConsumeLegacyDefaultStorage"/>;
        /// nothing writes it and it is never serialized again.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DefaultStorage { get; set; }

        /// <summary>Clears the legacy value; true when there was one (the file should then be re-saved without it).</summary>
        public bool ConsumeLegacyDefaultStorage()
        {
            var had = DefaultStorage != null;
            DefaultStorage = null;
            return had;
        }

        public bool ShowOnlyConfiguredProviders { get; set; }
        public bool MinimizeToTray { get; set; } = false;

        /// <summary>
        /// Show Windows toasts (with action buttons) instead of tray balloons. Effective only when packaged: toasts need
        /// the MSIX identity, so unpackaged runs always use balloons.
        /// </summary>
        public bool UseToastNotifications { get; set; } = true;

        public bool ContextMenuEnabled { get; set; } = false;

        /// <summary>Last main-window size/position/maximized state; null until the window has been closed once.</summary>
        public WindowPlacementSettings? MainWindowPlacement { get; set; }

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
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to load app settings from {SettingsPath}: {ex.Message}");
            }
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
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to save app settings to {SettingsPath}: {ex.Message}");
            }
        }
    }
}
