namespace ReStore.Core.src.utils;

/// <summary>
/// Single source of byte-size formatting. The CLI, the dashboard, the backups list, the
/// restore confirmation and the progress window each carried their own identical copy, so a
/// change to the unit set or precision had to be made in five places to stay consistent.
/// </summary>
public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Formats a byte count using binary (1024-based) units, e.g. <c>1.5 MB</c>.
    /// Negative inputs are rendered with a leading sign rather than as a huge unsigned value.
    /// </summary>
    public static string Format(long bytes)
    {
        var isNegative = bytes < 0;

        // long.MinValue has no positive counterpart, so widen before negating.
        var magnitude = isNegative ? -(double)bytes : bytes;

        var order = 0;
        while (magnitude >= 1024 && order < Units.Length - 1)
        {
            order++;
            magnitude /= 1024;
        }

        return $"{(isNegative ? "-" : string.Empty)}{magnitude.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {Units[order]}";
    }
}
