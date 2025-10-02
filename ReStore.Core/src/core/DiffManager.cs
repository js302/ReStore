using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

public class DiffManager
{
    private const int CHUNK_SIZE = 4096;
    private const int ROLLING_WINDOW = 64;

    public async Task<byte[]> CreateDiffAsync(string originalFile, string newFile)
    {
        // Quick skip if hashes match
        var fileHasher = new FileHasher();
        if (!await fileHasher.IsContentDifferentAsync(originalFile, newFile))
        {
            return [];
        }

        using var memStream = new MemoryStream();
        using var writer = new BinaryWriter(memStream);
        using var origFile = File.OpenRead(originalFile);
        using var newStream = File.OpenRead(newFile);

        // Calculate block checksums for original file
        var blockMap = await CalculateBlocksAsync(origFile);

        byte[] buffer = new byte[CHUNK_SIZE];
        byte[] window = new byte[ROLLING_WINDOW];
        int bytesRead;
        long position = 0;

        while ((bytesRead = await newStream.ReadAsync(buffer)) > 0)
        {
            bool matchFound = false;

            // Try to find matching blocks
            if (bytesRead >= ROLLING_WINDOW)
            {
                Array.Copy(buffer, 0, window, 0, ROLLING_WINDOW);
                var weakHash = CalculateRollingHash(window);

                if (blockMap.TryGetValue(weakHash, out var blockPositions))
                {
                    foreach (var blockPos in blockPositions)
                    {
                        if (await VerifyBlockMatchAsync(origFile, blockPos, newStream, position))
                        {
                            // Write a COPY instruction
                            writer.Write((byte)DiffOperation.Copy);
                            writer.Write(blockPos);
                            writer.Write(CHUNK_SIZE);

                            position += CHUNK_SIZE;
                            matchFound = true;
                            break;
                        }
                    }
                }
            }

            if (!matchFound)
            {
                // Write a DATA instruction for new/modified content
                writer.Write((byte)DiffOperation.Data);
                writer.Write(bytesRead);
                writer.Write(buffer, 0, bytesRead);
                position += bytesRead;
            }
        }

        return memStream.ToArray();
    }

    public async Task ApplyDiffAsync(string originalFile, byte[] diff, string outputFile)
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

    private async Task<Dictionary<uint, List<long>>> CalculateBlocksAsync(Stream stream)
    {
        var blocks = new Dictionary<uint, List<long>>();
        byte[] buffer = new byte[CHUNK_SIZE];
        long position = 0;

        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer);
            if (bytesRead == 0) break;

            if (bytesRead >= ROLLING_WINDOW)
            {
                var hash = CalculateRollingHash(buffer[..ROLLING_WINDOW]);

                if (!blocks.TryGetValue(hash, out var positions))
                {
                    positions = new List<long>();
                    blocks[hash] = positions;
                }

                positions.Add(position);
            }

            position += bytesRead;
        }

        return blocks;
    }

    private uint CalculateRollingHash(byte[] data)
    {
        uint hash = 0;
        for (int i = 0; i < data.Length; i++)
        {
            hash = (hash << 5) + hash + data[i];
        }
        return hash;
    }

    private async Task<bool> VerifyBlockMatchAsync(Stream original, long origPos, Stream current, long currentPos)
    {
        byte[] origBuffer = new byte[CHUNK_SIZE];
        byte[] currentBuffer = new byte[CHUNK_SIZE];

        original.Position = origPos;
        current.Position = currentPos;

        int origRead = await original.ReadAsync(origBuffer);
        int currentRead = await current.ReadAsync(currentBuffer);

        if (origRead != currentRead) return false;

        for (int i = 0; i < origRead; i++)
        {
            if (origBuffer[i] != currentBuffer[i]) return false;
        }

        return true;
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
