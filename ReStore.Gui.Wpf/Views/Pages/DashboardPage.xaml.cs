using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ReStore.src.utils;
using ReStore.src.core;
using ReStore.src.monitoring;
using ReStore.src.storage;
using ReStore.Gui.Wpf.Services;

namespace ReStore.Gui.Wpf.Views.Pages
{
    public partial class DashboardPage : Page, ILogger
    {
        private readonly Logger _fileLogger = new();
        private readonly ConfigManager _configManager;
        private SystemState? _state;
        private IStorage? _storage;
        private FileWatcher? _watcher;
        private readonly StringBuilder _logBuffer = new();
        private CancellationTokenSource? _cts;

        public DashboardPage()
        {
            InitializeComponent();
            _configManager = new ConfigManager(this);

            ValidateBtn.Click += (_, __) => ValidateConfig();
            StartWatcherBtn.Click += async (_, __) => await StartWatcherAsync();
            StopWatcherBtn.Click += (_, __) => StopWatcher();

            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                await _configManager.LoadAsync();
                StatusText.Text = $"Config loaded: {_configManager.GetConfigFilePath()}";
            }
            catch (Exception ex)
            {
                Log($"Failed to load config: {ex.Message}", LogLevel.Error);
            }
        }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            _fileLogger.Log(message, level);
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            Dispatcher.Invoke(() =>
            {
                _logBuffer.AppendLine(line);
                LogBox.Text = _logBuffer.ToString();
                LogBox.ScrollToEnd();
            });
        }

        private void ValidateConfig()
        {
            try
            {
                var result = _configManager.ValidateConfiguration();
                if (!result.IsValid)
                {
                    foreach (var e in result.Errors) Log($"Config Error: {e}", LogLevel.Error);
                }
                foreach (var w in result.Warnings) Log($"Config Warning: {w}", LogLevel.Warning);
                foreach (var i in result.Info) Log($"Config Info: {i}", LogLevel.Info);
                StatusText.Text = result.IsValid ? "Configuration is valid" : "Configuration has errors";
            }
            catch (Exception ex)
            {
                Log($"Validation error: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task StartWatcherAsync()
        {
            try
            {
                if (_watcher != null)
                {
                    Log("Watcher already running", LogLevel.Warning);
                    return;
                }

                var result = _configManager.ValidateConfiguration();
                if (!result.IsValid)
                {
                    Log("Cannot start watcher due to config errors", LogLevel.Error);
                    return;
                }

                _cts = new CancellationTokenSource();
                _state = new SystemState(this);
                await _state.LoadStateAsync();

                var appSettings = AppSettings.Load();
                var remote = string.IsNullOrWhiteSpace(appSettings.DefaultStorage) ? "local" : appSettings.DefaultStorage;
                _storage = await _configManager.CreateStorageAsync(remote);

                var sizeAnalyzer = new SizeAnalyzer();
                var compression = new CompressionUtil();
                _watcher = new FileWatcher(_configManager, this, _state, _storage, sizeAnalyzer, compression);
                await _watcher.StartAsync();

                StatusText.Text = "Watcher running";
                Log("File watcher started", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log($"Failed to start watcher: {ex.Message}", LogLevel.Error);
                StopWatcher();
            }
        }

        private void StopWatcher()
        {
            try
            {
                _cts?.Cancel();
                _watcher?.Dispose();
                _storage?.Dispose();
                _watcher = null;
                _storage = null;
                _cts = null;
                StatusText.Text = "Watcher stopped";
                Log("File watcher stopped", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log($"Error stopping watcher: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
