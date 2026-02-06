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
        var infoA = new FileInfo(fileA);
        var infoB = new FileInfo(fileB);
        
        if (infoA.Length != infoB.Length) return true;

        using var streamA = new FileStream(fileA, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, useAsync: true);
        using var streamB = new FileStream(fileB, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, useAsync: true);

        var bufferA = new byte[BUFFER_SIZE];
        var bufferB = new byte[BUFFER_SIZE];

        while (true)
        {
            var readA = await streamA.ReadAsync(bufferA);
            var readB = await streamB.ReadAsync(bufferB);

            if (readA != readB) return true;
            if (readA == 0) return false;

            if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
            {
                return true;
            }
        }
    }
}
