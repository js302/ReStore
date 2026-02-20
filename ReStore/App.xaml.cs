using System.Diagnostics;
using System.Threading.Tasks;
using System.IO.Pipes;
using System.IO;
using System.Windows;
using ReStore.Views;
using ReStore.Views.Pages;
using ReStore.Services;
using ReStore.Interop;

namespace ReStore
{
    public partial class App : Application
    {
        private SystemTrayManager? _trayManager;
        private static Mutex? _instanceMutex;
        private static bool _ownsMutex;
        private const string MUTEX_NAME = "ReStore_SingleInstance_Mutex";
        private const string PIPE_NAME = "ReStore_CommandPipe";
        private Thread? _pipeServerThread;
        private bool _isRunning = true;

        public static Services.GuiPasswordProvider? GlobalPasswordProvider { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _instanceMutex = new Mutex(true, MUTEX_NAME, out createdNew);
            _ownsMutex = createdNew;

            if (!createdNew)
            {
                if (e.Args.Length > 0)
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

            ReStore.Core.src.utils.ConfigInitializer.EnsureConfigurationSetup();

            // Initialize global password provider
            GlobalPasswordProvider = new Services.GuiPasswordProvider();

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                Trace.WriteLine($"UnhandledException: {ex}");
                MessageBox.Show(ex?.ToString() ?? "Unknown error", "ReStore Error");
            };
            DispatcherUnhandledException += (_, args) =>
            {
                Trace.WriteLine($"DispatcherUnhandledException: {args.Exception}");
                MessageBox.Show(args.Exception.ToString(), "ReStore Error");
                args.Handled = true;
            };

            // Load and apply persisted theme preference
            var theme = ThemeSettings.Load();
            theme.Apply();

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            var settings = AppSettings.Load();
            mainWindow.UpdateTrayManager(settings.MinimizeToTray);
            _trayManager = mainWindow.TrayManager;

            mainWindow.SourceInitialized += (_, __) =>
            {
                WindowEffects.ApplySystemBackdrop(mainWindow);
                WindowEffects.FixMaximizedBounds(mainWindow);
            };

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

                mainWindow.Show();
            }

            _pipeServerThread = new Thread(ListenForCommands);
            _pipeServerThread.IsBackground = true;
            _pipeServerThread.Start();
        }

        private void ListenForCommands()
        {
            while (_isRunning)
            {
                try
                {
                    using (var pipeServer = new NamedPipeServerStream(PIPE_NAME, PipeDirection.In, 1))
                    {
                        pipeServer.WaitForConnection();

                        using (var reader = new StreamReader(pipeServer))
                        {
                            var command = reader.ReadToEnd();

                            Dispatcher.Invoke(() =>
                            {
                                if (MainWindow is MainWindow mw)
                                {
                                    HandleCommand(command, mw);
                                }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Pipe server error: {ex}");
                }
            }
        }

        private void SendCommandToExistingInstance(string[] args)
        {
            var command = "";
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

        private void SendCommandToExistingInstance(string command)
        {
            try
            {
                using (var pipeClient = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.Out))
                {
                    pipeClient.Connect(1000);

                    using (var writer = new StreamWriter(pipeClient))
                    {
                        writer.Write(command);
                        writer.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to send command to existing instance: {ex}");
            }
        }

        private void BringExistingInstanceToFront()
        {
            SendCommandToExistingInstance("/show");
        }

        private void HandleCommand(string command, MainWindow mainWindow)
        {
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
                ExecuteWatcherAction(mainWindow, true);
            }
            else if (command == "/stopWatcher")
            {
                ExecuteWatcherAction(mainWindow, false);
            }
        }

        private async Task OpenShareWindowAsync(string filePath, bool shutdownOnClose)
        {
            try
            {
                var logger = new ReStore.Core.src.utils.Logger();
                var configManager = new ReStore.Core.src.utils.ConfigManager(logger);
                await configManager.LoadAsync();

                var shareService = new ReStore.Core.src.sharing.ShareService(configManager, logger);

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

        private static void ExecuteWatcherAction(MainWindow mainWindow, bool start)
        {
            mainWindow.Dispatcher.InvokeAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(100);
                var frame = mainWindow.FindName("ContentFrame") as System.Windows.Controls.Frame;

                if (frame?.Content is not DashboardPage)
                {
                    frame?.Navigate(new DashboardPage());
                    await System.Threading.Tasks.Task.Delay(100);
                }

                if (frame?.Content is DashboardPage dashboard)
                {
                    var btnName = start ? "StartWatcherBtn" : "StopWatcherBtn";
                    var btn = dashboard.FindName(btnName) as System.Windows.Controls.Button;
                    btn?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }
            });
        }

        private void HandleCommandLineArgs(string[] args, MainWindow mainWindow)
        {
            foreach (var arg in args)
            {
                HandleCommand(arg, mainWindow);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _isRunning = false;

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
    }
}
