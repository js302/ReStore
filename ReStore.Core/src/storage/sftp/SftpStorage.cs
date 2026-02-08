using Renci.SshNet;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.sftp;

public class SftpStorage(ILogger logger) : StorageBase(logger)
{
    private SftpClient? _sftpClient;
    private bool _disposed = false;

    public override async Task InitializeAsync(Dictionary<string, string> options)
    {
        ValidateOptions(options);

        try
        {
            var host = options["host"];
            var username = options["username"];
            var port = options.ContainsKey("port") ? int.Parse(options["port"]) : 22;

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

            await Task.Run(() => _sftpClient.Connect());
            Logger.Log($"Connected to SFTP server: {host}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize SFTP storage", ex);
        }
    }

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
            current = string.IsNullOrEmpty(current) ? $"/{part}" : $"{current}/{part}";
            if (!_sftpClient!.Exists(current))
            {
                _sftpClient.CreateDirectory(current);
            }
        }
    }

    public override async Task UploadAsync(string localPath, string remotePath)
    {
        var sftpPath = NormalizePath(remotePath);
        var remoteDir = Path.GetDirectoryName(sftpPath)?.Replace("\\", "/");
        
        if (!string.IsNullOrEmpty(remoteDir))
        {
            if (!_sftpClient!.Exists(remoteDir))
            {
                EnsureDirectoryExists(remoteDir);
            }
        }

        using var fileStream = File.OpenRead(localPath);
        await Task.Run(() => _sftpClient!.UploadFile(fileStream, sftpPath, true));
    }

    public override async Task DownloadAsync(string remotePath, string localPath)
    {
        var sftpPath = NormalizePath(remotePath);

        // Ensure local directory exists
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fileStream = File.Create(localPath);
        await Task.Run(() => _sftpClient!.DownloadFile(sftpPath, fileStream));
    }

    public override async Task<bool> ExistsAsync(string remotePath)
    {
        var sftpPath = NormalizePath(remotePath);
        return await Task.Run(() => _sftpClient!.Exists(sftpPath));
    }

    public override async Task DeleteAsync(string remotePath)
    {
        var sftpPath = NormalizePath(remotePath);
        if (await ExistsAsync(sftpPath))
        {
            await Task.Run(() => _sftpClient!.DeleteFile(sftpPath));
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
