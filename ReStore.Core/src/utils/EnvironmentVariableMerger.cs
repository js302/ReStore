namespace ReStore.Core.src.utils;

/// <summary>
/// Merge rules for <c>;</c>-separated environment variables (PATH, PATHEXT, PSModulePath, ...).
/// Restoring such a variable must never drop entries the machine already has.
/// </summary>
public static class EnvironmentVariableMerger
{
    private static readonly HashSet<string> KnownListVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "PSModulePath"
    };

    public static bool IsListVariable(string name, string? backedUpValue)
    {
        return KnownListVariables.Contains(name) || (backedUpValue?.Contains(';') ?? false);
    }

    /// <summary>
    /// Existing entries first (order kept), then backed-up entries that are not already present.
    /// Entries compare case-insensitively with trailing path separators ignored; empty entries are dropped.
    /// </summary>
    public static string Merge(string? existing, string? backedUp)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var source in new[] { existing, backedUp })
        {
            if (string.IsNullOrEmpty(source))
            {
                continue;
            }

            foreach (var raw in source.Split(';'))
            {
                var entry = raw.Trim();
                if (entry.Length == 0)
                {
                    continue;
                }

                if (seen.Add(NormalizeEntry(entry)))
                {
                    merged.Add(entry);
                }
            }
        }

        return string.Join(';', merged);
    }

    private static string NormalizeEntry(string entry)
    {
        var trimmed = entry.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? entry : trimmed;
    }
}
