using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ReStore.Core.src.storage;

namespace ReStore.Tests;

public enum StorageOp { Upload, Download, Exists, GetSize, Delete, List }

public enum FaultKind
{
    /// <summary>Throws an <see cref="IOException"/> (a retry would succeed).</summary>
    Transient,

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> (a retry would not help).</summary>
    Permanent,

    /// <summary>Throws <see cref="TimeoutException"/>.</summary>
    Timeout,

    /// <summary>Upload only: stores a truncated object at the final key, then throws (a non-atomic provider dropping the connection).</summary>
    PartialWriteThenThrow,

    /// <summary>List only: silently leaves entries out of the result (an eventually consistent listing).</summary>
    ListOmission,

    /// <summary>Waits a few milliseconds, then lets the call through.</summary>
    Latency
}

/// <summary>One scripted fault: "the <paramref name="Nth"/> call of <paramref name="Op"/> (optionally only those whose path contains <paramref name="PathContains"/>) fails".</summary>
internal sealed record ScriptedFault(StorageOp Op, int Nth, FaultKind Kind, string? PathContains = null);

/// <summary>
/// A storage wrapper that injects faults, deterministically. Two sources of faults:
/// <list type="bullet">
/// <item>scripted: <see cref="Script"/> lists "the Nth call of an operation fails like this";</item>
/// <item>seeded and probabilistic: each (operation, path, occurrence-of-that-pair) is hashed with the seed, so the decision does not depend
/// on the order in which parallel workers happen to call the storage, and a failing seed reproduces exactly.</item>
/// </list>
/// Faults can be switched off (<see cref="Enabled"/>) for fault-free phases and for inspecting the store.
/// </summary>
internal sealed class FaultyStorage(IStorage inner, long seed = 0, double rate = 0, IReadOnlyList<FaultKind>? kinds = null) : DelegatingStorage(inner)
{
    private static readonly FaultKind[] DefaultKinds = [FaultKind.Transient, FaultKind.Permanent, FaultKind.Timeout, FaultKind.PartialWriteThenThrow, FaultKind.ListOmission, FaultKind.Latency];

    private readonly IReadOnlyList<FaultKind> _kinds = kinds ?? DefaultKinds;
    private readonly ConcurrentDictionary<StorageOp, int> _opCounts = new();
    private readonly ConcurrentDictionary<(StorageOp, string), int> _pairCounts = new();
    private readonly ConcurrentQueue<string> _injected = new();

    public bool Enabled { get; set; } = true;

    public List<ScriptedFault> Script { get; } = [];

    /// <summary>Only operations on paths containing one of these (empty: all) take part in the seeded faults.</summary>
    public List<string> OnlyPaths { get; } = [];

    public IReadOnlyCollection<string> Injected => _injected;

    public int CallCount(StorageOp op) => _opCounts.TryGetValue(op, out var count) ? count : 0;

    private FaultKind? Decide(StorageOp op, string path)
    {
        if (!Enabled)
        {
            return null;
        }

        var nth = _opCounts.AddOrUpdate(op, 1, (_, current) => current + 1);
        foreach (var scripted in Script)
        {
            if (scripted.Op == op && scripted.Nth == nth && (scripted.PathContains == null || path.Contains(scripted.PathContains, StringComparison.Ordinal)))
            {
                return Applicable(op, path, scripted.Kind);
            }
        }

        if (rate <= 0 || (OnlyPaths.Count > 0 && !OnlyPaths.Any(p => path.Contains(p, StringComparison.Ordinal))))
        {
            return null;
        }

        var occurrence = _pairCounts.AddOrUpdate((op, path), 1, (_, current) => current + 1);
        var hash = Hash($"{seed}|{op}|{path}|{occurrence}");
        if ((hash % 100_000) / 100_000.0 >= rate)
        {
            return null;
        }

        var candidates = _kinds.Where(kind => Applicable(op, path, kind) != null).ToList();
        return candidates.Count == 0 ? null : candidates[(int)((hash / 100_000) % (ulong)candidates.Count)];
    }

    /// <summary>The kind, when it makes sense for the operation (partial writes only hit chunk objects: manifests and HEAD are small single PUTs).</summary>
    private static FaultKind? Applicable(StorageOp op, string path, FaultKind kind) => kind switch
    {
        FaultKind.PartialWriteThenThrow => op == StorageOp.Upload && path.Contains("chunks/", StringComparison.Ordinal) ? kind : null,
        FaultKind.ListOmission => op == StorageOp.List ? kind : null,
        _ => kind
    };

    private static ulong Hash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in text)
        {
            hash = (hash ^ ch) * 1099511628211UL;
        }

        return hash;
    }

    private async Task ApplyAsync(StorageOp op, string path, FaultKind? fault, CancellationToken ct)
    {
        if (fault == null)
        {
            return;
        }

        _injected.Enqueue($"{op}:{fault}:{path}");
        switch (fault)
        {
            case FaultKind.Latency:
                await Task.Delay(2, ct).ConfigureAwait(false);
                break;
            case FaultKind.Transient:
                throw new IOException($"injected transient fault on {op} {path}");
            case FaultKind.Permanent:
                throw new UnauthorizedAccessException($"injected permanent fault on {op} {path}");
            case FaultKind.Timeout:
                throw new TimeoutException($"injected timeout on {op} {path}");
        }
    }

    public override async Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        var fault = Decide(StorageOp.Upload, remotePath);
        if (fault == FaultKind.PartialWriteThenThrow)
        {
            await PartialUploadAsync(await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(false), remotePath, ct).ConfigureAwait(false);
        }

        await ApplyAsync(StorageOp.Upload, remotePath, fault, ct).ConfigureAwait(false);
        await Inner.UploadAsync(localPath, remotePath, ct).ConfigureAwait(false);
    }

    public override async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
    {
        var fault = Decide(StorageOp.Upload, remotePath);
        if (fault == FaultKind.PartialWriteThenThrow)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
            await PartialUploadAsync(buffer.ToArray(), remotePath, ct).ConfigureAwait(false);
        }

        await ApplyAsync(StorageOp.Upload, remotePath, fault, ct).ConfigureAwait(false);
        await Inner.UploadAsync(content, remotePath, ct).ConfigureAwait(false);
    }

    private async Task PartialUploadAsync(byte[] bytes, string remotePath, CancellationToken ct)
    {
        using var truncated = new MemoryStream(bytes, 0, Math.Max(1, bytes.Length / 2));
        await Inner.UploadAsync(truncated, remotePath, ct).ConfigureAwait(false);
        _injected.Enqueue($"Upload:PartialWriteThenThrow:{remotePath}");
        throw new IOException($"injected connection drop after a partial upload of {remotePath}");
    }

    public override async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        await ApplyAsync(StorageOp.Download, remotePath, Decide(StorageOp.Download, remotePath), ct).ConfigureAwait(false);
        await Inner.DownloadAsync(remotePath, localPath, ct).ConfigureAwait(false);
    }

    public override async Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default)
    {
        await ApplyAsync(StorageOp.Exists, remotePath, Decide(StorageOp.Exists, remotePath), ct).ConfigureAwait(false);
        return await Inner.ExistsAsync(remotePath, ct).ConfigureAwait(false);
    }

    public override async Task<long?> GetSizeAsync(string remotePath, CancellationToken ct = default)
    {
        await ApplyAsync(StorageOp.GetSize, remotePath, Decide(StorageOp.GetSize, remotePath), ct).ConfigureAwait(false);
        return await Inner.GetSizeAsync(remotePath, ct).ConfigureAwait(false);
    }

    public override async Task DeleteAsync(string remotePath, CancellationToken ct = default)
    {
        await ApplyAsync(StorageOp.Delete, remotePath, Decide(StorageOp.Delete, remotePath), ct).ConfigureAwait(false);
        await Inner.DeleteAsync(remotePath, ct).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<StorageEntry> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fault = Decide(StorageOp.List, prefix);
        if (fault != FaultKind.ListOmission)
        {
            await ApplyAsync(StorageOp.List, prefix, fault, ct).ConfigureAwait(false);
        }
        else
        {
            _injected.Enqueue($"List:ListOmission:{prefix}");
        }

        await foreach (var entry in Inner.ListAsync(prefix, ct).ConfigureAwait(false))
        {
            if (fault == FaultKind.ListOmission && Hash($"{seed}|omit|{entry.Path}") % 4 == 0)
            {
                continue;
            }

            yield return entry;
        }
    }
}
