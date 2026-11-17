namespace ReStore.Core.src.utils;

/// <summary>
/// Brings a live list (e.g. an <c>ObservableCollection</c> bound to the UI) in line with a desired one by touching
/// only the positions that differ, so unchanged rows keep their containers, focus and scroll position.
/// </summary>
public static class CollectionSync
{
    /// <summary>Returns the number of positions that were replaced, added or removed.</summary>
    public static int Apply<T>(IList<T> target, IReadOnlyList<T> desired, Func<T, T, bool> same)
    {
        var changes = 0;
        for (var i = 0; i < desired.Count; i++)
        {
            if (i >= target.Count)
            {
                target.Add(desired[i]);
                changes++;
            }
            else if (!same(target[i], desired[i]))
            {
                target[i] = desired[i];
                changes++;
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
            changes++;
        }

        return changes;
    }

    public static int Apply<T>(IList<T> target, IReadOnlyList<T> desired) where T : IEquatable<T>
        => Apply(target, desired, (a, b) => EqualityComparer<T>.Default.Equals(a, b));
}
