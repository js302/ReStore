using System.Diagnostics;
using System.IO.Pipes;
using System.IO;
using System.Windows;
using ReStore.Views;
using ReStore.Views.Pages;
using ReStore.Services;
using ReStore.Interop;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;
using CoreTelemetry = ReStore.Core.src.telemetry.Telemetry;

namespace ReStore
{
    public partial class App : Application
    {
        private SystemTrayManager? _trayManager;
        private static Mutex? _instanceMutex;
        private static bool _ownsMutex;
        private static readonly string PIPE_NAME = SingleInstanceNames.PipeName(
            System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value,
            Process.GetCurrentProcess().SessionId);
        private static readonly TimeSpan PipeReadTimeout = TimeSpan.FromSeconds(5);
        private Thread? _pipeServerThread;
        private bool _isRunning = true;

        // Commands from a second launch can arrive while this instance is still starting; they wait here until
        // the main window exists instead of being dropped.
        private readonly StartupCommandQueue _commandQueue = new();
        private SystemThemeFollower? _themeFollower;
        private ReStore.Core.src.telemetry.TelemetryFlushTimer? _telemetryFlushTimer;

        private static readonly TimeSpan ExitStopTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan SessionEndStopTimeout = TimeSpan.FromSeconds(3);
        private bool _protectionShutDown;
        private bool _sessionEnding;

        /// <summary>True once Windows is logging off or shutting down; prompts that wait for the user must be skipped.</summary>
        internal static bool IsSessionEnding { get; private set; }

        public static GuiPasswordProvider? GlobalPasswordProvider { get; private set; }

        /// <summary>App-wide owner of the watcher and scheduler; null only if configuration failed to load at startup.</summary>
        public static ProtectionService? Protection { get; private set; }

        /// <summary>Registered before anything else so startup failures are logged and reported too.</summary>
        private void RegisterGlobalExceptionHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                ErrorReporter.ReportUnhandled(
                    args.IsTerminating ? "Fatal unhandled exception" : "Unhandled exception",
                    args.ExceptionObject as Exception);
            };
            DispatcherUnhandledException += (_, args) =>
            {
                ErrorReporter.ReportUnhandled("Unhandled UI exception", args.Exception);
                args.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                // Background noise rather than a user-facing failure: log and write the crash file, no dialog.
                ErrorReporter.ReportUnhandled("Unobserved task exception", args.Exception, showDialog: false);
                args.SetObserved();
            };
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            RegisterGlobalExceptionHandlers();
            CoreTelemetry.SetSurface("gui");

            _instanceMutex = new Mutex(true, SingleInstanceNames.MutexName, out bool createdNew);
            _ownsMutex = createdNew;

            var activatedByStartupTask = StartupTaskService.WasActivatedByStartupTask();

            if (!createdNew)
            {
                if (activatedByStartupTask || (e.Args.Length == 1 && StartupMode.IsStartupArgument(e.Args[0])))
                {
                    // A second login-launch must not pop the first instance's window open.
                }
                else if (e.Args.Length > 0)
                {
                    SendCommandToExistingInstance(e.Args);
                }
                else
                {
                    BringExistingInstanceToFront();
                }
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Listen before the slow startup work so a second launch during it is queued, not lost.
            _pipeServerThread = new Thread(ListenForCommands)
            {
                IsBackground = true,
                Name = "ReStore command pipe"
            };
            _pipeServerThread.Start();

            var configSetupResult = ConfigInitializer.EnsureConfigurationSetup(AppServices.Logger);
            ConfigMigrationResult? configMigrationResult = null;

            try
            {
                // Warms the shared instance so pages reuse this load rather than each
                // re-reading config.json; also surfaces migration results up front.
                var startupConfigManager = await AppServices.GetConfigManagerAsync();
                configMigrationResult = startupConfigManager.LastMigrationResult;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Config preflight load failed: {ex}");
                MessageBox.Show(
                    $"Configuration initialization encountered an issue. You may need to review your config file.\n\n{ex.Message}\n\nPath: {ConfigInitializer.GetUserConfigPath()}",
                    "ReStore Configuration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var lifecycleMessage = ConfigLifecycleNotice.Build(
                configSetupResult,
                configMigrationResult,
                ConfigInitializer.GetUserConfigPath(),
                suppressConfigCreatedNotice: await FirstRunSetupIsPendingAsync());

            GlobalPasswordProvider = new GuiPasswordProvider();

            try
            {
                Protection = new ProtectionService(
                    await AppServices.GetConfigManagerAsync(),
                    await AppServices.GetSystemStateAsync(),
                    GlobalPasswordProvider,
                    AppServices.Logger);
            }
            catch (Exception ex)
            {
                // The config failure was already reported above; the app stays usable for fixing it in Settings.
                Trace.WriteLine($"Protection service unavailable: {ex}");
            }

            var theme = ThemeSettings.Load();
            theme.Apply();
            _themeFollower = new SystemThemeFollower(Dispatcher);

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            var settings = AppSettings.Load();

            if (PackageInfo.IsPackaged)
            {
                // A verb left by an older unpackaged/MSIX build points at a path that cannot work; remove it (idempotent).
                var (cleaned, cleanError) = FileContextMenuService.UnregisterContextMenu();
                if (!cleaned)
                {
                    AppServices.Logger.Log($"Could not remove stale Explorer context menu entry: {cleanError}", LogLevel.Warning);
                }
            }

            var startup = StartupMode.Decide(PackageInfo.IsPackaged, e.Args, settings.MinimizeToTray, activatedByStartupTask);
            mainWindow.UpdateTrayManager(settings.MinimizeToTray);
            _trayManager = mainWindow.TrayManager;

            // Anonymous usage statistics (docs/telemetry.md): one event per launch, then a flush every 30 minutes and at exit.
            CoreTelemetry.Track(ReStore.Core.src.telemetry.TelemetrySchema.AppStart, new Dictionary<string, object?>
            {
                ["startMode"] = activatedByStartupTask || (e.Args.Length == 1 && StartupMode.IsStartupArgument(e.Args[0]))
                    ? "startup"
                    : startup.WindowMode == StartupWindowMode.HiddenInTray ? "tray" : "normal"
            });
            _telemetryFlushTimer = new ReStore.Core.src.telemetry.TelemetryFlushTimer();

            // A crash report from an earlier run is waiting for the user's decision.
            CrashPromptService.ShowSoon();

            if (e.Args.Length > 0 && e.Args[0] == "--share" && e.Args.Length > 1)
            {
                _ = OpenShareWindowAsync(e.Args[1], shutdownOnClose: true);
            }
            else
            {
                if (e.Args.Length > 0)
                {
                    HandleCommandLineArgs(e.Args, mainWindow);
                }

                if (startup.WindowMode == StartupWindowMode.MinimizedToTaskbar)
                {
                    mainWindow.WindowState = WindowState.Minimized;
                }

                if (startup.WindowMode != StartupWindowMode.HiddenInTray)
                {
                    mainWindow.Show();
                }

                if (!string.IsNullOrWhiteSpace(lifecycleMessage))
                {
                    void ShowLifecycleMessage() => MessageBox.Show(
                        lifecycleMessage,
                        "ReStore Configuration",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    if (startup.WindowMode == StartupWindowMode.Normal)
                    {
                        ShowLifecycleMessage();
                    }
                    else
                    {
                        // A login launch is not the moment for a modal nobody is watching for; show it the
                        // first time the user actually opens the window.
                        ShowWhenFirstOpened(mainWindow, ShowLifecycleMessage);
                    }
                }
            }

            AppUpdateManager.Instance.Start();

            // Edits of config.json made outside the app (the CLI) are picked up instead of being overwritten by the next save.
            _ = AppServices.StartWatchingConfigAsync().ContinueWith(
                t => Trace.WriteLine($"Watching config.json failed: {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);

            // Replays anything a second launch sent while we were starting, then handles later commands directly.
            _commandQueue.MarkReady(command => _ = Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is MainWindow mw)
                {
                    HandleCommand(command, mw);
                }
            }));

            if (Protection is { } protection)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await protection.RestorePauseAsync();
                        await protection.AutoStartAsync();
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Protection auto-start failed: {ex}");
                    }
                });
            }
        }

        private void ListenForCommands()
        {
            while (_isRunning)
            {
                try
                {
                    using var pipeServer = new NamedPipeServerStream(PIPE_NAME, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                    pipeServer.WaitForConnection();

                    // A client that connects and never writes must not wedge the only listener.
                    using var timeout = new CancellationTokenSource(PipeReadTimeout);
                    using var reader = new StreamReader(pipeServer);
                    var command = reader.ReadToEndAsync(timeout.Token).GetAwaiter().GetResult();

                    _commandQueue.Submit(command);
                }
                catch (OperationCanceledException)
                {
                    Trace.WriteLine("Pipe client sent no complete command before the read timeout; dropped.");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Pipe server error: {ex}");

                    // A failure that recurs immediately (name already taken, ACL rejection)
                    // would otherwise spin this thread at full speed for the app's lifetime.
                    if (_isRunning)
                    {
                        Thread.Sleep(500);
                    }
                }
            }
        }

        private void SendCommandToExistingInstance(string[] args)
        {
            string? command;
            if (args.Length > 0 && args[0] == "--share" && args.Length > 1)
            {
                command = $"--share \"{args[1]}\"";
            }
            else
            {
                command = string.Join(" ", args);
            }
            SendCommandToExistingInstance(command);
        }

        private static void SendCommandToExistingInstance(string command)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.Out);
                pipeClient.Connect(3000);

                using var writer = new StreamWriter(pipeClient);
                writer.Write(command);
                writer.Flush();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to send command to existing instance: {ex}");
            }
        }

        private static void BringExistingInstanceToFront()
        {
            SendCommandToExistingInstance("/show");
        }

        private void HandleCommand(string command, MainWindow mainWindow)
        {
            if (ToastActivationArgs.IsToastArgument(command))
            {
                // A toast click/button (protocol activation). Unknown or malformed URIs are ignored, never run.
                if (ToastActivationArgs.TryParse(command, out var activation))
                {
                    ToastActivationHandler.Handle(activation);
                }
                else
                {
                    mainWindow.Show();
                    mainWindow.Activate();
                }

                return;
            }

            if (command.StartsWith("--share "))
            {
                var path = command.Substring(8).Trim('"');
                _ = OpenShareWindowAsync(path, shutdownOnClose: false);
                return;
            }

            mainWindow.Show();
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();

            if (command == "/startWatcher")
            {
                _ = Protection?.StartAsync();
            }
            else if (command == "/stopWatcher")
            {
                _ = Protection?.StopAsync();
            }
        }

        private async Task OpenShareWindowAsync(string filePath, bool shutdownOnClose)
        {
            try
            {
                var configManager = await AppServices.GetConfigManagerAsync();
                var shareService = new ReStore.Core.src.sharing.ShareService(configManager, AppServices.Logger);

                await Dispatcher.InvokeAsync(() =>
                {
                    var shareWindow = new ReStore.Views.Windows.ShareWindow(filePath, shareService, configManager);
                    if (shutdownOnClose)
                    {
                        shareWindow.Closed += (_, _) =>
                        {
                            if (MainWindow == null || MainWindow.Visibility != Visibility.Visible)
                            {
                                Shutdown();
                            }
                        };
                    }
                    shareWindow.Show();
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to open share window: {ex}");
                MessageBox.Show($"Failed to open share window: {ex.Message}", "ReStore Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void HandleCommandLineArgs(string[] args, MainWindow mainWindow)
        {
            foreach (var arg in args)
            {
                if (StartupMode.IsStartupArgument(arg))
                {
                    continue;
                }

                HandleCommand(arg, mainWindow);
            }
        }

        private static void ShowWhenFirstOpened(Window window, Action action)
        {
            var done = false;

            void Check()
            {
                if (done || !window.IsVisible || window.WindowState == WindowState.Minimized)
                {
                    return;
                }

                done = true;
                window.IsVisibleChanged -= OnVisibleChanged;
                window.StateChanged -= OnStateChanged;
                _ = window.Dispatcher.BeginInvoke(action);
            }

            void OnVisibleChanged(object? sender, DependencyPropertyChangedEventArgs args) => Check();
            void OnStateChanged(object? sender, EventArgs args) => Check();

            window.IsVisibleChanged += OnVisibleChanged;
            window.StateChanged += OnStateChanged;
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            // Do not stop protection here: a handler can still cancel the session end, and protection
            // would then stay off. When the session really ends WPF shuts the app down, so OnExit stops
            // protection; this only records that the shorter deadline applies.
            // Set before calling base: when the session end is not cancelled the base may shut the app down
            // synchronously, and MainWindow.OnClosing must already know not to show a prompt.
            _sessionEnding = true;
            IsSessionEnding = true;
            base.OnSessionEnding(e);
            if (e.Cancel)
            {
                _sessionEnding = false;
                IsSessionEnding = false;
            }
        }

        /// <summary>
        /// Stops the watcher and scheduler (cancelling in-flight backups) and saves state. Runs on a pool
        /// thread so a worker that needs the UI thread cannot deadlock the wait; bounded by the timeout.
        /// </summary>
        private void ShutDownProtection(TimeSpan timeout)
        {
            if (_protectionShutDown || Protection is not { } protection)
            {
                return;
            }

            _protectionShutDown = true;

            try
            {
                Task.Run(() => protection.ShutdownAsync(timeout)).Wait(timeout + TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Protection shutdown failed: {ex}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _isRunning = false;
            _themeFollower?.Dispose();
            _telemetryFlushTimer?.Dispose();
            AppServices.StopWatchingConfig();

            try
            {
                // Only does anything once a collection endpoint exists; bounded so exit is never held up.
                Task.Run(() => CoreTelemetry.FlushAsync(TimeSpan.FromSeconds(2))).Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Telemetry flush at exit failed: {ex}");
            }

            ShutDownProtection(_sessionEnding ? SessionEndStopTimeout : ExitStopTimeout);
            Protection?.DisposeNotifications();

            if (MainWindow is MainWindow mw)
            {
                mw.TrayManager?.Dispose();
            }

            if (_ownsMutex)
            {
                _instanceMutex?.ReleaseMutex();
            }
            _instanceMutex?.Dispose();

            base.OnExit(e);
        }

        /// <summary>
        /// Whether the Dashboard is about to open the first-run wizard. The wizard explains setup
        /// far better than a bare "config created" alert, and stacking both means the user dismisses
        /// a dialog before seeing the one that matters.
        /// </summary>
        private static async Task<bool> FirstRunSetupIsPendingAsync()
        {
            try
            {
                var configManager = await AppServices.GetConfigManagerAsync();
                var state = await AppServices.GetSystemStateAsync();

                var localBackupDirectoryExists =
                    configManager.StorageSources.TryGetValue("local", out var localConfig)
                    && !string.IsNullOrWhiteSpace(localConfig.Path)
                    && Directory.Exists(ReStore.Core.src.utils.ConfigManager.ExpandConfiguredPath(localConfig.Path));

                return FirstRunDetector.NeedsSetup(
                    configManager.StorageSources,
                    localBackupDirectoryExists,
                    state.GetTotalBackupCount());
            }
            catch (Exception ex)
            {
                // Never let this gate startup; worst case the user sees the extra notice.
                Trace.WriteLine($"First-run check for startup notice failed: {ex}");
                return false;
            }
        }

    }
}
