using System.Collections.Concurrent;

namespace ReStore.E2E;

internal static class Each
{
    /// <summary>Runs <paramref name="check"/> for every item on a few threads and fails once with every problem found.</summary>
    public static void InParallel<T>(IEnumerable<T> items, Action<T> check, int degree = 4)
    {
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = degree }, item =>
        {
            try
            {
                check(item);
            }
            catch (Exception ex)
            {
                failures.Add($"[{item}] {ex.Message}");
            }
        });

        Assert.True(failures.IsEmpty, string.Join("\n\n", failures.OrderBy(f => f, StringComparer.Ordinal)));
    }
}
