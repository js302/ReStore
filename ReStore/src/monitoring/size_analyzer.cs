namespace ReStore.Monitoring;

public class SizeAnalyzer
{
    private const long DEFAULT_SIZE_THRESHOLD = 1024 * 1024 * 500; // 500MB

    public long SizeThreshold { get; set; } = DEFAULT_SIZE_THRESHOLD;

    public async Task<(long Size, bool ExceedsThreshold)> AnalyzeDirectoryAsync(string path)
    {
        long size = await CalculateDirectorySizeAsync(path);
        return (size, size > SizeThreshold);
    }

    private async Task<long> CalculateDirectorySizeAsync(string path)
    {
        var info = new DirectoryInfo(path);
        return await Task.Run(() => CalculateSize(info));
    }

    private long CalculateSize(DirectoryInfo directory)
    {
        long size = 0;

        // Add size of all files
        foreach (FileInfo file in directory.GetFiles())
        {
            size += file.Length;
        }

        // Recursively add subdirectory sizes
        foreach (DirectoryInfo dir in directory.GetDirectories())
        {
            size += CalculateSize(dir);
        }

        return size;
    }
}
