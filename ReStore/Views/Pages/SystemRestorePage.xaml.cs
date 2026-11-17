using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using ReStore.Core.src.backup;
using ReStore.Services;

namespace ReStore.Views.Pages
{
    public class SystemBackupItem
    {
        public string Type { get; set; } = "";
        public string Path { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string StorageType { get; set; } = "";
        public string? StorageBasePath { get; set; }
        public string TimestampLabel => DisplayTime.Format(Timestamp);
        public string TimestampTooltip => DisplayTime.Tooltip(Timestamp) + " (UTC)";
        public string TypeLabel => Type switch
        {
            "system_programs" => "Installed Programs",
            "system_environment" => "Environment Variables",
            "system_settings" => "Windows Settings",
            "system_devtools" => "Developer Tools",
            _ => "Unknown"
        };
        public string Icon => Type switch
        {
            "system_programs" => "💻",
            "system_environment" => "⚙️",
            "system_settings" => "🎨",
            "system_devtools" => "🛠️",
            _ => "📦"
        };
        public Brush IconColor => Type switch
        {
            "system_programs" => new SolidColorBrush(Color.FromRgb(16, 185, 129)),
            "system_environment" => new SolidColorBrush(Color.FromRgb(139, 92, 246)),
            "system_settings" => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            "system_devtools" => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
            _ => new SolidColorBrush(Color.FromRgb(100, 100, 100))
        };
        
        public string DisplayPath
        {
            get
            {
                // Only local storage has a folder path worth joining; "bucket\\system_backups/..." is meaningless.
                if (!string.Equals(StorageType, "local", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(StorageBasePath) || string.IsNullOrEmpty(Path))
                    return Path;

                if (Path.StartsWith("./") || Path.StartsWith(".\\"))
                {
                    var relativePath = Path.Substring(2);
                    return System.IO.Path.Combine(StorageBasePath, relativePath);
                }

                return System.IO.Path.Combine(StorageBasePath, Path);
            }
        }
    }

    public partial class SystemRestorePage : Page
    {
        private readonly Logger _logger = AppServices.Logger;
        private ConfigManager _configManager = null!;
        private SystemState? _state;
        private readonly ObservableCollection<SystemBackupItem> _systemBackups = new();
        private List<SystemBackupItem> _allBackups = new();
        private bool _initialized;
        private RepositoryBusyBinding? _busyBinding;
        private bool _repositoryBusy;

        public SystemRestorePage()
        {
            InitializeComponent();

            Unloaded += (_, __) =>
            {
                _busyBinding?.Dispose();
                _busyBinding = null;
            };

            SystemBackupsList.ItemsSource = _systemBackups;

            RefreshBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Load system backups", () => LoadSystemBackupsAsync());
            BackupProgramsBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Backup programs", () => BackupProgramsAsync());
            BackupEnvBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Backup environment", () => BackupEnvironmentAsync());
            BackupSettingsBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Backup windows settings", () => BackupWindowsSettingsAsync());
            BackupFullSystemBtn.Click += async (_, __) => await ErrorReporter.GuardAsync("Backup full system", () => BackupFullSystemAsync());
            OpenBackupFolderBtn.Click += (_, __) => OpenBackupFolder();
            UndoPointsBtn.Click += (_, __) => new Windows.UndoPointsWindow { Owner = Window.GetWindow(this) }.ShowDialog();
            FilterTypeCombo.SelectionChanged += (_, __) => ApplyFilters();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _configManager = await AppServices.GetConfigManagerAsync();
                _state = await AppServices.GetSystemStateAsync();

                await LoadSystemBackupsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Loading the page", () => Page_LoadedCoreAsync(sender, e));
        }

        private async Task Page_LoadedCoreAsync(object sender, RoutedEventArgs e)
        {
            var storyboard = new System.Windows.Media.Animation.Storyboard();
            
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            storyboard.Children.Add(fadeIn);
            storyboard.Begin(this);

            if (!_initialized)
            {
                _initialized = true;
                await InitializeAsync();
            }
            else
            {
                // The page is cached across navigation, so show what changed while it was away.
                await LoadSystemBackupsAsync();
            }

            _busyBinding ??= new RepositoryBusyBinding(Dispatcher, ApplyBusyState);
        }

        /// <summary>The four backup buttons are unavailable while any operation (including their own) holds the repository.</summary>
        private void ApplyBusyState(bool busy, string? reason)
        {
            _repositoryBusy = busy;
            var tip = busy ? $"{RepositoryBusyBinding.BusyMessage} {reason}".Trim() : null;
            foreach (var button in new[] { BackupProgramsBtn, BackupEnvBtn, BackupSettingsBtn, BackupFullSystemBtn })
            {
                button.IsEnabled = !busy;
                button.ToolTip = tip;
            }
        }

        private async Task LoadSystemBackupsAsync()
        {
            if (_state == null) return;

            await Task.Run(() =>
            {
                var items = new List<SystemBackupItem>();

                // Snapshot once under the state lock; the scheduler can append system backups
                // on its own thread while this page enumerates.
                foreach (var group in new[] { "system_programs", "system_environment", "system_settings", "system_devtools" })
                {
                    foreach (var backup in _state.GetBackupsForGroup(group))
                    {
                        items.Add(CreateBackupItem(group, backup));
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    _allBackups = items;
                    ApplyFilters();
                    UpdateStatistics();
                });
            });
        }

        private SystemBackupItem CreateBackupItem(string type, BackupInfo backup)
        {
            var storageType = ResolveStorageType(type, backup.StorageType);

            return new SystemBackupItem
            {
                Type = type,
                Path = backup.Path,
                Timestamp = backup.Timestamp,
                StorageType = storageType,
                StorageBasePath = GetStorageBasePath(storageType)
            };
        }

        private string ResolveStorageType(SystemBackupItem backup)
        {
            return ResolveStorageType(backup.Type, backup.StorageType);
        }

        private string ResolveStorageType(string backupType, string? recordedStorageType)
        {
            if (!string.IsNullOrWhiteSpace(recordedStorageType))
            {
                return recordedStorageType;
            }

            return backupType switch
            {
                "system_programs" => _configManager.SystemBackup.ProgramsStorageType
                    ?? _configManager.SystemBackup.StorageType
                    ?? _configManager.GlobalStorageType,
                "system_environment" => _configManager.SystemBackup.EnvironmentStorageType
                    ?? _configManager.SystemBackup.StorageType
                    ?? _configManager.GlobalStorageType,
                "system_settings" => _configManager.SystemBackup.SettingsStorageType
                    ?? _configManager.SystemBackup.StorageType
                    ?? _configManager.GlobalStorageType,
                "system_devtools" => _configManager.SystemBackup.DevToolsStorageType
                    ?? _configManager.SystemBackup.StorageType
                    ?? _configManager.GlobalStorageType,
                _ => _configManager.GlobalStorageType
            };
        }

        private string? GetStorageBasePath(string storageType)
        {
            return _configManager.StorageSources.TryGetValue(storageType, out var storageConfig)
                ? ReStore.Core.src.utils.ConfigManager.ExpandConfiguredPath(storageConfig.Path)
                : null;
        }

        private async Task<IStorage> CreateStorageForBackupAsync(SystemBackupItem backup)
        {
            var storageType = ResolveStorageType(backup);
            return await _configManager.CreateStorageAsync(storageType);
        }

        private void ApplyFilters()
        {
            var filtered = _allBackups.AsEnumerable();

            if (FilterTypeCombo.SelectedItem is ComboBoxItem filterItem)
            {
                var filterTag = filterItem.Tag?.ToString();
                filtered = filterTag switch
                {
                    "programs" => filtered.Where(b => b.Type == "system_programs"),
                    "environment" => filtered.Where(b => b.Type == "system_environment"),
                    "settings" => filtered.Where(b => b.Type == "system_settings"),
                    "devtools" => filtered.Where(b => b.Type == "system_devtools"),
                    _ => filtered
                };
            }

            filtered = filtered.OrderByDescending(b => b.Timestamp);

            _systemBackups.Clear();
            foreach (var item in filtered)
            {
                _systemBackups.Add(item);
            }

            UpdateStats();
            UpdateEmptyState();
        }

        private void UpdateStats()
        {
            var total = _systemBackups.Count;
            StatsText.Text = $"{total} system backup{(total != 1 ? "s" : "")}";
        }

        private void UpdateEmptyState()
        {
            EmptyState.Visibility = _systemBackups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateStatistics()
        {
            TotalBackupsText.Text = _allBackups.Count.ToString();

            LastProgramBackupText.Text = FormatLastBackup("system_programs");
            LastEnvBackupText.Text = FormatLastBackup("system_environment");
            LastSettingsBackupText.Text = FormatLastBackup("system_settings");
        }

        private string FormatLastBackup(string type)
        {
            var last = _allBackups
                .Where(b => b.Type == type)
                .OrderByDescending(b => b.Timestamp)
                .FirstOrDefault();

            if (last == null)
            {
                return "Never";
            }

            var timeSince = DateTime.UtcNow - last.Timestamp.ToUniversalTime();
            if (timeSince.TotalMinutes < 1)
                return "Just now";
            if (timeSince.TotalHours < 1)
                return $"{(int)timeSince.TotalMinutes}m ago";
            if (timeSince.TotalDays < 1)
                return $"{(int)timeSince.TotalHours}h ago";
            return DisplayTime.Format(last.Timestamp);
        }

        private Task BackupProgramsAsync() => RunSystemBackupAsync(
            "Backup Programs",
            "This will backup your list of installed programs.\n\nContinue?",
            "Backing up installed programs",
            "Programs backup completed successfully!",
            (manager, token) => manager.BackupInstalledProgramsAsync(token));

        private Task BackupEnvironmentAsync() => RunSystemBackupAsync(
            "Backup Environment",
            "This will backup your environment variables.\n\nContinue?",
            "Backing up environment variables",
            "Environment variables backup completed successfully!",
            (manager, token) => manager.BackupEnvironmentVariablesAsync(token));

        private Task BackupWindowsSettingsAsync() => RunSystemBackupAsync(
            "Backup Windows Settings",
            "This will backup your Windows settings including:\n• Personalization (themes, colors)\n• File Explorer preferences\n• Taskbar settings\n• Regional settings\n• Mouse and keyboard preferences\n• Accessibility options\n\nContinue?",
            "Backing up Windows settings",
            "Windows settings backup completed successfully!",
            (manager, token) => manager.BackupWindowsSettingsAsync(token));

        private Task BackupFullSystemAsync() => RunSystemBackupAsync(
            "Full System Backup",
            "This will backup:\n• Installed programs list\n• Environment variables\n• Windows settings\n\nContinue?",
            "Backing up the full system",
            "Full system backup completed successfully!",
            (manager, token) => manager.BackupSystemAsync(token));

        /// <summary>
        /// Confirms, then runs a system backup in the shared progress window (log, progress, Cancel) instead of
        /// leaving the page showing only "Backing up...". Cancelling stops at the next safe point.
        /// </summary>
        private async Task RunSystemBackupAsync(
            string title,
            string confirmText,
            string statusText,
            string successText,
            Func<SystemBackupManager, CancellationToken, Task> backup)
        {
            if (!OperatingSystem.IsWindows())
            {
                MessageBox.Show("System backup is only available on Windows.", "System Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirmation = MessageBox.Show(confirmText, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            if (_state == null)
            {
                MessageBox.Show("System not initialized properly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();
            var progressWindow = new Windows.OperationProgressWindow(title, _logger);

            progressWindow.RunModal(Window.GetWindow(this), async token =>
            {
                progressWindow.StatusProgress.Report(statusText + "...");
                var systemBackup = new SystemBackupManager(progressWindow, _configManager, _state, passwordProvider);
                await backup(systemBackup, token);
            });

            await LoadSystemBackupsAsync();

            if (progressWindow.Succeeded)
            {
                MessageBox.Show(successText, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (progressWindow.Failure != null)
            {
                MessageBox.Show($"{title} failed: {progressWindow.Failure.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Restore backup", () => RestoreBackup_ClickCoreAsync(sender, e));
        }

        private async Task RestoreBackup_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SystemBackupItem backup)
            {
                await RestoreSystemBackupAsync(backup);
            }
        }

        private async Task RestoreSystemBackupAsync(SystemBackupItem backup)
        {
            if (!OperatingSystem.IsWindows())
            {
                MessageBox.Show("System restore is only available on Windows.", "System Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var typeLabel = backup.Type switch
                {
                    "system_programs" => "programs",
                    "system_environment" => "environment variables",
                    "system_settings" => "Windows settings",
                    "system_devtools" => "developer tools settings",
                    _ => "system data"
                };
                
                var result = MessageBox.Show(
                    $"Restore {typeLabel} from backup?\n\nCreated: {backup.TimestampLabel}\n\nNote: This will download the backup and provide options for restoration.",
                    "Confirm Restore",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    if (_state == null)
                    {
                        MessageBox.Show("System not initialized properly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var passwordProvider = App.GlobalPasswordProvider ?? new Services.GuiPasswordProvider();
                    using var storage = await CreateStorageForBackupAsync(backup);
                    var progressWindow = new Windows.RestoreProgressWindow(storage, _configManager, _state, backup.Type, backup.Path, passwordProvider);
                    progressWindow.Owner = Window.GetWindow(this);
                    progressWindow.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"System restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DeleteBackup_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Delete backup", () => DeleteBackup_ClickCoreAsync(sender, e));
        }

        private async Task DeleteBackup_ClickCoreAsync(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SystemBackupItem backup)
            {
                await DeleteSystemBackupAsync(backup);
            }
        }

        private async Task DeleteSystemBackupAsync(SystemBackupItem backup)
        {
            var typeLabel = backup.Type switch
            {
                "system_programs" => "programs",
                "system_environment" => "environment variables",
                "system_settings" => "Windows settings",
                "system_devtools" => "developer tools settings",
                _ => "system"
            };
            
            var result = MessageBox.Show(
                $"Delete this {typeLabel} backup?\n\nCreated: {backup.TimestampLabel}\n\nThis action cannot be undone.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    {
                        // Mutation + delete leases: serialises with backups, and waits for a restore that may be
                        // reading this archive. The state is reloaded first if the CLI changed it.
                        await using var mutation = await RepositoryCoordinator.Instance.AcquireMutationAsync(
                            "Deleting a system backup", default, _state);
                        await using var deleteLease = await RepositoryCoordinator.Instance.AcquireDeleteAsync(
                            "Deleting a system backup");

                        using var storage = await CreateStorageForBackupAsync(backup);

                        // Already gone (manual cleanup, retention)? Then there is nothing to delete, and the stale
                        // history entry below must still be removed or it could never be cleared from the UI.
                        if (await storage.ExistsAsync(backup.Path))
                        {
                            await storage.DeleteAsync(backup.Path);
                        }
                        else
                        {
                            _logger.Log($"System backup '{backup.Path}' was already missing from storage; removing it from the history.", LogLevel.Warning);
                        }

                        // If encrypted, also delete the metadata file
                        if (backup.Path.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
                        {
                            var metadataPath = backup.Path + ".meta";
                            try
                            {
                                if (await storage.ExistsAsync(metadataPath))
                                {
                                    await storage.DeleteAsync(metadataPath);
                                }
                            }
                            catch (Exception metaEx)
                            {
                                _logger.Log($"Warning: Failed to delete metadata file: {metaEx.Message}", LogLevel.Warning);
                            }
                        }

                        if (_state != null)
                        {
                            // Locked API rather than mutating BackupHistory directly: this also
                            // prunes the verification-rotation bookkeeping.
                            _state.RemoveBackupsFromGroup(backup.Type, [backup.Path]);
                            await _state.SaveStateAsync();
                        }
                    }

                    _allBackups.Remove(backup);
                    _systemBackups.Remove(backup);
                    UpdateStats();
                    UpdateStatistics();
                    UpdateEmptyState();

                    MessageBox.Show("Backup deleted successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Delete failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is SystemBackupItem backup)
            {
                var typeLabel = backup.TypeLabel;
                var description = backup.Type switch
                {
                    "system_programs" => "a list of installed programs with Winget IDs and restore scripts.",
                    "system_environment" => "environment variables that can be restored to your system.",
                    "system_settings" => "Windows registry settings including personalization, File Explorer, taskbar, regional settings, and more.",
                    "system_devtools" => "Windows Terminal, PowerShell profile, VS Code, git (and optionally SSH config, never keys) settings plus the VS Code extension list.",
                    _ => "system backup data."
                };

                var details = $"Type: {typeLabel}\n\n" +
                             $"Created: {backup.TimestampLabel}\n\n" +
                             $"Storage: {backup.StorageType}\n\n" +
                             $"Path: {backup.DisplayPath}\n\n" +
                             $"Description: This backup contains {description}";

                MessageBox.Show(details, "Backup Details", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OpenBackupFolder()
        {
            try
            {
                var localConfig = _configManager.StorageSources.FirstOrDefault(s => s.Key == "local").Value;
                if (localConfig == null || string.IsNullOrEmpty(localConfig.Path))
                {
                    MessageBox.Show("Local storage not configured.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // System backups are written under <local storage>\system_backups\{programs,environment,settings}.
                var systemBackupPath = Path.Combine(ReStore.Core.src.utils.ConfigManager.ExpandConfiguredPath(localConfig.Path), "system_backups");

                if (!Directory.Exists(systemBackupPath))
                {
                    MessageBox.Show(
                        $"There are no system backups in local storage yet.\n\n{systemBackupPath}",
                        "Information",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                // ArgumentList quotes the path itself, so folders containing spaces open correctly.
                var startInfo = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
                startInfo.ArgumentList.Add(systemBackupPath);
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open backup folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
