using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ReStore.src.backup;
using ReStore.src.core;
using ReStore.src.storage;
using ReStore.src.utils;

namespace ReStore.Gui.Wpf.Views.Windows
{
    public partial class RestoreProgressWindow : Window, ILogger
    {
        private readonly StringBuilder _logBuffer = new();
        private readonly IStorage _storage;
        private readonly SystemState _state;
        private readonly string _backupType;
        private readonly string _backupPath;
        private string? _scriptsFolder;
        private bool _isComplete = false;

        public RestoreProgressWindow(IStorage storage, SystemState state, string backupType, string backupPath)
        {
            InitializeComponent();
            _storage = storage;
            _state = state;
            _backupType = backupType;
            _backupPath = backupPath;

            Loaded += async (_, __) => await StartRestoreAsync();
        }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            var logLine = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            
            Dispatcher.Invoke(() =>
            {
                _logBuffer.AppendLine(logLine);
                LogBox.Text = _logBuffer.ToString();
                LogBox.ScrollToEnd();
            });
        }

        private async Task StartRestoreAsync()
        {
            try
            {
                Log("Starting system restore...", LogLevel.Info);
                DetailText.Text = "Downloading backup archive...";

                // Download and extract
                var tempDir = Path.Combine(Path.GetTempPath(), "ReStore_SystemRestore", DateTime.Now.ToString("yyyyMMddHHmmss"));
                Directory.CreateDirectory(tempDir);

                Log($"Created temporary directory: {tempDir}", LogLevel.Debug);
                
                var zipPath = Path.Combine(tempDir, "backup.zip");
                await _storage.DownloadAsync(_backupPath, zipPath);
                
                Log("Download complete. Extracting archive...", LogLevel.Info);
                DetailText.Text = "Extracting backup files...";

                var extractDir = Path.Combine(tempDir, "extracted");
                var compressionUtil = new CompressionUtil();
                await compressionUtil.DecompressAsync(zipPath, extractDir);

                _scriptsFolder = extractDir;
                Log($"Extraction complete: {extractDir}", LogLevel.Info);

                // Process based on type
                if (_backupType == "system_programs")
                {
                    await RestoreProgramsAsync(extractDir);
                }
                else if (_backupType == "system_environment")
                {
                    await RestoreEnvironmentAsync(extractDir);
                }

                // Complete
                _isComplete = true;
                StatusText.Text = "Restore Complete!";
                DetailText.Text = "System restore finished successfully.";
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
                CloseButton.IsEnabled = true;
                OpenScriptsBtn.Visibility = Visibility.Visible;

                Log("Restore completed successfully!", LogLevel.Info);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Restore Failed";
                DetailText.Text = ex.Message;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Foreground = System.Windows.Media.Brushes.Red;
                CloseButton.IsEnabled = true;
                
                Log($"Restore failed: {ex.Message}", LogLevel.Error);
                MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RestoreProgramsAsync(string extractDir)
        {
            StatusText.Text = "Restoring Programs";
            DetailText.Text = "Processing program list...";

            var jsonPath = Path.Combine(extractDir, "installed_programs.json");
            if (!File.Exists(jsonPath))
            {
                Log("Programs JSON not found in backup", LogLevel.Warning);
                TotalCountText.Text = "N/A";
                DetailText.Text = "Program list not found. Scripts are available for manual execution.";
                return;
            }

            Log("Loading program list from backup...", LogLevel.Info);

            // Check if we can use ProgramRestoreManager
            if (OperatingSystem.IsWindows())
            {
                var restoreManager = new ProgramRestoreManager(this);
                
                // Do a dry run first to see what we're dealing with
                DetailText.Text = "Analyzing programs to restore...";
                var dryRunResult = await restoreManager.RestoreProgramsFromJsonAsync(jsonPath, wingetOnly: false, dryRun: true);
                
                TotalCountText.Text = (dryRunResult.WingetPrograms + dryRunResult.ManualPrograms).ToString();
                
                Log($"Found {dryRunResult.WingetPrograms} winget programs and {dryRunResult.ManualPrograms} manual programs", LogLevel.Info);

                // Ask user if they want to proceed with winget installation
                var result = MessageBox.Show(
                    $"Found {dryRunResult.WingetPrograms} programs available via Winget and {dryRunResult.ManualPrograms} that require manual installation.\n\n" +
                    "Would you like to automatically install the Winget programs now?\n\n" +
                    "(This may take several minutes and requires an internet connection)",
                    "Install Programs",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    DetailText.Text = "Installing programs via Winget...";
                    ProgressBar.IsIndeterminate = true;

                    var installResult = await restoreManager.RestoreProgramsFromJsonAsync(jsonPath, wingetOnly: false, dryRun: false);
                    
                    SuccessCountText.Text = installResult.SuccessfulInstalls.ToString();
                    FailedCountText.Text = installResult.FailedInstalls.ToString();

                    Log($"Installation complete: {installResult.SuccessfulInstalls} succeeded, {installResult.FailedInstalls} failed", LogLevel.Info);

                    if (installResult.FailedInstalls > 0)
                    {
                        Log($"Some programs failed to install. Check the scripts folder for manual installation.", LogLevel.Warning);
                    }
                }
                else
                {
                    Log("User skipped automatic installation", LogLevel.Info);
                    DetailText.Text = "Skipped automatic installation. Scripts are available.";
                }
            }
            else
            {
                Log("Program restoration is only available on Windows", LogLevel.Warning);
                DetailText.Text = "Program restoration requires Windows.";
            }

            Log("Restore scripts are available in the extracted folder", LogLevel.Info);
        }

        private async Task RestoreEnvironmentAsync(string extractDir)
        {
            StatusText.Text = "Restoring Environment Variables";
            DetailText.Text = "Processing environment variables...";

            var jsonPath = Path.Combine(extractDir, "environment_variables.json");
            if (!File.Exists(jsonPath))
            {
                Log("Environment variables JSON not found in backup", LogLevel.Warning);
                DetailText.Text = "Environment variables not found. Scripts are available for manual execution.";
                return;
            }

            Log("Loading environment variables from backup...", LogLevel.Info);

            try
            {
                var envManager = new EnvironmentVariablesManager(this);
                
                // Read the JSON to count variables
                var json = await File.ReadAllTextAsync(jsonPath);
                var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
                int variableCount = 0;
                if (data.TryGetProperty("variables", out var variablesElement))
                {
                    variableCount = variablesElement.GetArrayLength();
                }
                
                TotalCountText.Text = variableCount.ToString();
                Log($"Found {variableCount} environment variables", LogLevel.Info);

                var result = MessageBox.Show(
                    $"Found {variableCount} environment variables to restore.\n\n" +
                    "Would you like to restore them now?\n\n" +
                    "(This requires administrator privileges and will modify system settings)",
                    "Restore Environment Variables",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    DetailText.Text = "Restoring environment variables...";
                    
                    await envManager.RestoreEnvironmentVariablesAsync(jsonPath);
                    
                    SuccessCountText.Text = variableCount.ToString();
                    Log("Environment variables restored successfully", LogLevel.Info);
                }
                else
                {
                    Log("User skipped environment variable restoration", LogLevel.Info);
                    DetailText.Text = "Skipped restoration. Scripts are available.";
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to restore environment variables: {ex.Message}", LogLevel.Error);
                FailedCountText.Text = "Error";
                throw;
            }
        }

        private void OpenScriptsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_scriptsFolder) && Directory.Exists(_scriptsFolder))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = _scriptsFolder,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_isComplete)
            {
                var result = MessageBox.Show(
                    "Restore is still in progress. Are you sure you want to close?",
                    "Confirm Close",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.No)
                {
                    return;
                }
            }

            Close();
        }
    }
}
