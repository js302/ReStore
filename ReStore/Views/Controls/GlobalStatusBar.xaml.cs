using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Services;

namespace ReStore.Views.Controls
{
    /// <summary>
    /// Bottom bar of the main window: current operation with elapsed time, protection state, next scheduled run and
    /// the last backup result. Clicking it raises <see cref="Clicked"/> (the main window opens the Activity page).
    /// Repository and protection events arrive on arbitrary threads; they only mark the bar dirty on the dispatcher.
    /// </summary>
    public partial class GlobalStatusBar : UserControl
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private RepositoryBusyBinding? _busyBinding;
        private SystemState? _state;
        private string? _busyReason;
        private DateTime _busySinceUtc;
        private int _tick;
        private static readonly Dictionary<GlobalStatusLevel, SolidColorBrush> LevelBrushes = new()
        {
            [GlobalStatusLevel.Idle] = Frozen("#10B981"),
            [GlobalStatusLevel.Busy] = Frozen("#3B82F6"),
            [GlobalStatusLevel.Warning] = Frozen("#F59E0B"),
            [GlobalStatusLevel.Error] = Frozen("#EF4444")
        };

        private static SolidColorBrush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public GlobalStatusBar()
        {
            InitializeComponent();
            BarButton.Click += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
            _timer.Tick += (_, _) => OnTick();
            Render();
        }

        public event EventHandler? Clicked;

        private async void Control_Loaded(object sender, RoutedEventArgs e)
        {
            // The bar lives as long as the window; subscribe again only if it was unloaded.
            _busyBinding ??= new RepositoryBusyBinding(Dispatcher, OnBusyChanged);
            if (App.Protection != null)
            {
                App.Protection.StateChanged -= OnProtectionStateChanged;
                App.Protection.StateChanged += OnProtectionStateChanged;
            }

            _timer.Start();
            await ErrorReporter.GuardAsync("Loading the status bar", async () =>
            {
                _state ??= await AppServices.GetSystemStateAsync();
                Render();
            });
        }

        private void Control_Unloaded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _busyBinding?.Dispose();
            _busyBinding = null;
            if (App.Protection != null)
            {
                App.Protection.StateChanged -= OnProtectionStateChanged;
            }
        }

        private void OnBusyChanged(bool busy, string? reason)
        {
            if (!string.Equals(_busyReason, reason, StringComparison.Ordinal))
            {
                _busySinceUtc = DateTime.UtcNow;
            }

            _busyReason = busy ? reason ?? "Working" : null;
            Render();
        }

        private void OnProtectionStateChanged(object? sender, EventArgs e) => _ = Dispatcher.BeginInvoke(Render);

        private void OnTick()
        {
            // Elapsed time moves every second while busy; the last-backup line only changes when a run finishes.
            _tick++;
            if (_busyReason != null || _tick % 5 == 0)
            {
                Render();
            }
        }

        private void Render()
        {
            var protection = App.Protection;
            var lastRun = _state?.GetRecentBackupRuns(1).FirstOrDefault();
            var elapsed = _busyReason != null ? DateTime.UtcNow - _busySinceUtc : TimeSpan.Zero;

            var text = GlobalStatus.Build(
                _busyReason,
                elapsed,
                protection?.State ?? ProtectionState.Stopped,
                protection?.PausedUntilUtc,
                protection?.NextRunUtc,
                lastRun,
                protection?.NextRunHeldByWindow ?? false);

            OperationText.Text = text.Operation;
            ProtectionText.Text = text.Protection;
            NextRunText.Text = text.NextRun;
            LastBackupText.Text = text.LastBackup;
            OperationDot.Fill = LevelBrushes[text.Level];
        }
    }
}
