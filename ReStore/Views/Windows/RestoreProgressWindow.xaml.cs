using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Views.Windows
{
    public partial class RestoreProgressWindow : Window, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog = new();
        private int _logFlushScheduled;
        private System.Windows.Threading.DispatcherTimer? _flushTimer;
        private readonly IStorage _storage;
        private readonly SystemState _state;
        private readonly string _backupType;
        private readonly string _backupPath;
        private string? _scriptsFolder;
        private bool _isComplete = false;
        private readonly IPasswordProvider? _passwordProvider;
        private readonly IConfigManager _config;
        private readonly CancellationTokenSource _cancellation = new();

        public RestoreProgressWindow(IStorage storage, IConfigManager config, SystemState state, string backupType, string backupPath, IPasswordProvider? passwordProvider = null)
        {
            InitializeComponent();
            _storage = storage;
            _config = config;
            _state = state;
            _backupType = backupType;
            _backupPath = backupPath;
            _passwordProvider = passwordProvider;

            Loaded += async (_, __) => await StartRestoreAsync();
        }

        /// <summary>
        /// Thread-safe: the restore runs on a pool thread. Lines are queued and appended in batches on the UI
        /// thread (one dispatcher post per batch, not per line).
        /// </summary>
        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            _pendingLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");

            if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, DrainLog);
            }
        }

        private void DrainLog()
        {
            Volatile.Write(ref _logFlushScheduled, 0);

            var batch = new StringBuilder();
            while (_pendingLog.TryDequeue(out var line))
            {
                batch.AppendLine(line);
            }

            if (batch.Length == 0)
            {
                return;
            }

            LogBox.AppendText(batch.ToString());
            LogBox.ScrollToEnd();
        }

        private async Task StartRestoreAsync()
        {
            try
            {
                Log("Starting system restore...", LogLevel.Info);
                DetailText.Text = "Downloading backup archive...";

                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("System restore requires Windows.");
                }

                // The download/decrypt/extract/apply pipeline lives in Core; the window only shows progress and asks the questions.
                IProgress<SystemRestoreProgress> uiProgress = new Progress<SystemRestoreProgress>(ShowProgress);
                var throttledProgress = new ThrottledProgress<SystemRestoreProgress>(uiProgress.Report, TimeSpan.FromMilliseconds(100));
                _flushTimer = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromMilliseconds(100), System.Windows.Threading.DispatcherPriority.Background, (_, __) => throttledProgress.Flush(), Dispatcher);
                _flushTimer.Start();
                var manager = new SystemBackupManager(this, _config, _state, _passwordProvider);
                var options = new SystemRestoreOptions
                {
                    Confirm = ConfirmAsync,
                    // The preview (per-item checkboxes) replaces the per-component yes/no prompts.
                    SelectActions = SelectActionsAsync,
                    // Created here on the UI thread, so ShowProgress runs on it even though the restore does not.
                    Progress = throttledProgress
                };

                // Off the UI thread; nothing inside touches UI directly (Log, Progress and Confirm all marshal).
                var token = _cancellation.Token;
                SystemRestoreResult result;
                try
                {
                    result = await Task.Run(() => manager.RestoreSystemAsync(_storage, _backupType, _backupPath, options, token));
                }
                finally
                {
                    _flushTimer?.Stop();
                    await ReStore.Core.src.core.ActivityRecorder.TryPersistAsync(_state);
                }

                _scriptsFolder = result.ScriptsFolder;
                _isComplete = true;
                TotalCountText.Text = result.Total > 0 ? result.Total.ToString() : "N/A";
                SuccessCountText.Text = result.Succeeded.ToString();
                FailedCountText.Text = result.Failed.ToString();
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
                CloseButton.IsEnabled = true;
                OpenScriptsBtn.Visibility = Visibility.Visible;

                if (result.Failed > 0)
                {
                    StatusText.Text = "Restore Completed With Errors";
                    DetailText.Text = result.Messages.FirstOrDefault() ?? $"{result.Failed} item(s) failed. See the log for details.";
                    ProgressBar.Foreground = System.Windows.Media.Brushes.Orange;
                }
                else
                {
                    StatusText.Text = "Restore Complete!";
                    DetailText.Text = result.Messages.FirstOrDefault() ?? "System restore finished successfully.";
                }

                foreach (var message in result.Messages)
                {
                    Log(message, LogLevel.Info);
                }

                Log("Restore finished.", LogLevel.Info);
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Restore Cancelled";
                DetailText.Text = "The restore was cancelled.";
                ProgressBar.IsIndeterminate = false;
                CloseButton.IsEnabled = true;
                Log("Restore cancelled.", LogLevel.Info);
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

        private void ShowProgress(SystemRestoreProgress progress)
        {
            StatusText.Text = progress.Status;
            DetailText.Text = progress.Detail;
            if (progress.Total.HasValue) TotalCountText.Text = progress.Total.Value.ToString();
            if (progress.Succeeded.HasValue) SuccessCountText.Text = progress.Succeeded.Value.ToString();
            if (progress.Failed.HasValue) FailedCountText.Text = progress.Failed.Value.ToString();
        }

        private Task<IReadOnlyCollection<string>?> SelectActionsAsync(SystemRestorePlan plan)
        {
            return Dispatcher.InvokeAsync<IReadOnlyCollection<string>?>(() =>
            {
                var preview = new RestorePreviewWindow(plan) { Owner = this };
                return preview.ShowDialog() == true ? preview.SelectedIds : null;
            }).Task;
        }

        private Task<bool> ConfirmAsync(RestorePrompt prompt)
        {
            return Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    prompt.Message,
                    prompt.Title,
                    MessageBoxButton.YesNo,
                    prompt.IsWarning ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes).Task;
        }

        protected override void OnClosed(EventArgs e)
        {
            // Closing the window mid-restore cancels the pipeline (which also removes the private work directory).
            _cancellation.Cancel();
            _cancellation.Dispose();
            base.OnClosed(e);
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

                _cancellation.Cancel();
            }

            Close();
        }
    }
}
