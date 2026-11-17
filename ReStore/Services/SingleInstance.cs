using System.Text;

namespace ReStore.Services
{
    /// <summary>Names for the single-instance machinery, scoped so two users on one PC never share a pipe.</summary>
    public static class SingleInstanceNames
    {
        public const string MutexName = "ReStore_SingleInstance_Mutex";

        /// <summary>
        /// Named pipes live in one machine-wide namespace (unlike the session-local mutex), so the name carries the
        /// user and the logon session; a second user's instance can neither collide with nor be addressed by this one.
        /// </summary>
        public static string PipeName(string? userId, int sessionId)
        {
            var sb = new StringBuilder("ReStore_CommandPipe_");
            foreach (var c in userId ?? string.Empty)
            {
                sb.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_');
            }

            return sb.Append('_').Append(sessionId).ToString();
        }
    }

    /// <summary>
    /// Holds commands that arrive from a second launch while this instance is still starting up, and replays them
    /// in order once the main window exists. After <see cref="MarkReady"/> commands go straight to the handler.
    /// The handler runs under a lock, so it must only post work (e.g. to the dispatcher), never block.
    /// </summary>
    public sealed class StartupCommandQueue
    {
        private const int MaxPending = 64;

        private readonly object _gate = new();
        private readonly Queue<string> _pending = new();
        private Action<string>? _handler;

        public int PendingCount
        {
            get { lock (_gate) { return _pending.Count; } }
        }

        public void Submit(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            lock (_gate)
            {
                if (_handler != null)
                {
                    _handler(command);
                    return;
                }

                if (_pending.Count < MaxPending)
                {
                    _pending.Enqueue(command);
                }
            }
        }

        public void MarkReady(Action<string> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            lock (_gate)
            {
                _handler = handler;
                while (_pending.Count > 0)
                {
                    handler(_pending.Dequeue());
                }
            }
        }
    }
}
