using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.updates;

/// <summary>An update cannot be installed (wrong channel, no asset, failed verification, ...). The message is meant for the user.</summary>
public sealed class UpdateInstallException(string message) : Exception(message);

public sealed record DownloadProgress(long Received, long Total);

/// <param name="Relaunch">MSIX: start the app again after installing (the GUI asks for this).</param>
/// <param name="RepositoryWait">How long to wait for another ReStore process that holds the repository; null uses the coordinator's configured wait (<c>--wait</c>).</param>
public sealed record InstallOptions(bool Relaunch = false, TimeSpan? RepositoryWait = null);

/// <summary>What <c>update install --dry-run</c> reports.</summary>
public sealed record InstallPlan(string Channel, string Version, UpdateAsset Asset, string DownloadPath, string Description);

/// <param name="ProcessShouldExit">MSIX: the installer will shut this app down in a moment; the caller should exit.</param>
public sealed record InstallResult(string Version, string Channel, string Message, bool ProcessShouldExit, string? LogPath = null);

/// <summary>
/// Downloads, verifies and installs a release asset. MSIX: verifies the package identity and hands over to a detached PowerShell
/// (<see cref="IMsixInstallLauncher"/>). Portable: swaps the files in the install folder, rolling back on failure.
/// Nothing is ever installed without a matching SHA-256 from <c>latest.json</c>.
/// </summary>
public sealed class UpdateInstaller
{
    public const string ExpectedPackageName = "ReStore";
    public const string FallbackPublisher = "CN=Jaskaran Singh";
    public const string PortableExecutableName = "restore.exe";
    public const string OldSuffix = ".old";

    private static readonly Lazy<HttpMessageHandler> SharedHandler = new(() => new SocketsHttpHandler { AllowAutoRedirect = true });

    private readonly HttpClient _http;
    private readonly IMsixInstallLauncher _launcher;
    private readonly string _channel;
    private readonly string _arch;
    private readonly Func<string?> _updatesRoot;
    private readonly string _installDirectory;
    private readonly string _runningManifestDirectory;
    private readonly RepositoryCoordinator _coordinator;
    private readonly Func<string?> _packageFamilyName;

    public UpdateInstaller(
        HttpMessageHandler? handler = null,
        IMsixInstallLauncher? launcher = null,
        string? channel = null,
        string? architecture = null,
        string? updatesRoot = null,
        string? installDirectory = null,
        string? runningManifestDirectory = null,
        RepositoryCoordinator? coordinator = null,
        Func<string?>? packageFamilyName = null)
    {
        _http = new HttpClient(handler ?? SharedHandler.Value, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _launcher = launcher ?? new PowerShellMsixInstallLauncher();
        _channel = channel ?? AppChannel.Current;
        _arch = architecture ?? AppChannel.Architecture;
        _updatesRoot = () => updatesRoot ?? Path.Combine(AppPaths.Root, "updates");
        _installDirectory = installDirectory ?? AppContext.BaseDirectory;
        _runningManifestDirectory = runningManifestDirectory ?? AppContext.BaseDirectory;
        _coordinator = coordinator ?? RepositoryCoordinator.Instance;
        _packageFamilyName = packageFamilyName ?? GetPackageFamilyName;
    }

    /// <summary>How long without a byte before a download is given up.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public string Channel => _channel;

    /// <summary>The asset for a channel and architecture (msix for the MSIX channel, cli-zip for portable); null for dev or no match.</summary>
    public static UpdateAsset? SelectAsset(IEnumerable<UpdateAsset> assets, string channel, string arch)
    {
        var kind = channel switch
        {
            AppChannel.Msix => UpdateAsset.KindMsix,
            AppChannel.Portable => UpdateAsset.KindCliZip,
            _ => null
        };
        return kind == null
            ? null
            : assets.FirstOrDefault(a => a.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && a.Arch.Equals(arch, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Why <paramref name="result"/> cannot be installed here, or null when it can.</summary>
    public static string? WhyNotInstallable(UpdateCheckResult? result, string channel)
    {
        if (channel == AppChannel.Dev)
        {
            return "This copy was built from source: pull the latest changes and rebuild.";
        }

        if (result is not { Status: UpdateStatus.UpdateAvailable })
        {
            return null;
        }

        if (result.VersionOnly)
        {
            return "The release has no update manifest (latest.json), so its files cannot be verified; download it from the release page.";
        }

        return result.Asset == null ? $"The release has no {(channel == AppChannel.Msix ? "MSIX" : "portable CLI zip")} for this architecture." : null;
    }

    public InstallPlan PreparePlan(UpdateCheckResult result)
    {
        if (WhyNotInstallable(result, _channel) is { } reason)
        {
            throw new UpdateInstallException(reason);
        }

        if (result is not { Status: UpdateStatus.UpdateAvailable, Asset: { } asset, Latest: { } version })
        {
            throw new UpdateInstallException("There is no update to install.");
        }

        ValidateAsset(asset, version);
        var path = DownloadPathFor(version, asset);
        var description = _channel == AppChannel.Msix
            ? $"Download {asset.Name} ({FormatSize(asset.Size)}) to {path}, verify its SHA-256 and package identity, then install it with Add-AppxPackage (ReStore closes and restarts)."
            : $"Download {asset.Name} ({FormatSize(asset.Size)}) to {path}, verify its SHA-256, then replace the files in {_installDirectory}.";
        return new InstallPlan(_channel, version, asset, path, description);
    }

    /// <summary>Downloads and installs <paramref name="result"/>. Takes a repository lease first (<see cref="RepositoryBusyException"/> when busy).</summary>
    public async Task<InstallResult> InstallAsync(
        UpdateCheckResult result, InstallOptions? options = null, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new InstallOptions();
        var plan = PreparePlan(result);
        CleanupOldDownloads(plan.Version);

        var file = await DownloadAsync(plan.Asset, plan.DownloadPath, progress, cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel == AppChannel.Msix)
            {
                VerifyMsixIdentity(file, ReadRunningPublisher(_runningManifestDirectory));
            }

            var lease = await AcquireLeaseAsync(options, cancellationToken).ConfigureAwait(false);
            try
            {
                return _channel == AppChannel.Msix
                    ? InstallMsix(plan, file, options)
                    : InstallPortable(plan, file);
            }
            finally
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (UpdateInstallException) when (_channel == AppChannel.Msix)
        {
            TryDelete(file);
            throw;
        }
    }

    private async Task<RepositoryLease> AcquireLeaseAsync(InstallOptions options, CancellationToken cancellationToken)
    {
        if (options.RepositoryWait is not { } wait)
        {
            return await _coordinator.AcquireMutationAsync("Installing a ReStore update", cancellationToken).ConfigureAwait(false);
        }

        return await _coordinator.TryAcquireMutationAsync(wait, "Installing a ReStore update", cancellationToken).ConfigureAwait(false)
            ?? throw new RepositoryBusyException("ReStore is busy with a backup or another operation; try again when it has finished.");
    }

    // ---- Download & verification ------------------------------------------------------------------------------------------

    private string DownloadPathFor(string version, UpdateAsset asset) => Path.Combine(_updatesRoot()!, version, asset.Name);

    private static void ValidateAsset(UpdateAsset asset, string version)
    {
        if (SemVer.ParseOrNull(version) == null || version.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
        {
            throw new UpdateInstallException("The release has an invalid version number.");
        }

        if (string.IsNullOrWhiteSpace(asset.Name) || asset.Name != Path.GetFileName(asset.Name) || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || asset.Name.StartsWith('.'))
        {
            throw new UpdateInstallException("The release lists an invalid file name.");
        }

        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateInstallException("The release lists a download address that is not https.");
        }

        if (string.IsNullOrWhiteSpace(asset.Sha256) || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit))
        {
            throw new UpdateInstallException($"The release does not list a SHA-256 for {asset.Name}; refusing to install an unverified file.");
        }
    }

    /// <summary>Streams the asset to disk and checks size and SHA-256; a bad file is deleted and the install refused.</summary>
    public async Task<string> DownloadAsync(UpdateAsset asset, string destination, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".partial";
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            stall.CancelAfter(StallTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ReStore", UpdateChecker.CurrentVersion));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateInstallException($"Downloading {asset.Name} failed: HTTP {(int)response.StatusCode}.");
            }

            var declared = response.Content.Headers.ContentLength;
            if (asset.Size > 0 && declared > asset.Size)
            {
                throw new UpdateInstallException($"{asset.Name} is larger than the release says ({declared} bytes, expected {asset.Size}); the download was refused.");
            }

            var total = declared ?? asset.Size;
            long received = 0;
            using var hash = SHA256.Create();
            using (var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    if (asset.Size > 0 && received + read > asset.Size)
                    {
                        throw new UpdateInstallException($"{asset.Name} is larger than the release says (expected {asset.Size} bytes); the download was aborted.");
                    }

                    hash.TransformBlock(buffer, 0, read, null, 0);
                    await target.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new DownloadProgress(received, total));
                }

                hash.TransformFinalBlock([], 0, 0);
            }

            if (asset.Size > 0 && received != asset.Size)
            {
                throw new UpdateInstallException($"{asset.Name} has the wrong size ({received} bytes, expected {asset.Size}); the download was discarded.");
            }

            var actual = Convert.ToHexStringLower(hash.Hash!);
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateInstallException($"{asset.Name} failed the SHA-256 check; the download was discarded and nothing was installed.");
            }

            File.Move(partial, destination, overwrite: true);
            return destination;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(partial);
            throw new UpdateInstallException($"Downloading {asset.Name} stalled and was abandoned.");
        }
        catch (HttpRequestException ex)
        {
            TryDelete(partial);
            throw new UpdateInstallException($"Downloading {asset.Name} failed: {ex.Message}");
        }
        catch
        {
            TryDelete(partial);
            TryDelete(destination);
            throw;
        }
    }

    /// <summary>The Identity of the package inside <paramref name="msixPath"/> must be <c>ReStore</c> from the same publisher as the running install.</summary>
    public static void VerifyMsixIdentity(string msixPath, string expectedPublisher)
    {
        string? name, publisher;
        try
        {
            using var zip = ZipFile.OpenRead(msixPath);
            var entry = zip.GetEntry("AppxManifest.xml") ?? throw new UpdateInstallException("The package has no AppxManifest.xml; refusing to install it.");
            using var stream = entry.Open();
            (name, publisher) = ReadIdentity(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException)
        {
            TryDelete(msixPath);
            throw new UpdateInstallException("The downloaded file is not a valid MSIX package; it was discarded.");
        }

        if (name != ExpectedPackageName)
        {
            TryDelete(msixPath);
            throw new UpdateInstallException($"The package is named '{name}', not '{ExpectedPackageName}'; it was discarded.");
        }

        if (!string.Equals(publisher, expectedPublisher, StringComparison.Ordinal))
        {
            TryDelete(msixPath);
            throw new UpdateInstallException("The package was published by someone other than the running ReStore; it was discarded.");
        }
    }

    /// <summary>The publisher of the installed package (its <c>AppxManifest.xml</c> next to the executable), else <see cref="FallbackPublisher"/>.</summary>
    public static string ReadRunningPublisher(string? directory)
    {
        try
        {
            var path = directory == null ? null : Path.Combine(directory, "AppxManifest.xml");
            if (path != null && File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                if (ReadIdentity(stream).Publisher is { Length: > 0 } publisher)
                {
                    return publisher;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            // Fall through to the constant.
        }

        return FallbackPublisher;
    }

    private static (string? Name, string? Publisher) ReadIdentity(Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        var identity = XDocument.Load(reader).Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
        return (identity?.Attribute("Name")?.Value, identity?.Attribute("Publisher")?.Value);
    }

    // ---- MSIX -------------------------------------------------------------------------------------------------------------

    private InstallResult InstallMsix(InstallPlan plan, string file, InstallOptions options)
    {
        var log = Path.Combine(AppPaths.LogsDirectory, "update-install.log");
        var relaunch = options.Relaunch && _packageFamilyName() is { Length: > 0 } family ? family + "!ReStore" : null;
        try
        {
            _launcher.Launch(file, log, relaunch);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new UpdateInstallException($"Could not start the installer: {ex.Message}");
        }

        return new InstallResult(plan.Version, _channel,
            $"The installer for ReStore {plan.Version} was started; ReStore closes now and the update finishes in a few seconds. Outcome: {log}", ProcessShouldExit: true, log);
    }

    // ---- Portable ---------------------------------------------------------------------------------------------------------

    private InstallResult InstallPortable(InstallPlan plan, string zipPath)
    {
        var versionDirectory = Path.GetDirectoryName(zipPath)!;
        var staging = Path.Combine(versionDirectory, "staging");
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            ExtractSafely(zipPath, staging);
            if (!File.Exists(Path.Combine(staging, PortableExecutableName)))
            {
                throw new UpdateInstallException($"The zip does not contain {PortableExecutableName}; nothing was changed.");
            }

            PortableSwap.Apply(staging, _installDirectory, Path.Combine(versionDirectory, "rollback"));
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteDirectory(Path.Combine(versionDirectory, "rollback"));
        }

        TryDelete(zipPath);
        return new InstallResult(plan.Version, _channel, $"ReStore was updated to {plan.Version} in {_installDirectory}. The new version runs the next time you start restore.exe.", ProcessShouldExit: false);
    }

    /// <summary>Most entries a portable zip may hold.</summary>
    internal const int MaxZipEntries = 10_000;

    /// <summary>Total uncompressed size allowed for a zip of <paramref name="zipLength"/> bytes: 10x the zip (at least 16 MB, so tiny zips are not refused), at most 1 GB.</summary>
    internal static long MaxUncompressedBytes(long zipLength) => Math.Min(Math.Max(zipLength * 10, 16L << 20), 1L << 30);

    internal static void ExtractSafely(string zipPath, string destination, long? maxUncompressedBytes = null, int maxEntries = MaxZipEntries)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var budget = maxUncompressedBytes ?? MaxUncompressedBytes(new FileInfo(zipPath).Length);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > maxEntries)
            {
                throw new UpdateInstallException($"The zip holds {zip.Entries.Count} entries (more than {maxEntries}); it was refused.");
            }

            // The declared sizes are checked first, then the bytes really written (a lying header must not get past the cap).
            if (zip.Entries.Sum(e => e.Length) > budget)
            {
                throw new UpdateInstallException("The zip would expand to more than the allowed size; it was refused.");
            }

            foreach (var entry in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateInstallException("The zip contains a file outside its folder; it was refused.");
                }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var source = entry.Open())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        budget -= read;
                        if (budget < 0)
                        {
                            throw new UpdateInstallException("The zip expands to more than the allowed size; it was refused.");
                        }

                        output.Write(buffer, 0, read);
                    }
                }

                File.SetLastWriteTime(target, entry.LastWriteTime.DateTime);
            }
        }
        catch (InvalidDataException)
        {
            throw new UpdateInstallException("The downloaded file is not a valid zip archive; nothing was changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A name that cannot be created here (too long, a file where a folder is needed, ...): a refusal, not a crash.
            throw new UpdateInstallException("The zip contains a file that cannot be extracted; nothing was changed.");
        }
    }

    /// <summary>Best effort: removes a leftover <c>restore.exe.old</c> from an earlier portable update. Call at CLI start.</summary>
    public static void CleanupLeftovers(string? directory = null)
    {
        try
        {
            var old = Path.Combine(directory ?? AppContext.BaseDirectory, PortableExecutableName + OldSuffix);
            if (File.Exists(old))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use: the next start tries again.
        }
    }

    /// <summary>Removes the download folders of other versions (and failed attempts), keeping <paramref name="keepVersion"/>.</summary>
    private void CleanupOldDownloads(string keepVersion)
    {
        try
        {
            var root = _updatesRoot()!;
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(root).Where(d => !string.Equals(Path.GetFileName(d), keepVersion, StringComparison.Ordinal)))
            {
                TryDeleteDirectory(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not important.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static string FormatSize(long bytes) => bytes <= 0 ? "unknown size" : bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{bytes / 1024.0:0} KB";

    /// <summary>The running package's family name (<c>kernel32!GetCurrentPackageFamilyName</c>), or null when unpackaged or not on Windows.</summary>
    public static string? GetPackageFamilyName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            uint length = 0;
            var result = GetCurrentPackageFamilyName(ref length, null);
            if (result != 122 || length == 0)
            {
                return null;
            }

            var name = new StringBuilder((int)length);
            return GetCurrentPackageFamilyName(ref length, name) == 0 ? name.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);
}

/// <summary>Replaces the files of a portable install with those of a staging folder; any failure restores the previous files.</summary>
internal static class PortableSwap
{
    public static void Apply(string staging, string target, string rollbackDirectory)
    {
        var exeName = UpdateInstaller.PortableExecutableName;
        var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(staging, f))
            .OrderBy(f => string.Equals(f, exeName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();

        var exe = Path.Combine(target, exeName);
        var old = exe + UpdateInstaller.OldSuffix;
        var renamedExe = false;
        var backedUp = new List<string>();
        var created = new List<string>();
        try
        {
            if (File.Exists(exe))
            {
                // A running executable cannot be overwritten but can be renamed.
                File.Delete(old);
                File.Move(exe, old);
                renamedExe = true;
            }

            foreach (var relative in files)
            {
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!string.Equals(relative, exeName, StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
                {
                    var saved = Path.Combine(rollbackDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(destination, saved, overwrite: true);
                    backedUp.Add(relative);
                }
                else if (!File.Exists(destination))
                {
                    created.Add(relative);
                }

                File.Copy(Path.Combine(staging, relative), destination, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Rollback(target, rollbackDirectory, exe, old, renamedExe, backedUp, created);
            throw new UpdateInstallException($"The update could not be applied ({ex.Message}); the previous version was restored.");
        }
    }

    private static void Rollback(string target, string rollbackDirectory, string exe, string old, bool renamedExe, List<string> backedUp, List<string> created)
    {
        foreach (var relative in created)
        {
            Try(() => File.Delete(Path.Combine(target, relative)));
        }

        foreach (var relative in backedUp)
        {
            Try(() => File.Copy(Path.Combine(rollbackDirectory, relative), Path.Combine(target, relative), overwrite: true));
        }

        if (renamedExe)
        {
            Try(() =>
            {
                File.Delete(exe);
                File.Move(old, exe);
            });
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rolling back as far as possible.
        }
    }
}
