namespace ReStore.Core.src.monitoring;

public enum NotificationSeverity { Info, Warning, Error }

/// <summary>Where clicking a notification should take the user.</summary>
public enum NotificationTarget { None, Dashboard, Backups, Settings, Activity }

public sealed record PendingNotification(
    string Title,
    string Message,
    NotificationSeverity Severity,
    NotificationTarget Target,
    DateTime QueuedUtc,
    IReadOnlyList<ToastAction>? Actions = null);

/// <summary>
/// Notifications held back while Windows suppresses them (Focus Assist, fullscreen), flushed once it stops.
/// Bounded, de-duplicated and age-limited so a long suppression cannot produce a flood or stale news.
/// Not thread-safe: used from the UI thread.
/// </summary>
public sealed class NotificationQueue(int capacity = 20, TimeSpan? maxAge = null)
{
    private readonly List<PendingNotification> _items = [];
    private readonly TimeSpan _maxAge = maxAge ?? TimeSpan.FromHours(24);

    public int Count => _items.Count;

    /// <summary>Adds the notification unless an identical one is already waiting; drops the oldest beyond capacity.</summary>
    public void Enqueue(PendingNotification notification)
    {
        if (_items.Any(i => i.Title == notification.Title && i.Message == notification.Message))
        {
            return;
        }

        _items.Add(notification);
        if (_items.Count > capacity)
        {
            _items.RemoveRange(0, _items.Count - capacity);
        }
    }

    /// <summary>Removes and returns everything still fresh at <paramref name="nowUtc"/> (oldest first).</summary>
    public List<PendingNotification> Drain(DateTime nowUtc)
    {
        var fresh = _items.Where(i => nowUtc - i.QueuedUtc <= _maxAge).ToList();
        _items.Clear();
        return fresh;
    }

    /// <summary>One notification for a drained batch: itself when single, otherwise a summary of the batch.</summary>
    public static PendingNotification? Coalesce(IReadOnlyList<PendingNotification> batch, DateTime nowUtc)
    {
        if (batch.Count == 0)
        {
            return null;
        }

        if (batch.Count == 1)
        {
            return batch[0];
        }

        var worst = batch.OrderByDescending(n => n.Severity).First();
        var titles = string.Join("; ", batch.Select(n => n.Title).Distinct());
        return new PendingNotification(
            $"ReStore: {batch.Count} notifications while you were away",
            titles,
            worst.Severity,
            worst.Target,
            nowUtc);
    }
}

/// <summary>Allows an event per key at most once per period (e.g. the staleness alert, once a day per group).</summary>
public sealed class PeriodicNotificationGate(TimeSpan? period = null)
{
    private readonly TimeSpan _period = period ?? TimeSpan.FromDays(1);
    private readonly Dictionary<string, DateTime> _last = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAcquire(string key, DateTime nowUtc)
    {
        if (_last.TryGetValue(key, out var last) && nowUtc - last < _period)
        {
            return false;
        }

        _last[key] = nowUtc;
        return true;
    }
}
