using System.Runtime.CompilerServices;
using Renci.SshNet;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.sftp;

public class SftpStorage(ILogger logger) : StorageBase(logger)
{
    private SftpClient? _sftpClient;
    private bool _disposed = false;
    private string? _hostKeyError;

    protected override async Task InitializeCoreAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try
        {
            var host = options["host"];
            var username = options["username"];
            var port = ParsePort(options);

            // Support password or private key
            if (options.TryGetValue("privateKeyPath", out var keyPath) && !string.IsNullOrEmpty(keyPath))
            {
                var keyFile = new PrivateKeyFile(keyPath, options.GetValueOrDefault("passphrase"));
                _sftpClient = new SftpClient(host, port, username, keyFile);
            }
            else if (options.TryGetValue("password", out var password))
            {
                _sftpClient = new SftpClient(host, port, username, password);
            }
            else
            {
                throw new ArgumentException("SFTP requires either 'password' or 'privateKeyPath'");
            }

            options.TryGetValue("hostKeyFingerprint", out var expectedFingerprint);
            if (!string.IsNullOrWhiteSpace(expectedFingerprint) && !SftpHostKey.IsValid(expectedFingerprint))
            {
                throw new ArgumentException(
                    "SFTP 'hostKeyFingerprint' must be a SHA256 fingerprint as printed by OpenSSH (for example 'SHA256:AbC...').");
            }

            _sftpClient.HostKeyReceived += (_, e) => OnHostKeyReceived(host, expectedFingerprint, e);

            await Task.Run(() => _sftpClient.Connect()).ConfigureAwait(false);
            Logger.Log($"Connected to SFTP server: {host}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            // A rejected host key surfaces from SSH.NET as a generic key-exchange failure; report the real cause.
            throw new InvalidOperationException(_hostKeyError ?? "Failed to initialize SFTP storage", ex);
        }
    }

    private void OnHostKeyReceived(string host, string? expectedFingerprint, Renci.SshNet.Common.HostKeyEventArgs e)
    {
        var actual = e.FingerPrintSHA256;

        if (string.IsNullOrWhiteSpace(expectedFingerprint))
        {
            // Trust on first use: accept, but make the fingerprint visible so it can be pinned.
            e.CanTrust = true;
            Logger.Log(
                $"SFTP host key for {host} is not pinned; trusting SHA256:{actual} ({e.HostKeyName}). " +
                $"To pin it, add \"hostKeyFingerprint\": \"SHA256:{actual}\" to this SFTP source's options in config.json.",
                LogLevel.Warning);
            return;
        }

        if (SftpHostKey.Matches(expectedFingerprint, actual))
        {
            e.CanTrust = true;
            return;
        }

        e.CanTrust = false;
        _hostKeyError =
            $"SFTP host key verification failed for {host}: the server presented SHA256:{actual} " +
            $"but 'hostKeyFingerprint' is configured as {expectedFingerprint}. " +
            "Refusing to connect (possible man-in-the-middle, or the server key changed).";
        Logger.Log(_hostKeyError, LogLevel.Error);
    }

    /// <summary>The configured port (22 when absent). A machine format: parsed the same under every culture.</summary>
    internal static int ParsePort(Dictionary<string, string> options) =>
        options.TryGetValue("port", out var port) ? int.Parse(port, System.Globalization.CultureInfo.InvariantCulture) : 22;

    private void ValidateOptions(Dictionary<string, string> options)
    {
        var required = new[] { "host", "username" };
        var missing = required.Where(key => !options.ContainsKey(key) || string.IsNullOrEmpty(options[key]));
        
        if (missing.Any())
        {
            throw new ArgumentException($"Missing SFTP configuration: {string.Join(", ", missing)}");
        }
    }

    private string NormalizePath(string path)
    {
        return path.Replace("\\", "/");
    }

    private void EnsureDirectoryExists(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = path.StartsWith("/") ? "/" : "";

        foreach (var part in parts)
        {
            // Keep relative paths relative (to the login directory) so the folders created here are the same
            // ones the upload/list calls address; only paths that start with "/" are absolute.
            current = current.Length == 0 ? part : current == "/" ? $"/{part}" : $"{current}/{part}";
            if (!_sftpClient!.Exists(current))
            {
                _sftpClient.CreateDirectory(current);
            }
        }
    }

    protected override async Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct)
    {
        var sftpPath = NormalizePath(remotePath);
        var remoteDir = Path.GetDirectoryName(sftpPath)?.Replace("\\", "/");

        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(remoteDir))
        {
            if (!await _sftpClient!.ExistsAsync(remoteDir, ct).ConfigureAwait(false))
            {
                EnsureDirectoryExists(remoteDir);
            }
        }

        // Upload to a sibling .part-<guid> file and rename, so a dropped connection never leaves a
        // truncated object at the final path.
        var partPath = sftpPath + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fileStream = File.OpenRead(localPath))
            {
                await _sftpClient!.UploadFileAsync(fileStream, partPath, true, null, ct).ConfigureAwait(false);
            }

            await Task.Run(() => RenameOverwriting(partPath, sftpPath), ct).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (_sftpClient != null && _sftpClient.IsConnected && _sftpClient.Exists(partPath))
                {
                    _sftpClient.DeleteFile(partPath);
                }
            }
            catch (Exception cleanupEx)
            {
                Logger.Log($"Failed to remove partial SFTP upload {partPath}: {cleanupEx.Message}", LogLevel.Warning);
            }

            throw;
        }
    }

    /// <summary>Temp name used by our atomic upload: <c>&lt;final&gt;.part-&lt;guid&gt;</c> where <c>&lt;final&gt;</c> is itself a stored object name.</summary>
    internal static bool IsOwnPartFile(string path) => PartNamePattern.IsMatch(path);

    private static readonly System.Text.RegularExpressions.Regex PartNamePattern =
        new(@"\.part-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void RenameOverwriting(string partPath, string finalPath)
    {
        try
        {
            // posix-rename@openssh.com replaces an existing target atomically.
            _sftpClient!.RenameFile(partPath, finalPath, isPosix: true);
            return;
        }
        catch (Exception ex) when ((ex is Renci.SshNet.Common.SshException || ex is NotSupportedException) && _sftpClient!.IsConnected)
        {
            // Server lacks the extension (or rejected it): fall back to delete + plain rename.
        }

        if (_sftpClient!.Exists(finalPath))
        {
            _sftpClient.DeleteFile(finalPath);
        }

        _sftpClient.RenameFile(partPath, finalPath);
    }

    protected override async Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken ct)
    {
        var sftpPath = NormalizePath(remotePath);

        try
        {
            await DownloadAtomicAsync(localPath, async partPath =>
            {
                using var fileStream = File.Create(partPath);
                await _sftpClient!.DownloadFileAsync(sftpPath, fileStream, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException ex)
        {
            throw new StorageNotFoundException($"File not found on SFTP server: {remotePath}", ex) { RemotePath = remotePath };
        }
    }

    protected override async Task<bool> ExistsCoreAsync(string remotePath, CancellationToken ct)
    {
        var sftpPath = NormalizePath(remotePath);
        return await _sftpClient!.ExistsAsync(sftpPath, ct).ConfigureAwait(false);
    }

    protected override async Task<long?> GetSizeCoreAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            var file = await _sftpClient!.GetAsync(NormalizePath(remotePath), ct).ConfigureAwait(false);
            return file.IsDirectory ? null : file.Length;
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return null;
        }
    }

    protected override async IAsyncEnumerable<StorageEntry> ListCoreAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var normalized = NormalizePath(prefix ?? string.Empty);
        var slash = normalized.LastIndexOf('/');
        var directoryPart = slash >= 0 ? normalized[..slash] : string.Empty;

        var pending = new Stack<string>();
        pending.Push(directoryPart);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var listPath = directory.Length == 0 ? "." : directory;
            if (!await _sftpClient!.ExistsAsync(listPath, ct).ConfigureAwait(false))
            {
                continue;
            }

            await foreach (var item in _sftpClient.ListDirectoryAsync(listPath, ct).ConfigureAwait(false))
            {
                if (item.Name == "." || item.Name == "..")
                {
                    continue;
                }

                var logical = directory.Length == 0 ? item.Name : directory + "/" + item.Name;
                if (item.IsDirectory)
                {
                    pending.Push(logical);
                }
                else if (item.IsRegularFile && logical.StartsWith(normalized, StringComparison.Ordinal) && !IsOwnPartFile(logical))
                {
                    yield return new StorageEntry(logical, item.Length, item.LastWriteTimeUtc);
                }
            }
        }
    }

    protected override async Task DeleteCoreAsync(string remotePath, CancellationToken ct)
    {
        var sftpPath = NormalizePath(remotePath);
        try
        {
            if (await _sftpClient!.ExistsAsync(sftpPath, ct).ConfigureAwait(false))
            {
                await _sftpClient.DeleteFileAsync(sftpPath, ct).ConfigureAwait(false);
            }
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            // Already gone: delete of a missing path is a no-op.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _sftpClient?.Dispose();
            _sftpClient = null;
            Logger.Log("Disposed SftpStorage resources.", LogLevel.Debug);
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
