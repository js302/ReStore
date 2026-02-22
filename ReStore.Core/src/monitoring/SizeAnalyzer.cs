namespace ReStore.Core.src.monitoring;

public class SizeAnalyzer
{
    private const long DEFAULT_SIZE_THRESHOLD = 1024 * 1024 * 500; // 500MB

    public long SizeThreshold { get; set; } = DEFAULT_SIZE_THRESHOLD;

    public virtual async Task<(long Size, bool ExceedsThreshold)> AnalyzeDirectoryAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be null or empty", nameof(path));
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        long size = await CalculateDirectorySizeAsync(path);
        return (size, size > SizeThreshold);
    }

    private static async Task<long> CalculateDirectorySizeAsync(string path)
    {
        var info = new DirectoryInfo(path);
        return await Task.Run(() => CalculateSize(info));
    }

    private static long CalculateSize(DirectoryInfo directory)
    {
        long size = 0;
        var directories = new Stack<DirectoryInfo>();
        directories.Push(directory);

        while (directories.Count > 0)
        {
            var current = directories.Pop();

            try
            {
                foreach (var file in current.EnumerateFiles())
                {
                    try
                    {
                        size += file.Length;
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (FileNotFoundException) { }
                }

                foreach (var subDir in current.EnumerateDirectories())
                {
                    directories.Push(subDir);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we can't access
            }
            catch (DirectoryNotFoundException)
            {
                // Skip directories that no longer exist
            }
        }

        return size;
    }
}
