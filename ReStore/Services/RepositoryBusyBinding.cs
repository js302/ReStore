using System.Windows.Threading;
using ReStore.Core.src.core;

namespace ReStore.Services;

/// <summary>
/// Reports <see cref="RepositoryCoordinator"/> busy changes to a page on its dispatcher thread, so the
/// page can disable the buttons that start a mutating operation. The coordinator is a process-wide
/// singleton, so a page must dispose its binding when it unloads or the event would keep it alive.
/// </summary>
public sealed class RepositoryBusyBinding : IDisposable
{
    /// <summary>Tooltip for a button that is disabled because another operation is running.</summary>
    public const string BusyMessage = "Another operation is running…";

    private readonly Dispatcher _dispatcher;
    private readonly Action<bool, string?> _apply;
    private readonly RepositoryCoordinator _coordinator;
    private bool _disposed;

    public RepositoryBusyBinding(Dispatcher dispatcher, Action<bool, string?> apply, RepositoryCoordinator? coordinator = null)
    {
        _dispatcher = dispatcher;
        _apply = apply;
        _coordinator = coordinator ?? RepositoryCoordinator.Instance;
        _coordinator.BusyChanged += OnBusyChanged;
        Refresh();
    }

    /// <summary>Re-applies the current state, e.g. after a page rebuilt its buttons.</summary>
    public void Refresh()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                _apply(_coordinator.IsBusy, _coordinator.CurrentReason);
            }
        });
    }

    private void OnBusyChanged(object? sender, EventArgs e) => Refresh();

    public void Dispose()
    {
        _disposed = true;
        _coordinator.BusyChanged -= OnBusyChanged;
    }
}
