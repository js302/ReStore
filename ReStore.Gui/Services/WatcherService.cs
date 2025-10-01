using System;
using System.Threading.Tasks;
using ReStore.src.monitoring;

namespace ReStore.Gui.Services
{
    public class WatcherService
    {
        private static WatcherService? _instance;
        private static readonly object _lock = new object();

        private FileWatcher? _watcher;
        private Action? _onStarted;
        private Action? _onStopped;
        private Action<string>? _onError;

        public static WatcherService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new WatcherService();
                        }
                    }
                }
                return _instance;
            }
        }

        private WatcherService() { }

        public bool IsRunning => _watcher != null;

        public void SetCallbacks(Action? onStarted, Action? onStopped, Action<string>? onError)
        {
            _onStarted = onStarted;
            _onStopped = onStopped;
            _onError = onError;
        }

        public void SetWatcher(FileWatcher? watcher)
        {
            _watcher = watcher;
            if (watcher != null)
            {
                _onStarted?.Invoke();
            }
            else
            {
                _onStopped?.Invoke();
            }
        }

        public FileWatcher? GetWatcher()
        {
            return _watcher;
        }

        public void NotifyError(string message)
        {
            _onError?.Invoke(message);
        }
    }
}
