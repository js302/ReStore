using System.Text;
using System.Windows;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Views.Windows
{
    /// <summary>
    /// Shared progress and cancellation host for backup, restore and verify. Runs the work
    /// the caller supplies, feeding it a token the Cancel button trips, and doubles as the
    /// <see cref="ILogger"/> for the operation so its log lands in the window.
    /// </summary>
    public partial class OperationProgressWindow : Window, ILogger
    {
        private const int MaxLogBufferChars = 256 * 1024;
        private const int KeepLogBufferChars = 128 * 1024;
        private static readonly TimeSpan UiUpdateInterval = TimeSpan.FromMilliseconds(100);

        private readonly StringBuilder _logBuffer = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ILogger? _fileLogger;

        // Operations run on a pool thread, so the progress sinks are created once, here on the UI thread
        // (Progress<T> posts to the context it was constructed on), and rate-limited to ~10 Hz before posting.
        private readonly ThrottledProgress<BackupProgress> _backupProgress;
        private readonly ThrottledProgress<RestoreProgress> _restoreProgress;
        private readonly ThrottledProgress<VerificationProgress> _verificationProgress;
        private readonly ThrottledProgress<string> _statusProgress;
        private readonly System.Windows.Threading.DispatcherTimer _flushTimer;

        private bool _isComplete;
        private bool _finished;

        public OperationProgressWindow(string title, ILogger? fileLogger = null)
        {
            InitializeComponent();

            _fileLogger = fileLogger;

            Title = title;
            TitleText.Text = title;
            StatusText.Text = title;

            _backupProgress = Throttle(new Progress<BackupProgress>(report =>
            {
                if (_finished)
                {
                    return;
                }

                PhaseText.Text = report.Phase.ToString();
                DetailText.Text = string.IsNullOrWhiteSpace(report.CurrentFile) ? "Working..." : report.CurrentFile;
                FileCountText.Text = $"{report.FilesDone} / {report.FilesTotal}";
                BytesText.Text = ByteFormatter.Format(report.BytesDone);
                ApplyFraction(report.FilesTotal > 0 || report.BytesTotal > 0, report.Fraction);
            }));
            _restoreProgress = Throttle(new Progress<RestoreProgress>(report =>
            {
                if (_finished)
                {
                    return;
                }

                PhaseText.Text = report.Phase.ToString();
                DetailText.Text = string.IsNullOrWhiteSpace(report.CurrentFile) ? "Working..." : report.CurrentFile;
                FileCountText.Text = $"{report.FilesDone} / {report.FilesTotal}";
                BytesText.Text = ByteFormatter.Format(report.BytesDone);
                ApplyFraction(report.FilesTotal > 0 || report.BytesTotal > 0, report.Fraction);
            }));
            _verificationProgress = Throttle(new Progress<VerificationProgress>(report =>
            {
                if (_finished)
                {
                    return;
                }

                PhaseText.Text = report.Phase;
                DetailText.Text = string.IsNullOrWhiteSpace(report.CurrentItem) ? "Working..." : report.CurrentItem;
                FileCountText.Text = $"{report.ItemsDone} / {report.ItemsTotal}";
                ApplyFraction(report.ItemsTotal > 0, report.Fraction);
            }));
            _statusProgress = Throttle(new Progress<string>(message =>
            {
                if (_finished)
                {
                    return;
                }

                PhaseText.Text = "Working";
                DetailText.Text = message;
                ProgressBar.IsIndeterminate = true;
            }));

            // Delivers each throttle's trailing report and drains the batched log, both on the UI thread.
            _flushTimer = new System.Windows.Threading.DispatcherTimer(
                UiUpdateInterval, System.Windows.Threading.DispatcherPriority.Background, (_, __) => FlushUi(), Dispatcher);
            _flushTimer.Start();
        }

        private static ThrottledProgress<T> Throttle<T>(IProgress<T> inner) => new(inner.Report, UiUpdateInterval);

        /// <summary>True when the operation finished without throwing.</summary>
        public bool Succeeded { get; private set; }

        public bool WasCancelled { get; private set; }

        public Exception? Failure { get; private set; }

        /// <summary>
        /// Shows the window modally and runs <paramref name="operation"/>. Returns true only
        /// on success; cancellation and failure both return false, with detail on
        /// <see cref="WasCancelled"/> and <see cref="Failure"/>.
        /// </summary>
        public bool RunModal(Window? owner, Func<CancellationToken, Task> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);

            if (owner != null)
            {
                Owner = owner;
            }

            Loaded += async (_, __) => await ExecuteAsync(operation);
            ShowDialog();

            return Succeeded;
        }

        public IProgress<BackupProgress> BackupProgress => _backupProgress;

        public IProgress<RestoreProgress> RestoreProgress => _restoreProgress;

        public IProgress<VerificationProgress> VerificationProgress => _verificationProgress;

        /// <summary>Free-text status line for operations without file/byte counters (scan, repair).</summary>
        public IProgress<string> StatusProgress => _statusProgress;

        /// <summary>
        /// Thread-safe: operations log from pool threads. Lines are queued and appended in one batch per UI tick
        /// rather than one dispatcher round-trip (and one TextBox append) per line.
        /// </summary>
        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            _fileLogger?.Log(message, level);

            _pendingLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        }

        private void FlushUi()
        {
            _backupProgress.Flush();
            _restoreProgress.Flush();
            _verificationProgress.Flush();
            _statusProgress.Flush();
            DrainLog();
        }

        private void DrainLog()
        {
            if (_pendingLog.IsEmpty)
            {
                return;
            }

            var batch = new StringBuilder();
            while (_pendingLog.TryDequeue(out var line))
            {
                batch.AppendLine(line);
            }

            // Append rather than reassigning Text, and keep only a bounded tail: a large
            // backup logs per chunk.
            _logBuffer.Append(batch);
            if (_logBuffer.Length > MaxLogBufferChars)
            {
                TrimLogBufferIfNeeded();
            }
            else
            {
                LogBox.AppendText(batch.ToString());
            }

            LogBox.ScrollToEnd();
        }

        private void TrimLogBufferIfNeeded()
        {
            if (_logBuffer.Length <= MaxLogBufferChars)
            {
                return;
            }

            var retained = _logBuffer.ToString(_logBuffer.Length - KeepLogBufferChars, KeepLogBufferChars);
            var firstLineBreak = retained.IndexOf('\n');
            if (firstLineBreak >= 0 && firstLineBreak + 1 < retained.Length)
            {
                retained = retained[(firstLineBreak + 1)..];
            }

            _logBuffer.Clear();
            _logBuffer.Append(retained);
            LogBox.Text = retained;
        }

        private async Task ExecuteAsync(Func<CancellationToken, Task> operation)
        {
            try
            {
                var token = _cancellation.Token;
                // Off the UI thread: Core awaits no longer capture the dispatcher, and synchronous stretches
                // (hashing, chunking, compression) must not freeze the window.
                await Task.Run(() => operation(token));

                Succeeded = true;
                StatusText.Text = "Completed";
                DetailText.Text = "Finished successfully.";
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                StatusText.Text = "Cancelled";
                DetailText.Text = "The operation was cancelled.";
                ProgressBar.IsIndeterminate = false;
                Log("Operation cancelled by user.", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                Failure = ex;
                StatusText.Text = "Failed";
                DetailText.Text = ex.Message;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Foreground = System.Windows.Media.Brushes.Red;
                Log($"Operation failed: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                // Stop applying progress (reports still queued on the dispatcher must not overwrite the final
                // status), then show whatever the operation logged last.
                _flushTimer.Stop();
                _finished = true;
                DrainLog();
                _isComplete = true;
                CancelButton.IsEnabled = false;
                CloseButton.IsEnabled = true;
            }
        }

        private void ApplyFraction(bool hasTotals, double fraction)
        {
            if (!hasTotals)
            {
                ProgressBar.IsIndeterminate = true;
                return;
            }

            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = Math.Clamp(fraction * 100, 0, 100);
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isComplete)
            {
                return;
            }

            CancelButton.IsEnabled = false;
            StatusText.Text = "Cancelling...";
            DetailText.Text = "Finishing the current file before stopping.";

            _cancellation.Cancel();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Closing the window mid-operation must cancel rather than orphan the work.
            if (!_isComplete)
            {
                e.Cancel = true;
                _cancellation.Cancel();
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _cancellation.Dispose();
            base.OnClosed(e);
        }
    }
}
