using System.Security.Cryptography;

namespace ReStore.Core.src.utils;

public class FileHasher
{
    private const int BUFFER_SIZE = 81920;

    public async Task<string> ComputeHashAsync(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = await sha256.ComputeHashAsync(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public async Task<Dictionary<string, string>> ComputeDirectoryHashesAsync(string directory)
    {
        var hashes = new Dictionary<string, string>();
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            hashes[file] = await ComputeHashAsync(file);
        }

        return hashes;
    }

    public async Task<bool> IsContentDifferentAsync(string fileA, string fileB)
    {
        if(new FileInfo(fileA).Length != new FileInfo(fileB).Length) return true;

        var hashA = await ComputeHashAsync(fileA);
        var hashB = await ComputeHashAsync(fileB);
        return !hashA.Equals(hashB, StringComparison.OrdinalIgnoreCase);
    }
}
