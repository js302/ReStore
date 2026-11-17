namespace ReStore.Core.src.storage;

/// <summary>
/// Read-only view of a seekable stream starting at its current position, so that the view's position 0 is the
/// caller's "current position". SDKs that rewind the stream they are given (the AWS S3 client does, for
/// checksumming and retries) would otherwise upload the bytes before the caller's position as well.
/// Disposing the view leaves the underlying stream open.
/// </summary>
internal sealed class OffsetReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;

    public OffsetReadStream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(inner));
        }

        _inner = inner;
        _start = inner.Position;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _inner.Length - _start;

    public override long Position
    {
        get => _inner.Position - _start;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0)
        {
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        }

        _inner.Position = _start + target;
        return target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
