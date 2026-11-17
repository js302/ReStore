using System.IO.Enumeration;

namespace ReStore.Core.src.utils;

/// <summary>
/// What a single directory walk learned about a file: enough for selection, change detection and progress totals
/// without another stat per file.
/// </summary>
public sealed record ScannedFile(string Path, long Length, DateTime LastWriteUtc, DateTime CreationUtc, FileAttributes Attributes);

public class FileSelectionService(ILogger logger, IConfigManager configManager)
{
    private readonly ILogger _logger = logger;
    private readonly IConfigManager _configManager = configManager;

    /// <summary>Suffix of the temporary file Restore writes next to its target; never part of a backup.</summary>
    public const string RestorePartialExtension = ".restorepartial";

    public static bool IsRestorePartialFile(string path)
        => path.EndsWith(RestorePartialExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Attributes of an OneDrive Files-On-Demand style placeholder whose content is not on disk.</summary>
    private const FileAttributes CloudOnlyAttributes = (FileAttributes)(0x400000 | 0x1000 | 0x40000);

    /// <summary>True for a file whose content lives in the cloud (opening it would download it).</summary>
    public static bool IsCloudOnly(FileAttributes attributes) => (attributes & CloudOnlyAttributes) != 0;

    /// <summary>True when cloud-only files are downloaded for backup (<c>backup.hydrateCloudFiles</c>).</summary>
    public bool HydratesCloudFiles => _configManager.BackupSettings?.HydrateCloudFiles ?? false;

    /// <summary>
    /// True for a cloud-only placeholder that would be skipped. Kept apart from <see cref="ShouldExcludeFile"/>: such a
    /// file is not "excluded" from the snapshot, its previous version must be carried forward.
    /// </summary>
    public bool IsCloudOnlyFile(string path)
    {
        try
        {
            if (HydratesCloudFiles)
            {
                return false;
            }

            var info = new FileInfo(path);
            return info.Exists && IsCloudOnly(info.Attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// A directory is a link to skip only when it is a name surrogate (symlink, junction, mount point). Other reparse
    /// points, such as OneDrive Files-On-Demand folders, are ordinary directories and are entered. An unreadable link
    /// target counts as a link: not following is the safe side.
    /// </summary>
    internal static bool IsLinkDirectory(FileAttributes attributes, Func<string?> linkTarget)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            return linkTarget() != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Test seam: called with every directory the walk lists.</summary>
    internal Action<string>? DirectoryListed { get; set; }

    internal ScannedFile Observe(ScannedFile file) => ScanInterceptor?.Invoke(file) ?? file;

    /// <summary>Test seam: replaces each scanned file's stat (e.g. to inject cloud attributes) before selection.</summary>
    internal Func<ScannedFile, ScannedFile>? ScanInterceptor { get; set; }

    /// <summary>
    /// The selection rules for one decision or one walk, read from the config once. Exclusion by path, by pattern,
    /// by attribute and size, and the automatic exclusion of the backup destination all live here so the walk, the
    /// watcher and the incremental snapshot apply the same rules.
    /// </summary>
    private sealed class Rules
    {
        public required List<string> ExcludedRoots { get; init; }
        public required List<ExcludePattern> Patterns { get; init; }
        public required List<string> WatchRoots { get; init; }
        public required bool HydrateCloudFiles { get; init; }
        public required long MaxFileSizeBytes { get; init; }
    }

    /// <summary>
    /// A folder the user explicitly asked to back up (<c>backup &lt;dir&gt; --allow-excluded</c>): <c>excludedPaths</c> entries that
    /// contain it are ignored for the walk. Entries inside it, patterns and attribute rules still apply.
    /// </summary>
    internal string? LiftExcludedPathsContaining { get; set; }

    /// <summary>
    /// Why <paramref name="directory"/> itself would contribute no files to a backup, or null: an <c>excludedPaths</c> entry that
    /// contains it, or an <c>excludeGlobs</c> / preset pattern that matches the folder. The text names the rule so it can be changed.
    /// </summary>
    public string? FindExcludingRule(string directory)
    {
        if (!TryNormalize(directory, out var normalized))
        {
            return null;
        }

        foreach (var excluded in _configManager.ExcludedPaths ?? [])
        {
            if (TryNormalize(excluded, out var root) && IsPathWithinRoot(normalized, root))
            {
                return $"excludedPaths entry '{excluded}'";
            }
        }

        var forwardSlashPath = normalized.Replace('\\', '/');
        var name = Path.GetFileName(normalized);
        foreach (var text in _configManager.ExcludeGlobs ?? [])
        {
            if (ExcludePattern.GetCached(text, legacy: false) is { } glob && glob.IsMatch(forwardSlashPath, name, true))
            {
                return $"excludeGlobs pattern '{text}'";
            }
        }

        foreach (var preset in _configManager.ExcludePresets ?? [])
        {
            foreach (var text in ExcludePresetCatalog.PatternsFor(preset))
            {
                if (ExcludePattern.GetCached(text, legacy: false) is { } glob && glob.IsMatch(forwardSlashPath, name, true))
                {
                    return $"pattern '{text}' of excludePresets '{preset}'";
                }
            }
        }

        return null;
    }

    private Rules BuildRules(IEnumerable<string>? extraRoots = null)
    {
        var excludedRoots = new List<string>();
        foreach (var excluded in _configManager.ExcludedPaths ?? [])
        {
            if (TryNormalize(excluded, out var normalized))
            {
                excludedRoots.Add(normalized);
            }
        }

        if (LiftExcludedPathsContaining != null && TryNormalize(LiftExcludedPathsContaining, out var lifted))
        {
            excludedRoots.RemoveAll(root => IsPathWithinRoot(lifted, root));
        }

        var patterns = new List<ExcludePattern>();
        foreach (var text in _configManager.ExcludedPatterns ?? [])
        {
            if (ExcludePattern.GetCached(text, legacy: true) is { } legacyPattern)
            {
                patterns.Add(legacyPattern);
            }
        }

        var globTexts = new List<string>(_configManager.ExcludeGlobs ?? []);
        foreach (var preset in _configManager.ExcludePresets ?? [])
        {
            globTexts.AddRange(ExcludePresetCatalog.PatternsFor(preset));
        }

        foreach (var text in globTexts)
        {
            if (ExcludePattern.GetCached(text, legacy: false) is { } glob)
            {
                patterns.Add(glob);
            }
        }

        var watchRoots = new List<string>();
        foreach (var watch in _configManager.WatchDirectories ?? [])
        {
            if (TryNormalize(watch.Path, out var normalized))
            {
                watchRoots.Add(normalized);
            }
        }

        // The backup destination (and ReStore's own data) is never part of a backup when it lies inside a watched
        // folder. Left alone when it also contains a watched folder: that is a misconfiguration, and excluding it
        // would exclude the whole backup.
        var scanRoots = new List<string>(watchRoots);
        foreach (var extra in extraRoots ?? [])
        {
            if (TryNormalize(extra, out var normalized))
            {
                scanRoots.Add(normalized);
            }
        }

        foreach (var destination in GetDestinationRoots(_configManager))
        {
            var insideScanRoot = scanRoots.Any(root => !root.Equals(destination, StringComparison.OrdinalIgnoreCase) && IsPathWithinRoot(destination, root));
            var containsScanRoot = scanRoots.Any(root => IsPathWithinRoot(root, destination));
            if (insideScanRoot && !containsScanRoot)
            {
                excludedRoots.Add(destination);
            }
        }

        return new Rules
        {
            ExcludedRoots = excludedRoots,
            Patterns = patterns,
            WatchRoots = watchRoots,
            HydrateCloudFiles = _configManager.BackupSettings?.HydrateCloudFiles ?? false,
            MaxFileSizeBytes = _configManager.MaxFileSizeMB * 1024 * 1024L
        };
    }

    /// <summary>Normalised roots of the local storage destinations plus the ReStore data directory.</summary>
    public static List<string> GetDestinationRoots(IConfigManager config)
    {
        var roots = new List<string>();
        if (TryNormalize(AppPaths.Root, out var dataRoot))
        {
            roots.Add(dataRoot);
        }

        if (config.StorageSources != null)
        {
            foreach (var (name, source) in config.StorageSources)
            {
                if (source == null || !string.Equals(name, "local", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = source.Options != null && source.Options.TryGetValue("path", out var optionPath) && !string.IsNullOrWhiteSpace(optionPath)
                    ? optionPath
                    : source.Path;
                if (!string.IsNullOrWhiteSpace(path) && TryNormalize(path, out var normalized))
                {
                    roots.Add(normalized);
                }
            }
        }

        return roots;
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = NormalizePath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <param name="watchedRoot">The watched folder the path belongs to, when the caller knows it.</param>
    public bool ShouldExcludeFile(string filePath, string? watchedRoot = null)
    {
        filePath = NormalizePath(filePath);
        var rules = BuildRules(watchedRoot == null ? null : [watchedRoot]);

        if (IsExcludedByPath(rules, filePath))
        {
            return true;
        }

        bool? isDirectory = null;
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Exists)
            {
                isDirectory = false;
                if (IsExcludedByAttributesOrSize(rules, filePath, fileInfo.Attributes, fileInfo.Length))
                {
                    return true;
                }
            }
            else
            {
                var dirInfo = new DirectoryInfo(filePath);
                if (dirInfo.Exists)
                {
                    isDirectory = true;
                    if ((dirInfo.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0
                        || IsLinkDirectory(dirInfo.Attributes, () => dirInfo.LinkTarget))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Not a reason to drop the file: include it and let the backup's per-file handler decide.
            _logger.Log($"Error accessing file/directory {filePath}: {ex.GetType().Name}: {ex.Message}; including it so the backup can handle it", LogLevel.Warning);
        }

        // The watched folder the path is in; directories above it are not part of the walk, so never pruned by it.
        var root = rules.WatchRoots
            .Where(watch => !watch.Equals(filePath, StringComparison.OrdinalIgnoreCase) && IsPathWithinRoot(filePath, watch))
            .OrderByDescending(watch => watch.Length)
            .FirstOrDefault();
        return IsExcludedByPattern(rules, filePath, isDirectory, root);
    }

    /// <summary>Same decision as <see cref="ShouldExcludeFile(string)"/> for a file whose stat is already known (its directories were already checked by the walk).</summary>
    private bool ShouldExclude(Rules rules, ScannedFile file)
    {
        var filePath = NormalizePath(file.Path);
        return IsExcludedByPath(rules, filePath)
            || IsExcludedByAttributesOrSize(rules, filePath, file.Attributes, file.Length)
            || IsExcludedByPattern(rules, filePath, false, null, checkAncestors: false);
    }

    private static bool IsExcludedByPath(Rules rules, string normalizedPath)
        => IsRestorePartialFile(normalizedPath)
            || rules.ExcludedRoots.Any(root => IsPathWithinRoot(normalizedPath, root));

    private bool IsExcludedByAttributesOrSize(Rules rules, string filePath, FileAttributes attributes, long length)
    {
        if ((attributes & FileAttributes.System) == FileAttributes.System ||
            (attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
        {
            return true;
        }

        if (length > rules.MaxFileSizeBytes)
        {
            _logger.Log($"Skipping large file: {filePath} ({length / (1024 * 1024)}MB)", LogLevel.Debug);
            return true;
        }

        return false;
    }

    private static bool IsExcludedByPattern(Rules rules, string normalizedPath, bool? isDirectory, string? root, bool checkAncestors = true)
    {
        if (rules.Patterns.Count == 0)
        {
            return false;
        }

        if (MatchesAnyPattern(rules, normalizedPath, isDirectory))
        {
            return true;
        }

        if (!checkAncestors)
        {
            return false;
        }

        // Only directories below the watched root: the ones above it are not part of the walk. Unknown root: none.
        if (root == null)
        {
            return false;
        }

        for (var directory = Path.GetDirectoryName(normalizedPath);
             !string.IsNullOrEmpty(directory) && !directory.Equals(root, StringComparison.OrdinalIgnoreCase) && IsPathWithinRoot(directory, root);
             directory = Path.GetDirectoryName(directory))
        {
            if (MatchesAnyPattern(rules, directory, true))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAnyPattern(Rules rules, string normalizedPath, bool? isDirectory)
    {
        var name = Path.GetFileName(normalizedPath);
        var forwardSlashPath = normalizedPath.Replace('\\', '/');
        foreach (var pattern in rules.Patterns)
        {
            if (pattern.IsMatch(forwardSlashPath, name, isDirectory))
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="unreadableDirectories">Receives directories that could not be enumerated (their files are not in the result).</param>
    public List<string> GetFilesToBackup(List<string> includePaths, List<string>? unreadableDirectories = null)
        => [.. ScanFiles(includePaths, unreadableDirectories).Select(file => file.Path)];

    /// <summary>
    /// One walk over the include paths, returning each selected file with the stat the walk already read
    /// (length, timestamps, attributes). Selection, change detection and progress totals reuse these records
    /// instead of stat-ing every file again. Directories matching an exclusion (path or pattern), hidden and
    /// system directories and directory junctions/symlinks are not entered.
    /// </summary>
    /// <param name="unreadableDirectories">Receives directories that could not be enumerated (their files are not in the result).</param>
    /// <param name="cloudOnlyFiles">Receives cloud-only placeholder files that were otherwise selected but are not
    /// downloaded (unless <c>backup.hydrateCloudFiles</c> is on); they are not in the result.</param>
    public List<ScannedFile> ScanFiles(List<string> includePaths, List<string>? unreadableDirectories = null, List<ScannedFile>? cloudOnlyFiles = null)
    {
        var filesToBackup = new List<ScannedFile>();
        var rules = BuildRules(includePaths);
        var reparseSkipped = new List<string>();

        void Select(ScannedFile file)
        {
            file = Observe(file);
            if (ShouldExclude(rules, file))
            {
                return;
            }

            if (!rules.HydrateCloudFiles && IsCloudOnly(file.Attributes))
            {
                cloudOnlyFiles?.Add(file);
                return;
            }

            filesToBackup.Add(file);
        }

        foreach (var includePath in includePaths)
        {
            try
            {
                if (File.Exists(includePath))
                {
                    var info = new FileInfo(includePath);
                    Select(new ScannedFile(includePath, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Attributes));
                }
                else if (Directory.Exists(includePath))
                {
                    foreach (var file in EnumerateFilesRecursivelySafe(rules, includePath, unreadableDirectories, reparseSkipped))
                    {
                        try
                        {
                            Select(file);
                        }
                        catch (Exception ex)
                        {
                            _logger.Log($"Error evaluating file {file.Path}: {ex.GetType().Name}: {ex.Message}", LogLevel.Warning);
                        }
                    }
                }
                else
                {
                    _logger.Log($"Path not found: {includePath}", LogLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error accessing path {includePath}: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            }
        }

        if (reparseSkipped.Count > 0)
        {
            _logger.Log(
                $"Not following {reparseSkipped.Count} directory junction(s)/symbolic link(s), for example {reparseSkipped[0]}; their targets are not backed up through them.",
                LogLevel.Info);
        }

        if (cloudOnlyFiles is { Count: > 0 })
        {
            _logger.Log($"Skipping {cloudOnlyFiles.Count} cloud-only file(s) that are not downloaded (backup.hydrateCloudFiles is off), for example {cloudOnlyFiles[0].Path}.", LogLevel.Info);
        }

        return filesToBackup;
    }

    /// <summary>A directory that holds nothing at all, found by <see cref="FindEmptyDirectories"/>.</summary>
    public sealed record ScannedDirectory(string Path, DateTime LastWriteUtc, FileAttributes Attributes);

    /// <summary>
    /// Directories under <paramref name="rootDirectory"/> (not the root itself) that contain no files and no
    /// subdirectories. Hidden/system and excluded directories are left out, as the file walk leaves them out.
    /// Unreadable directories are skipped silently: an unknown listing is not "empty".
    /// </summary>
    public List<ScannedDirectory> FindEmptyDirectories(string rootDirectory)
    {
        var empty = new List<ScannedDirectory>();
        if (!Directory.Exists(rootDirectory))
        {
            return empty;
        }

        var rules = BuildRules([rootDirectory]);
        var pending = new Stack<string>();
        pending.Push(rootDirectory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                var any = false;
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    any = true;
                    if (entry is DirectoryInfo sub
                        && (sub.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0
                        && !IsLinkDirectory(sub.Attributes, () => sub.LinkTarget)
                        && !IsDirectoryPruned(rules, sub.FullName))
                    {
                        pending.Push(sub.FullName);
                    }
                }

                if (!any && !string.Equals(current, rootDirectory, StringComparison.Ordinal))
                {
                    var info = new DirectoryInfo(current);
                    var normalized = NormalizePath(current);
                    if (!IsExcludedByPath(rules, normalized) && !IsExcludedByPattern(rules, normalized, true, null, checkAncestors: false))
                    {
                        empty.Add(new ScannedDirectory(current, info.LastWriteTimeUtc, info.Attributes));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Log($"Cannot enumerate directory {current}: {ex.GetType().Name}: {ex.Message}", LogLevel.Warning);
            }
        }

        return empty;
    }

    /// <summary>A directory the walk does not enter: excluded by path or by a directory-matching pattern.</summary>
    private static bool IsDirectoryPruned(Rules rules, string directoryPath)
    {
        var normalized = NormalizePath(directoryPath);
        return IsExcludedByPath(rules, normalized)
            || IsExcludedByPattern(rules, normalized, true, null, checkAncestors: false);
    }

    private IEnumerable<ScannedFile> EnumerateFilesRecursivelySafe(Rules rules, string rootDirectory, List<string>? unreadableDirectories, List<string> reparseSkipped)
    {
        void ReportUnreadable(string directory, Exception ex)
        {
            _logger.Log($"Cannot enumerate directory {directory}: {ex.GetType().Name}: {ex.Message}", LogLevel.Warning);
            unreadableDirectories?.Add(directory);
        }

        var directories = new Stack<string>();
        directories.Push(rootDirectory);

        while (directories.Count > 0)
        {
            var current = directories.Pop();
            DirectoryListed?.Invoke(current);

            // Materialised inside the try: the lazy enumerators throw on MoveNext, not on creation. One listing
            // per directory yields the files with their stat and the subdirectories with their attributes.
            List<ScannedFile> files = [];
            List<string> subDirectories = [];
            try
            {
                var entries = new FileSystemEnumerable<(bool IsDirectory, ScannedFile? File, string Path, FileAttributes Attributes)>(
                    current,
                    (ref FileSystemEntry entry) => entry.IsDirectory
                        ? (true, null, entry.ToFullPath(), entry.Attributes)
                        : (false,
                            new ScannedFile(
                                entry.ToFullPath(),
                                entry.Length,
                                entry.LastWriteTimeUtc.UtcDateTime,
                                entry.CreationTimeUtc.UtcDateTime,
                                entry.Attributes),
                            string.Empty,
                            entry.Attributes),
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        AttributesToSkip = 0,
                        ReturnSpecialDirectories = false
                    });

                foreach (var entry in entries)
                {
                    if (entry.IsDirectory)
                    {
                        // Skip hidden and system directories
                        if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        {
                            continue;
                        }

                        // Never follow junctions/symlinks: their target is backed up where it really lives, and a
                        // link back up the tree would loop.
                        if (IsLinkDirectory(entry.Attributes, () => new DirectoryInfo(entry.Path).LinkTarget))
                        {
                            reparseSkipped.Add(entry.Path);
                            continue;
                        }

                        if (!IsDirectoryPruned(rules, entry.Path))
                        {
                            subDirectories.Add(entry.Path);
                        }
                    }
                    else
                    {
                        files.Add(entry.File!);
                    }
                }
            }
            catch (Exception ex)
            {
                ReportUnreadable(current, ex);
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var subDirectory in subDirectories)
            {
                directories.Push(subDirectory);
            }
        }
    }

    private static string NormalizePath(string path)
    {
        var normalizedSeparators = path
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        return Path.GetFullPath(ConfigManager.ExpandConfiguredPath(normalizedSeparators))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var normalizedPath = NormalizePath(path);

        if (normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
