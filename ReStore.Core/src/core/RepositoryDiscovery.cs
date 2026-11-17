using System.Text.Json;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

/// <summary>One snapshot found in storage, described from its manifest.</summary>
public sealed record DiscoveredSnapshot(
    string SnapshotId,
    string ManifestPath,
    DateTime CreatedUtc,
    int FileCount,
    long TotalBytes,
    string BackupMode,
    bool Encrypted,
    string? ChunkStorageNamespace,
    string RootHash,
    bool IsHead,
    int ManifestVersion = SnapshotManifest.VersionV2);

/// <summary>One backed-up directory found in storage.</summary>
public sealed class DiscoveredGroup
{
    /// <summary>The <c>snapshots/&lt;key&gt;</c> folder name.</summary>
    public string GroupKey { get; init; } = string.Empty;

    /// <summary>Original full path of the backed-up directory (from group.json, else from the newest manifest).</summary>
    public string OriginalPath { get; init; } = string.Empty;

    public string? MachineName { get; init; }
    public string? InstallId { get; init; }

    /// <summary>True when the group has a readable group.json (false for repositories written before the index existed).</summary>
    public bool HasIndex { get; init; }

    /// <summary>group.json exists but was not readable (encrypted without a password, wrong password, corrupt).</summary>
    public bool IndexUnreadable { get; init; }

    public bool Encrypted { get; init; }

    /// <summary>Newest first.</summary>
    public List<DiscoveredSnapshot> Snapshots { get; init; } = [];

    public List<string> Warnings { get; } = [];
}

public sealed class DiscoveryResult
{
    public List<DiscoveredGroup> Groups { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>A manifest loaded and integrity-checked from storage.</summary>
public sealed record LoadedStorageManifest(string ManifestPath, SnapshotManifest Manifest);

/// <summary>
/// Finds what a repository holds without any local state: lists <c>snapshots/</c>, reads each group's
/// <c>group.json</c> when present, and describes every snapshot from its manifest. Repositories written before
/// group.json existed are found through their manifests and HEAD.
/// </summary>
public sealed class RepositoryDiscovery(ILogger logger, RepositoryCoordinator? coordinator = null)
{
    private readonly RepositoryCoordinator _coordinator = coordinator ?? RepositoryCoordinator.Instance;

    private const string SnapshotsPrefix = "snapshots/";
    private const string ManifestSuffix = ".manifest.json";

    private readonly GroupIndexStore _indexStore = new(logger);

    /// <summary>Lists the repository under a read lease, so a delete or retention cannot remove manifests mid-listing.</summary>
    /// <exception cref="NotSupportedException">The provider cannot list its contents.</exception>
    public async Task<DiscoveryResult> DiscoverAsync(IStorage storage, IPasswordProvider? passwordProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        RequireListing(storage);

        var lease = await _coordinator.AcquireReadAsync("Discovering existing backups", cancellationToken).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        return await DiscoverCoreAsync(storage, passwordProvider, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DiscoveryResult> DiscoverCoreAsync(IStorage storage, IPasswordProvider? passwordProvider, CancellationToken cancellationToken)
    {

        var listing = await ListSnapshotsAsync(storage, cancellationToken).ConfigureAwait(false);

        var result = new DiscoveryResult();
        using var manifestReader = new RepositoryManifestReader(logger, storage, passwordProvider);
        string? password = null;
        var passwordAsked = false;

        foreach (var folder in listing.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = folder.Key;
            var files = folder.Value;

            var warnings = new List<string>();
            var snapshots = new List<(DiscoveredSnapshot Snapshot, SnapshotManifest Manifest)>();
            var headTarget = files.HeadPath == null ? null : await TryReadHeadTargetAsync(storage, files.HeadPath, cancellationToken).ConfigureAwait(false);

            foreach (var manifestPath in files.ManifestPaths.Order(StringComparer.Ordinal))
            {
                try
                {
                    var loaded = await LoadManifestAsync(storage, manifestPath, cancellationToken, manifestReader).ConfigureAwait(false);
                    var manifest = loaded.Manifest;
                    snapshots.Add((new DiscoveredSnapshot(
                        manifest.SnapshotId,
                        manifestPath,
                        manifest.CreatedUtc.ToUniversalTime(),
                        manifest.Files.Count,
                        manifest.Files.Sum(file => file.SizeBytes),
                        manifest.BackupMode,
                        manifest.EncryptionEnabled,
                        manifest.ChunkStorageNamespace,
                        manifest.RootHash,
                        string.Equals(headTarget, manifestPath, StringComparison.Ordinal),
                        manifest.Version), manifest));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add($"Skipped unreadable snapshot '{manifestPath}': {ex.Message}");
                }
            }

            GroupIndex? index = null;
            var indexUnreadable = false;
            if (files.IndexPath != null)
            {
                // The password is only asked for when an encrypted index shows up, and only once.
                var read = await _indexStore.ReadAsync(storage, files.IndexPath, password, cancellationToken).ConfigureAwait(false);
                if (read.Status == GroupIndexReadStatus.PasswordRequired && !passwordAsked && passwordProvider != null)
                {
                    passwordAsked = true;
                    password = await passwordProvider.GetPasswordAsync(PasswordPurpose.Decrypt).ConfigureAwait(false);
                    read = await _indexStore.ReadAsync(storage, files.IndexPath, password, cancellationToken).ConfigureAwait(false);
                }

                if (read.Status == GroupIndexReadStatus.Ok)
                {
                    index = read.Index;
                }
                else
                {
                    indexUnreadable = true;
                    warnings.Add(read.Status == GroupIndexReadStatus.PasswordRequired
                        ? "The repository index is encrypted and no password was provided."
                        : read.Message ?? "The repository index could not be read.");
                }
            }

            if (snapshots.Count == 0 && index == null)
            {
                result.Warnings.AddRange(warnings.Select(w => $"{key}: {w}"));
                continue;
            }

            var ordered = snapshots.OrderByDescending(item => item.Snapshot.CreatedUtc).ToList();
            var newest = ordered.FirstOrDefault().Manifest;
            var group = new DiscoveredGroup
            {
                GroupKey = key,
                OriginalPath = !string.IsNullOrWhiteSpace(index?.GroupPath) ? index!.GroupPath : newest?.Group ?? key,
                MachineName = index?.MachineName,
                InstallId = index?.InstallId,
                HasIndex = index != null,
                IndexUnreadable = indexUnreadable,
                Encrypted = index?.Encrypted ?? ordered.Any(item => item.Snapshot.Encrypted),
                Snapshots = [.. ordered.Select(item => item.Snapshot)]
            };
            group.Warnings.AddRange(warnings);
            result.Groups.Add(group);
        }

        return result;
    }

    /// <summary>
    /// Loads and integrity-checks one manifest from storage. Encrypted v3 manifests need <paramref name="reader"/> (which holds the
    /// password provider and the unlocked repository keys); without one they fail with <see cref="ManifestKeyRequiredException"/>.
    /// </summary>
    public static async Task<LoadedStorageManifest> LoadManifestAsync(
        IStorage storage,
        string manifestPath,
        CancellationToken cancellationToken = default,
        RepositoryManifestReader? reader = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"restore_discovery_{Guid.NewGuid():N}.json");
        try
        {
            await storage.DownloadAsync(manifestPath, temp, cancellationToken).ConfigureAwait(false);
            var manifest = reader != null
                ? await reader.ReadFileAsync(temp, manifestPath, cancellationToken).ConfigureAwait(false)
                : await SnapshotManifestSerializer.ReadFileAsync(temp, cancellationToken).ConfigureAwait(false);
            if (!SnapshotManifestHasher.IsValid(manifest))
            {
                throw new InvalidOperationException("The manifest failed its integrity check.");
            }

            return new LoadedStorageManifest(manifestPath, manifest);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Resolves a HEAD path to the manifest path it names; any other path is returned unchanged.</summary>
    public static async Task<string> ResolveManifestPathAsync(IStorage storage, string path, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return await TryReadHeadTargetAsync(storage, path, cancellationToken).ConfigureAwait(false)
            ?? throw new StorageNotFoundException($"HEAD '{path}' does not name a snapshot manifest.") { RemotePath = path };
    }

    internal static void RequireListing(IStorage storage)
    {
        if (!storage.SupportsListing)
        {
            throw new NotSupportedException(
                "This storage provider cannot list its contents, so existing backups cannot be discovered. " +
                "Restore with an exact snapshot path instead (for example snapshots/<folder>_<hash>/HEAD).");
        }
    }

    internal sealed class FolderListing
    {
        public string? IndexPath { get; set; }
        public string? HeadPath { get; set; }
        public List<string> ManifestPaths { get; } = [];
    }

    /// <summary>Lists <c>snapshots/</c> fully (an interrupted listing throws, it is never treated as complete).</summary>
    internal static async Task<Dictionary<string, FolderListing>> ListSnapshotsAsync(IStorage storage, CancellationToken cancellationToken)
    {
        var folders = new Dictionary<string, FolderListing>(StringComparer.Ordinal);
        await foreach (var entry in storage.ListAsync(SnapshotsPrefix, cancellationToken).ConfigureAwait(false))
        {
            var path = entry.Path;
            if (!path.StartsWith(SnapshotsPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = path[SnapshotsPrefix.Length..].Split('/');
            if (parts.Length != 2 || parts[0].Length == 0)
            {
                continue;
            }

            if (!folders.TryGetValue(parts[0], out var folder))
            {
                folders[parts[0]] = folder = new FolderListing();
            }

            if (parts[1] == "group.json")
            {
                folder.IndexPath = path;
            }
            else if (parts[1] == "HEAD")
            {
                folder.HeadPath = path;
            }
            else if (parts[1].EndsWith(ManifestSuffix, StringComparison.Ordinal))
            {
                folder.ManifestPaths.Add(path);
            }
        }

        return folders;
    }

    internal static async Task<string?> TryReadHeadTargetAsync(IStorage storage, string headPath, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"restore_discovery_head_{Guid.NewGuid():N}.txt");
        try
        {
            await storage.DownloadAsync(headPath, temp, cancellationToken).ConfigureAwait(false);
            var first = (await File.ReadAllLinesAsync(temp, cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(first) ? null : first;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>An <c>old=new</c> path prefix remapping (adopt and restore on a machine whose folders differ).</summary>
public sealed class PathMapping
{
    private readonly List<(string Old, string New)> _rules = [];

    public bool IsEmpty => _rules.Count == 0;

    public static PathMapping Parse(IEnumerable<string>? specs)
    {
        var mapping = new PathMapping();
        foreach (var spec in specs ?? [])
        {
            var split = spec.IndexOf('=');
            if (split <= 0 || split == spec.Length - 1)
            {
                throw new ArgumentException($"Invalid mapping '{spec}'. Expected old=new, for example C:\\Users\\old\\Documents=D:\\Documents.");
            }

            mapping._rules.Add((spec[..split].Trim(), spec[(split + 1)..].Trim()));
        }

        return mapping;
    }

    /// <summary>Replaces the first matching prefix (whole path segments, case-insensitive, either slash style); unmatched paths are unchanged.</summary>
    public string Apply(string path)
    {
        var normalized = Normalize(path);
        foreach (var (oldPrefix, newPrefix) in _rules)
        {
            var old = Normalize(oldPrefix);
            if (normalized.Equals(old, StringComparison.OrdinalIgnoreCase))
            {
                return newPrefix;
            }

            if (normalized.StartsWith(old + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rest = normalized[(old.Length + 1)..];
                var separator = newPrefix.Contains('\\') ? '\\' : '/';
                return newPrefix.TrimEnd('\\', '/') + separator + rest.Replace('/', separator);
            }
        }

        return path;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}

public sealed record AdoptResult(int SnapshotsAdopted, int SnapshotsAlreadyKnown, int SnapshotsFailed, List<string> Messages);

/// <summary>Adds snapshots found in storage to the local history, chunk lists and reference counts.</summary>
public sealed class RepositoryAdopter(ILogger logger, IConfigManager config, SystemState state, RepositoryCoordinator? coordinator = null, IPasswordProvider? passwordProvider = null)
{
    private readonly RepositoryCoordinator _coordinator = coordinator ?? RepositoryCoordinator.Instance;

    /// <summary>
    /// Rebuilds history entries (original timestamps), chunk-id sidecars and reference counts for the given groups'
    /// snapshots under the mutation lease, optionally remapping each group's original path. Idempotent: snapshots
    /// already in the history are left alone. Takes the mutation lease itself; do not call while holding one.
    /// </summary>
    public async Task<AdoptResult> AdoptAsync(
        IStorage storage,
        string storageType,
        IEnumerable<DiscoveredGroup> groups,
        PathMapping? mapping = null,
        CancellationToken cancellationToken = default)
    {
        var mutation = await _coordinator.AcquireMutationAsync("Adopting existing backups", cancellationToken, state).ConfigureAwait(false);
        await using var mutationScope = mutation.ConfigureAwait(false);
        return await AdoptCoreAsync(storage, storageType, groups, mapping, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>As <see cref="AdoptAsync"/> for callers that already hold the mutation lease (repair).</summary>
    internal async Task<AdoptResult> AdoptCoreAsync(
        IStorage storage,
        string storageType,
        IEnumerable<DiscoveredGroup> groups,
        PathMapping? mapping,
        CancellationToken cancellationToken)
    {
        mapping ??= new PathMapping();
        var adopted = 0;
        var known = 0;
        var failed = 0;
        var messages = new List<string>();
        using var manifestReader = new RepositoryManifestReader(logger, storage, passwordProvider);

        foreach (var group in groups)
        {
            var localGroup = mapping.Apply(group.OriginalPath);
            foreach (var snapshot in group.Snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var loaded = await RepositoryDiscovery.LoadManifestAsync(storage, snapshot.ManifestPath, cancellationToken, manifestReader).ConfigureAwait(false);
                    if (AdoptManifest(localGroup, storageType, loaded))
                    {
                        adopted++;
                    }
                    else
                    {
                        known++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    messages.Add($"Could not adopt '{snapshot.ManifestPath}': {ex.Message}");
                    logger.Log($"Adopt: could not adopt '{snapshot.ManifestPath}': {ex.Message}", LogLevel.Warning);
                }
            }
        }

        await state.SaveStateAsync().ConfigureAwait(false);
        logger.Log($"Adopt: {adopted} snapshot(s) added, {known} already known, {failed} failed.", LogLevel.Info);
        return new AdoptResult(adopted, known, failed, messages);
    }

    /// <summary>Adds one loaded manifest to the history; false when it was already there.</summary>
    internal bool AdoptManifest(string localGroup, string storageType, LoadedStorageManifest loaded)
    {
        var manifest = loaded.Manifest;
        var chunkIds = manifest.Files.SelectMany(file => file.Chunks).Select(chunk => chunk.ChunkId);
        return state.AdoptSnapshotBackup(
            localGroup,
            manifest.SnapshotId,
            loaded.ManifestPath,
            string.IsNullOrWhiteSpace(storageType) ? config.GlobalStorageType : storageType,
            manifest.CreatedUtc,
            chunkIds,
            manifest.Files.Sum(file => file.SizeBytes),
            manifest.RootHash,
            manifest.EncryptionEnabled,
            manifest.ChunkStorageNamespace,
            SnapshotChunkUsage.FromManifest(manifest));
    }
}
