using ReStore.Core.src.utils;
using System.Security.Cryptography;

namespace ReStore.Core.src.core;

public class DiffManager
{
    private const int CHUNK_SIZE = 4096;
    private const int ROLLING_WINDOW = 64;

    private record struct BlockInfo(long Position, byte[] StrongHash);

    public static async Task<byte[]> CreateDiffAsync(string originalFile, string newFile)
    {
        var fileHasher = new FileHasher();
        if (!await FileHasher.IsContentDifferentAsync(originalFile, newFile))
        {
            return [];
        }

        using var memStream = new MemoryStream();
        using var writer = new BinaryWriter(memStream);
        using var origFile = File.OpenRead(originalFile);
        using var newStream = File.OpenRead(newFile);

        var blockMap = await CalculateBlocksAsync(origFile);

        byte[] buffer = new byte[CHUNK_SIZE];
        byte[] window = new byte[ROLLING_WINDOW];

        while (true)
        {
            int bytesRead = 0;
            while (bytesRead < CHUNK_SIZE)
            {
                int read = await newStream.ReadAsync(buffer.AsMemory(bytesRead, CHUNK_SIZE - bytesRead));
                if (read == 0) break;
                bytesRead += read;
            }

            if (bytesRead == 0) break;

            bool matchFound = false;

            if (bytesRead == CHUNK_SIZE && bytesRead >= ROLLING_WINDOW)
            {
                Array.Copy(buffer, 0, window, 0, ROLLING_WINDOW);
                var weakHash = CalculateRollingHash(window);

                if (blockMap.TryGetValue(weakHash, out var blockInfos))
                {
                    var currentStrongHash = SHA256.HashData(buffer.AsSpan(0, bytesRead));

                    foreach (var blockInfo in blockInfos)
                    {
                        if (!currentStrongHash.AsSpan().SequenceEqual(blockInfo.StrongHash))
                        {
                            continue;
                        }

                        if (await VerifyBlockMatchAsync(origFile, blockInfo.Position, buffer, bytesRead))
                        {
                            writer.Write((byte)DiffOperation.Copy);
                            writer.Write(blockInfo.Position);
                            writer.Write(CHUNK_SIZE);
                            matchFound = true;
                            break;
                        }
                    }
                }
            }

            if (!matchFound)
            {
                writer.Write((byte)DiffOperation.Data);
                writer.Write(bytesRead);
                writer.Write(buffer, 0, bytesRead);
            }
        }

        return memStream.ToArray();
    }

    public static async Task ApplyDiffAsync(string originalFile, byte[] diff, string outputFile)
    {
        using var diffStream = new MemoryStream(diff);
        using var reader = new BinaryReader(diffStream);
        using var origFile = File.OpenRead(originalFile);
        using var outFile = File.Create(outputFile);

        while (diffStream.Position < diffStream.Length)
        {
            var operation = (DiffOperation)reader.ReadByte();

            switch (operation)
            {
                case DiffOperation.Copy:
                    var sourcePos = reader.ReadInt64();
                    var length = reader.ReadInt32();

                    origFile.Position = sourcePos;
                    await CopyFixedLengthAsync(origFile, outFile, length);
                    break;

                case DiffOperation.Data:
                    var dataLength = reader.ReadInt32();
                    var data = reader.ReadBytes(dataLength);
                    await outFile.WriteAsync(data);
                    break;
            }
        }
    }

    private static async Task<Dictionary<uint, List<BlockInfo>>> CalculateBlocksAsync(Stream stream)
    {
        var blocks = new Dictionary<uint, List<BlockInfo>>();
        byte[] buffer = new byte[CHUNK_SIZE];
        long position = 0;

        while (true)
        {
            int bytesRead = 0;
            while (bytesRead < CHUNK_SIZE)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(bytesRead, CHUNK_SIZE - bytesRead));
                if (read == 0) break;
                bytesRead += read;
            }

            if (bytesRead == 0) break;

            if (bytesRead >= ROLLING_WINDOW)
            {
                var weakHash = CalculateRollingHash(buffer[..ROLLING_WINDOW]);
                var strongHash = SHA256.HashData(buffer.AsSpan(0, bytesRead));

                if (!blocks.TryGetValue(weakHash, out var blockInfos))
                {
                    blockInfos = [];
                    blocks[weakHash] = blockInfos;
                }

                blockInfos.Add(new BlockInfo(position, strongHash));
            }

            position += bytesRead;
        }

        return blocks;
    }

    private static uint CalculateRollingHash(byte[] data)
    {
        uint hash = 0;
        for (int i = 0; i < data.Length; i++)
        {
            hash = (hash << 5) + hash + data[i];
        }
        return hash;
    }

    private static async Task<bool> VerifyBlockMatchAsync(Stream original, long origPos, byte[] currentBuffer, int currentLength)
    {
        long savedOrigPos = original.Position;

        try
        {
            byte[] origBuffer = new byte[CHUNK_SIZE];

            original.Position = origPos;
            int origRead = 0;
            while (origRead < currentLength)
            {
                int read = await original.ReadAsync(origBuffer.AsMemory(origRead, currentLength - origRead));
                if (read == 0) break;
                origRead += read;
            }

            if (origRead != currentLength) return false;

            return origBuffer.AsSpan(0, origRead).SequenceEqual(currentBuffer.AsSpan(0, currentLength));
        }
        finally
        {
            original.Position = savedOrigPos;
        }
    }

    private enum DiffOperation : byte
    {
        Copy = 0,
        Data = 1
    }

    private static async Task CopyFixedLengthAsync(Stream source, Stream destination, int bytesToCopy)
    {
        const int bufferSize = 81920;
        byte[] buffer = new byte[Math.Min(bufferSize, bytesToCopy)];
        int remaining = bytesToCopy;
        while (remaining > 0)
        {
            int toRead = Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, toRead));
            if (read == 0)
            {
                break;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }
}
