using System.Diagnostics;
using System.Text;

namespace ReStore.Core.src.utils;

/// <summary>Outcome of <see cref="ProcessRunner.RunAsync"/>.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// Runs an external program with <see cref="ProcessStartInfo.ArgumentList"/> (no string concatenation),
/// a timeout and cancellation. The process tree is killed on timeout or cancellation.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Encoding? standardOutputEncoding = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (standardOutputEncoding != null)
        {
            startInfo.StandardOutputEncoding = standardOutputEncoding;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");

        // Read both streams concurrently so a full pipe buffer can never block the child.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            var partialOutput = await ReadQuietlyAsync(outputTask).ConfigureAwait(false);
            var partialError = await ReadQuietlyAsync(errorTask).ConfigureAwait(false);
            return new ProcessResult(-1, partialOutput, partialError, TimedOut: true);
        }

        return new ProcessResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false), TimedOut: false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already exited or not killable; nothing more to do.
        }
    }

    private static async Task<string> ReadQuietlyAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "";
        }
    }
}
