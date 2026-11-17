using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace ReStore.Core.src.utils;

/// <summary>One validated <c>.reg</c> file ready to import.</summary>
public sealed record RegistryImportItem(string RegistryKey, string FilePath, bool RequiresElevation);

public sealed class RegistryImportPlan
{
    public List<RegistryImportItem> Items { get; } = [];
    public List<string> Rejected { get; } = [];
}

public sealed class RegistryImportResult
{
    public int Imported { get; set; }
    public List<string> Failures { get; } = [];
}

/// <summary>
/// Turns a settings manifest into a list of safe imports. Nothing from the archive is executed: file names must be
/// plain names inside the extraction directory, the exported key and every key inside the file must be on the
/// allow-list, and key deletions are rejected.
/// </summary>
public static class WindowsSettingsRestorePlanner
{
    public static RegistryImportPlan BuildPlan(WindowsSettingsExport manifest, string extractDir)
    {
        var plan = new RegistryImportPlan();
        var root = Path.GetFullPath(extractDir);

        foreach (var (registryKey, fileName) in manifest.ExportedFiles)
        {
            if (!RegistryImportPolicy.IsKeyAllowed(registryKey))
            {
                plan.Rejected.Add($"{registryKey}: key is not on the allow-list");
                continue;
            }

            if (string.IsNullOrWhiteSpace(fileName)
                || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
                || !fileName.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
            {
                plan.Rejected.Add($"{registryKey}: '{fileName}' is not a plain .reg file name");
                continue;
            }

            var filePath = Path.GetFullPath(Path.Combine(root, fileName));
            if (!filePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(filePath))
            {
                plan.Rejected.Add($"{registryKey}: file '{fileName}' not found in the backup");
                continue;
            }

            RegFileValidation validation;
            try
            {
                validation = RegistryImportPolicy.Validate(ReadRegText(filePath), registryKey);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                plan.Rejected.Add($"{registryKey}: cannot read '{fileName}': {ex.Message}");
                continue;
            }

            if (!validation.IsValid)
            {
                plan.Rejected.Add($"{registryKey}: {fileName} rejected ({validation.Errors[0]})");
                continue;
            }

            plan.Items.Add(new RegistryImportItem(registryKey, filePath, RegistryImportPolicy.IsMachineKey(registryKey)));
        }

        return plan;
    }

    /// <summary>Reads a .reg file; <c>reg export</c> writes UTF-16 LE with a BOM, which StreamReader detects.</summary>
    public static string ReadRegText(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Imports validated <c>.reg</c> files with <c>reg.exe import</c>. HKCU files are imported in this (user) process;
/// only HKLM files are run elevated, one UAC prompt per file. If the user declines UAC, remaining HKLM files are skipped.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSettingsImporter(ILogger logger)
{
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(2);

    public async Task<RegistryImportResult> ImportAsync(RegistryImportPlan plan, CancellationToken cancellationToken = default)
    {
        var result = new RegistryImportResult();
        var elevationDeclined = false;

        foreach (var item in plan.Items.OrderBy(i => i.RequiresElevation))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.RequiresElevation && elevationDeclined)
            {
                result.Failures.Add($"{item.RegistryKey}: skipped (administrator approval was declined)");
                continue;
            }

            try
            {
                var exitCode = item.RequiresElevation && !IsElevated()
                    ? await ImportElevatedAsync(item.FilePath, cancellationToken).ConfigureAwait(false)
                    : await ImportAsUserAsync(item.FilePath, cancellationToken).ConfigureAwait(false);

                if (exitCode == 0)
                {
                    result.Imported++;
                    logger.Log($"Imported {item.RegistryKey}", LogLevel.Info);
                }
                else
                {
                    result.Failures.Add($"{item.RegistryKey}: reg import exit code {exitCode}");
                }
            }
            catch (Win32Exception ex) when (item.RequiresElevation && ex.NativeErrorCode == 1223)
            {
                elevationDeclined = true;
                result.Failures.Add($"{item.RegistryKey}: administrator approval was declined");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Failures.Add($"{item.RegistryKey}: {ex.Message}");
            }
        }

        foreach (var failure in result.Failures)
        {
            logger.Log($"Settings import failed: {failure}", LogLevel.Warning);
        }

        return result;
    }

    private static async Task<int> ImportAsUserAsync(string filePath, CancellationToken cancellationToken)
    {
        var run = await ProcessRunner.RunAsync("reg.exe", ["import", filePath], ImportTimeout, cancellationToken).ConfigureAwait(false);
        return run.TimedOut ? -1 : run.ExitCode;
    }

    private static async Task<int> ImportElevatedAsync(string filePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "reg.exe",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("import");
        startInfo.ArgumentList.Add(filePath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the elevated reg.exe process.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ImportTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (Exception) { /* elevated process may not be killable */ }
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return -1;
        }

        return process.ExitCode;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
