namespace ReStore.Core.src.telemetry;

/// <summary>Coarse buckets for durations, counts and sizes: telemetry never carries exact numbers.</summary>
public static class TelemetryBuckets
{
    public static readonly string[] Durations = ["<1s", "1-10s", "10-60s", "1-10m", "10-60m", ">1h"];
    public static readonly string[] Counts = ["0", "1-10", "11-100", "101-1k", "1k-10k", "10k-100k", ">100k"];
    public static readonly string[] Bytes = ["<1MB", "1-100MB", "100MB-1GB", "1-10GB", "10-100GB", ">100GB"];

    public static string Duration(TimeSpan duration)
    {
        var seconds = duration.TotalSeconds;
        return seconds switch
        {
            < 1 => Durations[0],
            < 10 => Durations[1],
            < 60 => Durations[2],
            < 600 => Durations[3],
            < 3600 => Durations[4],
            _ => Durations[5]
        };
    }

    public static string Count(long count) => count switch
    {
        <= 0 => Counts[0],
        <= 10 => Counts[1],
        <= 100 => Counts[2],
        <= 1_000 => Counts[3],
        <= 10_000 => Counts[4],
        <= 100_000 => Counts[5],
        _ => Counts[6]
    };

    public static string Size(long bytes)
    {
        const long mb = 1024L * 1024;
        const long gb = 1024L * mb;
        return bytes switch
        {
            < mb => Bytes[0],
            < 100 * mb => Bytes[1],
            < gb => Bytes[2],
            < 10 * gb => Bytes[3],
            < 100 * gb => Bytes[4],
            _ => Bytes[5]
        };
    }
}
