using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.backup;

/// <summary>
/// Hands items to up to <c>concurrency</c> workers through a small bounded queue, so the producer (chunking) runs ahead of
/// the uploads by a bounded amount only. The first worker failure cancels the other workers and is rethrown to the
/// producer on its next <see cref="EnqueueAsync"/> or at <see cref="CompleteAsync"/>; an external cancellation surfaces as
/// <see cref="OperationCanceledException"/> the same way.
/// </summary>
internal sealed class ParallelWorkQueue<T> : IAsyncDisposable
{
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task[] _workers;
    private Exception? _error;

    public ParallelWorkQueue(int concurrency, Func<T, CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        concurrency = Math.Max(1, concurrency);
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(concurrency * 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        });
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var token = _cts.Token;
        _workers = new Task[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            _workers[i] = Task.Run(() => RunWorkerAsync(work, token), CancellationToken.None);
        }
    }

    /// <summary>Queues an item, waiting while the queue is full. Throws the first worker failure, if any.</summary>
    public async ValueTask EnqueueAsync(T item)
    {
        ThrowIfFailed();
        try
        {
            await _channel.Writer.WriteAsync(item, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _error) != null)
        {
            ThrowIfFailed();
        }
    }

    /// <summary>Waits until every queued item has been processed. Throws the first failure.</summary>
    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        ThrowIfFailed();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task RunWorkerAsync(Func<T, CancellationToken, Task> work, CancellationToken token)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await work(item, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // The first failure wins; the others are the cancellation it triggers.
            Interlocked.CompareExchange(ref _error, ex, null);
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void ThrowIfFailed()
    {
        var error = Volatile.Read(ref _error);
        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}

/// <summary>
/// Answers "is this chunk already stored with the expected size?" for one backup. Small backups ask the provider per chunk;
/// once more than <c>threshold</c> chunks have been asked about and the provider supports listing, the chunk directory is
/// listed once (paged) and every later answer comes from that listing, with the size compared (H9). A listing failure falls
/// back to per-chunk existence checks. Safe to call concurrently.
/// </summary>
internal sealed class ChunkExistenceIndex(IStorage storage, string listPrefix, int threshold, ILogger logger)
{
    /// <summary>Candidate chunks above which one listing replaces the per-chunk existence calls.</summary>
    public const int DefaultListingThreshold = 64;

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Dictionary<string, long>? _listed;
    private int _lookups;
    private volatile bool _listingFailed;

    /// <summary>True once the listing has been loaded (for tests and diagnostics).</summary>
    public bool IsListingLoaded => Volatile.Read(ref _listed) != null;

    public async Task<bool> ExistsAsync(string chunkPath, long expectedStoredSize, CancellationToken cancellationToken)
    {
        var listed = Volatile.Read(ref _listed);
        if (listed == null
            && storage.SupportsListing
            && !_listingFailed
            && Interlocked.Increment(ref _lookups) > threshold)
        {
            listed = await LoadListingAsync(cancellationToken).ConfigureAwait(false);
        }

        if (listed == null)
        {
            return await storage.ExistsAsync(chunkPath, cancellationToken).ConfigureAwait(false);
        }

        if (!listed.TryGetValue(chunkPath, out var size))
        {
            return false;
        }

        if (size != expectedStoredSize)
        {
            logger.Log($"Stored chunk '{chunkPath}' is {size} bytes but {expectedStoredSize} were expected; re-uploading it.", LogLevel.Warning);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The stored size of the object at <paramref name="chunkPath"/>, or null when it is absent. Same listing/per-chunk
    /// strategy as <see cref="ExistsAsync"/>; the per-chunk path asks the provider for the size only once the object exists.
    /// The size is how an already-stored chunk's codec is recognised (raw and compressed forms differ in size).
    /// </summary>
    public async Task<long?> GetStoredSizeAsync(string chunkPath, CancellationToken cancellationToken)
    {
        var listed = Volatile.Read(ref _listed);
        if (listed == null
            && storage.SupportsListing
            && !_listingFailed
            && Interlocked.Increment(ref _lookups) > threshold)
        {
            listed = await LoadListingAsync(cancellationToken).ConfigureAwait(false);
        }

        if (listed != null)
        {
            return listed.TryGetValue(chunkPath, out var size) ? size : null;
        }

        if (!await storage.ExistsAsync(chunkPath, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await storage.GetSizeAsync(chunkPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, long>?> LoadListingAsync(CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_listed != null)
            {
                return _listed;
            }

            if (_listingFailed)
            {
                return null;
            }

            var entries = new Dictionary<string, long>(StringComparer.Ordinal);
            try
            {
                await foreach (var entry in storage.ListAsync(listPrefix, cancellationToken).ConfigureAwait(false))
                {
                    entries[entry.Path] = entry.Size;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _listingFailed = true;
                logger.Log($"Listing '{listPrefix}' failed ({ex.Message}); checking chunks one by one instead.", LogLevel.Warning);
                return null;
            }

            logger.Log($"Listed {entries.Count} stored chunk object(s) under '{listPrefix}' for existence checks.", LogLevel.Debug);
            Volatile.Write(ref _listed, entries);
            return entries;
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
