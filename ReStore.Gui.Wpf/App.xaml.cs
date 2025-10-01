using System;
using System.Diagnostics;
using System.Windows;
using ReStore.Gui.Wpf.Views;
using ReStore.Gui.Wpf.Views.Pages;
using Wpf.Ui.Appearance;
using ReStore.Gui.Wpf.Services;
using ReStore.Gui.Wpf.Interop;

namespace ReStore.Gui.Wpf
{
    public partial class App : Application
    {
        private SystemTrayManager? _trayManager;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                Trace.WriteLine($"UnhandledException: {ex}");
                MessageBox.Show(ex?.ToString() ?? "Unknown error", "ReStore GUI Error");
            };
            DispatcherUnhandledException += (_, args) =>
            {
                Trace.WriteLine($"DispatcherUnhandledException: {args.Exception}");
                MessageBox.Show(args.Exception.ToString(), "ReStore GUI Error");
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
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (MainWindow is MainWindow mw)
            {
                mw.TrayManager?.Dispose();
            }
            base.OnExit(e);
        }
    }
}
