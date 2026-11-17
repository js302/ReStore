using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

/// <summary>A file or directory in a snapshot's tree. Directory sizes and counts are recursive.</summary>
public sealed class SnapshotTreeNode
{
    private readonly List<SnapshotTreeNode> _children = [];

    internal SnapshotTreeNode(string name, string relativePath, bool isDirectory)
    {
        Name = name;
        RelativePath = relativePath;
        IsDirectory = isDirectory;
    }

    /// <summary>Last path segment; empty for the root.</summary>
    public string Name { get; }

    /// <summary>Manifest form: '/'-separated, empty for the root.</summary>
    public string RelativePath { get; }

    public bool IsDirectory { get; }

    /// <summary>File size, or for a directory the total of every file below it.</summary>
    public long SizeBytes { get; internal set; }

    /// <summary>Files below a directory (0 for a file).</summary>
    public int FileCount { get; internal set; }

    /// <summary>Null for a directory the manifest recorded no time for (v2 manifests, or an implied parent).</summary>
    public DateTime? LastModifiedUtc { get; internal set; }

    public string ContentHash { get; internal set; } = string.Empty;

    /// <summary>Directories first, then files, each ordered by name.</summary>
    public IReadOnlyList<SnapshotTreeNode> Children => _children;

    internal void AddChild(SnapshotTreeNode child) => _children.Add(child);

    internal void SortChildren()
    {
        // Iterative: a pathologically deep tree must not overflow the stack.
        var stack = new Stack<SnapshotTreeNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            node._children.Sort(static (a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory)
                {
                    return a.IsDirectory ? -1 : 1;
                }

                var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Name, b.Name);
            });
            foreach (var child in node._children)
            {
                if (child.IsDirectory)
                {
                    stack.Push(child);
                }
            }
        }
    }
}

/// <summary>A snapshot's whole file tree, built from its manifest alone.</summary>
public sealed class SnapshotTree
{
    private readonly Dictionary<string, SnapshotTreeNode> _byPath;

    internal SnapshotTree(
        SnapshotManifest manifest,
        string manifestPath,
        SnapshotTreeNode root,
        Dictionary<string, SnapshotTreeNode> byPath)
    {
        SnapshotId = manifest.SnapshotId;
        Group = manifest.Group;
        ManifestPath = manifestPath;
        var segments = manifestPath.Split('/');
        GroupKey = segments.Length == 3 && segments[0] == "snapshots" && segments[1].Length > 0 ? segments[1] : null;
        CreatedUtc = manifest.CreatedUtc;
        Encrypted = manifest.EncryptionEnabled;
        ManifestVersion = manifest.Version;
        Root = root;
        _byPath = byPath;
    }

    public string SnapshotId { get; }

    /// <summary>The directory that was backed up (the original location).</summary>
    public string Group { get; }

    public string ManifestPath { get; }

    /// <summary>
    /// The <c>snapshots/</c> folder name this manifest lives in (what <see cref="SnapshotBrowser.GetFileVersionsAsync"/>
    /// accepts as a group), or null for a manifest outside that layout.
    /// </summary>
    public string? GroupKey { get; }

    public DateTime CreatedUtc { get; }
    public bool Encrypted { get; }
    public int ManifestVersion { get; }
    public SnapshotTreeNode Root { get; }

    public int FileCount => Root.FileCount;
    public long TotalBytes => Root.SizeBytes;

    /// <summary>Finds a node by manifest path (case-insensitive, either slash). Empty path is the root.</summary>
    public SnapshotTreeNode? Find(string relativePath) =>
        _byPath.TryGetValue(SnapshotBrowser.NormalizeRelativePath(relativePath), out var node) ? node : null;

    /// <summary>Every file path below <paramref name="node"/> (the node itself when it is a file).</summary>
    public static IEnumerable<string> EnumerateFilePaths(SnapshotTreeNode node)
    {
        if (!node.IsDirectory)
        {
            yield return node.RelativePath;
            yield break;
        }

        var stack = new Stack<SnapshotTreeNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var child in current.Children)
            {
                if (child.IsDirectory)
                {
                    stack.Push(child);
                }
                else
                {
                    yield return child.RelativePath;
                }
            }
        }
    }

    /// <summary>Drops nodes that sit below another selected directory, so nothing is counted or restored twice.</summary>
    public static List<SnapshotTreeNode> TopLevel(IEnumerable<SnapshotTreeNode> nodes)
    {
        var distinct = nodes.Distinct().ToList();
        var directories = distinct
            .Where(n => n.IsDirectory)
            .Select(n => n.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. distinct.Where(n =>
        {
            var path = n.RelativePath;
            for (var slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/'))
            {
                path = path[..slash];
                if (directories.Contains(path))
                {
                    return false;
                }
            }

            return !(path.Length > 0 && directories.Contains(string.Empty));
        })];
    }

    /// <summary>The distinct file paths a selection of files and directories stands for (what a restore is asked for).</summary>
    public static HashSet<string> CollectFilePaths(IEnumerable<SnapshotTreeNode> selection)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in TopLevel(selection))
        {
            paths.UnionWith(EnumerateFilePaths(node));
        }

        return paths;
    }

    /// <summary>
    /// Case-insensitive substring search. A query without a slash matches names only (so "docs" does not return every
    /// file under a docs folder); a query containing a slash matches the whole relative path.
    /// </summary>
    public IReadOnlyList<SnapshotTreeNode> Search(string query, int maxResults = 1000)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        query = query.Trim();
        var matchPath = query.Contains('/') || query.Contains('\\');
        if (matchPath)
        {
            query = query.Replace('\\', '/');
        }

        var results = new List<SnapshotTreeNode>();
        var stack = new Stack<SnapshotTreeNode>();
        stack.Push(Root);
        while (stack.Count > 0 && results.Count < maxResults)
        {
            var current = stack.Pop();

            // Reverse so the depth-first walk yields siblings in display order.
            for (var i = current.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(current.Children[i]);
            }

            if (ReferenceEquals(current, Root))
            {
                continue;
            }

            var haystack = matchPath ? current.RelativePath : current.Name;
            if (haystack.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(current);
            }
        }

        return results;
    }
}

/// <summary>One distinct content of a file across a group's history.</summary>
/// <param name="SnapshotId">The oldest snapshot in which this content appears.</param>
/// <param name="ManifestPath">Manifest of that snapshot; restore this version from it.</param>
/// <param name="SnapshotCount">Consecutive snapshots (where the file exists) that held this same content.</param>
public sealed record FileVersion(
    string SnapshotId,
    string ManifestPath,
    DateTime SnapshotCreatedUtc,
    long SizeBytes,
    DateTime LastModifiedUtc,
    string ContentHash,
    int SnapshotCount);

public sealed class FileVersionHistory
{
    /// <summary>Newest first.</summary>
    public List<FileVersion> Versions { get; } = [];

    /// <summary>Snapshots whose manifest could not be read; their versions may be missing from <see cref="Versions"/>.</summary>
    public List<string> Warnings { get; } = [];

    public int SnapshotsScanned { get; internal set; }
}

/// <param name="RelativePath">The path in snapshot A (or in B for an added file).</param>
/// <param name="CaseRename">The file is in both snapshots under paths that differ only in letter case; <paramref name="RelativePathB"/> is the path in B.</param>
public sealed record SnapshotDiffEntry(
    string RelativePath,
    long? SizeBytesA,
    long? SizeBytesB,
    DateTime? LastModifiedUtcA,
    DateTime? LastModifiedUtcB,
    bool CaseRename = false,
    string? RelativePathB = null);

/// <summary>Files compared by relative path between two snapshots (A is the baseline, B the later/other one).</summary>
public sealed class SnapshotDiff
{
    public string SnapshotIdA { get; init; } = string.Empty;
    public string SnapshotIdB { get; init; } = string.Empty;
    public DateTime CreatedUtcA { get; init; }
    public DateTime CreatedUtcB { get; init; }

    /// <summary>In B only.</summary>
    public List<SnapshotDiffEntry> Added { get; } = [];

    /// <summary>In A only.</summary>
    public List<SnapshotDiffEntry> Removed { get; } = [];

    /// <summary>In both (paths compared case-insensitively), with a different content hash or size, or a path that differs in case only.</summary>
    public List<SnapshotDiffEntry> Changed { get; } = [];

    public int UnchangedCount { get; internal set; }

    public bool HasDifferences => Added.Count + Removed.Count + Changed.Count > 0;
}

/// <summary>
/// Reads snapshot manifests to browse a snapshot's tree, follow one file across snapshots and compare two snapshots.
/// Manifest-only: no chunk is ever downloaded, and manifests are stored unencrypted (only chunks are encrypted), so no
/// password is needed to browse. Restoring chosen entries is <see cref="Restore"/>'s job.
/// </summary>
public sealed class SnapshotBrowser(ILogger logger, IStorage storage, RepositoryCoordinator? coordinator = null, IPasswordProvider? passwordProvider = null)
{
    private const string SnapshotsPrefix = "snapshots/";
    private const string ManifestSuffix = ".manifest.json";

    private readonly ILogger _logger = logger;
    private readonly IStorage _storage = storage;
    private readonly RepositoryCoordinator _coordinator = coordinator ?? RepositoryCoordinator.Instance;
    private readonly IPasswordProvider? _passwordProvider = passwordProvider;

    // One reader per operation: it unlocks the repository key file once for every encrypted v3 manifest the operation reads.
    private RepositoryManifestReader NewReader() => new(_logger, _storage, _passwordProvider);

    /// <summary>
    /// The folder a restore "to the original location" writes into. Prefers <paramref name="mappedGroup"/> (the path this PC's
    /// history records, which an adopt --map may have remapped) over the path stored in the manifest (the writing PC's).
    /// Null when neither is a rooted path.
    /// </summary>
    public static string? ResolveOriginalLocation(string? mappedGroup, string? manifestGroup)
    {
        foreach (var candidate in new[] { mappedGroup, manifestGroup })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (Path.IsPathRooted(expanded))
            {
                return expanded;
            }
        }

        return null;
    }

    internal static string NormalizeRelativePath(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>Loads one snapshot's tree. <paramref name="manifestPath"/> may be a manifest or a group's HEAD.</summary>
    public async Task<SnapshotTree> LoadTreeAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var readLease = await _coordinator.AcquireReadAsync($"Browsing {manifestPath}", cancellationToken).ConfigureAwait(false);
        await using var readLeaseScope = readLease.ConfigureAwait(false);

        using var reader = NewReader();
        var loaded = await LoadAsync(manifestPath, reader, cancellationToken).ConfigureAwait(false);
        return BuildTree(loaded.Manifest, loaded.ManifestPath);
    }

    /// <summary>Manifest paths of a group's snapshots (oldest name first), found by listing storage.</summary>
    public async Task<IReadOnlyList<string>> ListManifestPathsAsync(string group, CancellationToken cancellationToken = default)
    {
        RepositoryDiscovery.RequireListing(_storage);
        return await FindManifestPathsAsync(group, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every distinct content one file has had across the group's snapshots, newest first. A run of consecutive snapshots
    /// (among those containing the file) with the same content hash is reported once, at its oldest snapshot.
    /// </summary>
    /// <param name="group">The backed-up directory path, or the <c>snapshots/</c> folder name (group key).</param>
    public async Task<FileVersionHistory> GetFileVersionsAsync(
        string group,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            throw new ArgumentException("Group cannot be null or empty", nameof(group));
        }

        var wanted = NormalizeRelativePath(relativePath ?? string.Empty);
        if (wanted.Length == 0)
        {
            throw new ArgumentException("Relative path cannot be empty", nameof(relativePath));
        }

        RepositoryDiscovery.RequireListing(_storage);

        var readLease = await _coordinator.AcquireReadAsync($"Reading history of {wanted}", cancellationToken).ConfigureAwait(false);
        await using var readLeaseScope = readLease.ConfigureAwait(false);

        using var reader = NewReader();
        var history = new FileVersionHistory();
        var manifestPaths = await FindManifestPathsAsync(group, cancellationToken).ConfigureAwait(false);

        var observed = new List<(SnapshotManifest Manifest, string ManifestPath, SnapshotFileManifestEntry File)>();
        foreach (var manifestPath in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var loaded = await RepositoryDiscovery.LoadManifestAsync(_storage, manifestPath, cancellationToken, reader).ConfigureAwait(false);
                history.SnapshotsScanned++;
                var file = loaded.Manifest.Files.FirstOrDefault(f =>
                    NormalizeRelativePath(f.RelativePath).Equals(wanted, StringComparison.OrdinalIgnoreCase));
                if (file != null)
                {
                    observed.Add((loaded.Manifest, manifestPath, file));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Log($"Skipping unreadable manifest {manifestPath}: {ex.Message}", LogLevel.Warning);
                history.Warnings.Add($"{manifestPath}: {ex.Message}");
            }
        }

        // Oldest first so a run collapses onto its first snapshot.
        FileVersion? current = null;
        foreach (var (manifest, manifestPath, file) in observed.OrderBy(o => o.Manifest.CreatedUtc).ThenBy(o => o.ManifestPath, StringComparer.Ordinal))
        {
            if (current != null && current.ContentHash.Equals(file.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                current = current with { SnapshotCount = current.SnapshotCount + 1 };
                history.Versions[^1] = current;
                continue;
            }

            current = new FileVersion(
                manifest.SnapshotId,
                manifestPath,
                manifest.CreatedUtc,
                file.SizeBytes,
                file.LastModifiedUtc,
                file.ContentHash,
                1);
            history.Versions.Add(current);
        }

        history.Versions.Reverse();
        return history;
    }

    /// <summary>Compares two snapshots' files by relative path using the manifests only.</summary>
    public async Task<SnapshotDiff> DiffAsync(string manifestPathA, string manifestPathB, CancellationToken cancellationToken = default)
    {
        var readLease = await _coordinator.AcquireReadAsync("Comparing snapshots", cancellationToken).ConfigureAwait(false);
        await using var readLeaseScope = readLease.ConfigureAwait(false);

        using var reader = NewReader();
        var a = await LoadAsync(manifestPathA, reader, cancellationToken).ConfigureAwait(false);
        var b = await LoadAsync(manifestPathB, reader, cancellationToken).ConfigureAwait(false);
        return Diff(a.Manifest, b.Manifest);
    }

    internal static SnapshotDiff Diff(SnapshotManifest a, SnapshotManifest b)
    {
        var diff = new SnapshotDiff
        {
            SnapshotIdA = a.SnapshotId,
            SnapshotIdB = b.SnapshotId,
            CreatedUtcA = a.CreatedUtc,
            CreatedUtcB = b.CreatedUtc
        };

        var filesA = IndexByPath(a);
        var filesB = IndexByPath(b);

        // Windows paths are case-insensitive: exact matches pair first, then leftovers pair by path ignoring case
        // (a case-only difference is a change, not a removal plus an addition).
        var leftoverB = new Dictionary<string, Queue<(string Path, SnapshotFileManifestEntry File)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, fileB) in filesB)
        {
            if (filesA.ContainsKey(path))
            {
                continue;
            }

            if (!leftoverB.TryGetValue(path, out var queue))
            {
                leftoverB[path] = queue = new Queue<(string, SnapshotFileManifestEntry)>();
            }

            queue.Enqueue((path, fileB));
        }

        foreach (var (path, fileA) in filesA)
        {
            if (filesB.TryGetValue(path, out var sameB))
            {
                if (fileA.SizeBytes != sameB.SizeBytes
                    || !string.Equals(fileA.ContentHash, sameB.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    diff.Changed.Add(new SnapshotDiffEntry(path, fileA.SizeBytes, sameB.SizeBytes, fileA.LastModifiedUtc, sameB.LastModifiedUtc));
                }
                else
                {
                    diff.UnchangedCount++;
                }
            }
            else if (leftoverB.TryGetValue(path, out var queue) && queue.Count > 0)
            {
                var (pathB, fileB) = queue.Dequeue();
                diff.Changed.Add(new SnapshotDiffEntry(path, fileA.SizeBytes, fileB.SizeBytes, fileA.LastModifiedUtc, fileB.LastModifiedUtc, CaseRename: true, RelativePathB: pathB));
            }
            else
            {
                diff.Removed.Add(new SnapshotDiffEntry(path, fileA.SizeBytes, null, fileA.LastModifiedUtc, null));
            }
        }

        foreach (var queue in leftoverB.Values)
        {
            foreach (var (path, fileB) in queue)
            {
                diff.Added.Add(new SnapshotDiffEntry(path, null, fileB.SizeBytes, null, fileB.LastModifiedUtc));
            }
        }

        diff.Added.Sort(static (x, y) => StringComparer.Ordinal.Compare(x.RelativePath, y.RelativePath));
        diff.Removed.Sort(static (x, y) => StringComparer.Ordinal.Compare(x.RelativePath, y.RelativePath));
        diff.Changed.Sort(static (x, y) => StringComparer.Ordinal.Compare(x.RelativePath, y.RelativePath));
        return diff;
    }

    private static Dictionary<string, SnapshotFileManifestEntry> IndexByPath(SnapshotManifest manifest)
    {
        var index = new Dictionary<string, SnapshotFileManifestEntry>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            index[NormalizeRelativePath(file.RelativePath)] = file;
        }

        return index;
    }

    private async Task<LoadedStorageManifest> LoadAsync(string manifestPath, RepositoryManifestReader reader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Manifest path cannot be null or empty", nameof(manifestPath));
        }

        var resolved = await RepositoryDiscovery.ResolveManifestPathAsync(_storage, manifestPath, cancellationToken).ConfigureAwait(false);
        if (!resolved.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Not a snapshot manifest: {resolved}");
        }

        return await RepositoryDiscovery.LoadManifestAsync(_storage, resolved, cancellationToken, reader).ConfigureAwait(false);
    }

    private async Task<List<string>> FindManifestPathsAsync(string group, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        if (!group.Contains('/') && !group.Contains('\\'))
        {
            keys.Add(group);
        }

        var derived = SnapshotStoragePaths.BuildGroupStorageKey(group);
        if (!keys.Contains(derived, StringComparer.Ordinal))
        {
            keys.Add(derived);
        }

        foreach (var key in keys)
        {
            var prefix = $"{SnapshotsPrefix}{key}/";
            var paths = new List<string>();
            await foreach (var entry in _storage.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
            {
                if (entry.Path.StartsWith(prefix, StringComparison.Ordinal)
                    && entry.Path.EndsWith(ManifestSuffix, StringComparison.Ordinal)
                    && entry.Path.IndexOf('/', prefix.Length) < 0)
                {
                    paths.Add(entry.Path);
                }
            }

            if (paths.Count > 0)
            {
                paths.Sort(StringComparer.Ordinal);
                return paths;
            }
        }

        return [];
    }

    internal static SnapshotTree BuildTree(SnapshotManifest manifest, string manifestPath)
    {
        var root = new SnapshotTreeNode(string.Empty, string.Empty, isDirectory: true);
        // byPath serves lookups (case-insensitive, first node wins). dirs merges case-variant folders into one node.
        // Files are never dropped for differing from another only in case: both stay listed.
        var byPath = new Dictionary<string, SnapshotTreeNode>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = root };
        var dirs = new Dictionary<string, SnapshotTreeNode>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = root };
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);

        SnapshotTreeNode EnsureDirectory(string path)
        {
            if (dirs.TryGetValue(path, out var existing))
            {
                return existing;
            }

            var slash = path.LastIndexOf('/');
            var parent = EnsureDirectory(slash < 0 ? string.Empty : path[..slash]);
            var node = new SnapshotTreeNode(slash < 0 ? path : path[(slash + 1)..], path, isDirectory: true);
            parent.AddChild(node);
            dirs[path] = node;
            byPath.TryAdd(path, node);
            return node;
        }

        foreach (var directory in manifest.Directories ?? [])
        {
            var path = NormalizeRelativePath(directory.RelativePath);
            if (path.Length > 0)
            {
                EnsureDirectory(path).LastModifiedUtc = directory.LastWriteUtc;
            }
        }

        foreach (var file in manifest.Files)
        {
            var path = NormalizeRelativePath(file.RelativePath);
            if (path.Length == 0 || !seenFiles.Add(path))
            {
                continue;
            }

            var slash = path.LastIndexOf('/');
            var parent = EnsureDirectory(slash < 0 ? string.Empty : path[..slash]);
            var node = new SnapshotTreeNode(slash < 0 ? path : path[(slash + 1)..], path, isDirectory: false)
            {
                SizeBytes = file.SizeBytes,
                LastModifiedUtc = file.LastModifiedUtc,
                ContentHash = file.ContentHash
            };
            parent.AddChild(node);
            byPath.TryAdd(path, node);
        }

        Aggregate(root);
        root.SortChildren();
        return new SnapshotTree(manifest, manifestPath, root, byPath);
    }

    private static void Aggregate(SnapshotTreeNode root)
    {
        // Iterative post-order: deep trees must not overflow the stack.
        var order = new List<SnapshotTreeNode>();
        var stack = new Stack<SnapshotTreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            foreach (var child in node.Children)
            {
                if (child.IsDirectory)
                {
                    stack.Push(child);
                }
            }
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            long size = 0;
            var count = 0;
            foreach (var child in node.Children)
            {
                size += child.SizeBytes;
                count += child.IsDirectory ? child.FileCount : 1;
            }

            node.SizeBytes = size;
            node.FileCount = count;
        }
    }
}
