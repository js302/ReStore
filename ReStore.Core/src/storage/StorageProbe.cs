using System.Diagnostics;
using System.Text;

namespace ReStore.Core.src.storage;

/// <summary>Outcome of one probe step.</summary>
/// <param name="Name">Step name (upload, size, download, list, delete, verify-deleted).</param>
/// <param name="Passed">True when the step succeeded.</param>
/// <param name="Skipped">True when the step did not apply (e.g. listing unsupported); a skipped step counts as passed.</param>
/// <param name="ElapsedMs">Step latency in milliseconds.</param>
/// <param name="Error">Short error description when the step failed.</param>
public sealed record StorageProbeStep(string Name, bool Passed, bool Skipped, long ElapsedMs, string? Error);

/// <summary>Result of <see cref="StorageProbe.RunAsync"/>.</summary>
public sealed record StorageProbeResult(string Key, IReadOnlyList<StorageProbeStep> Steps, bool CleanedUp)
{
    public bool Success => Steps.All(step => step.Passed);

    /// <summary>One line per step, e.g. for the Settings dialog or the CLI.</summary>
    public string ToDisplayText()
    {
        var builder = new StringBuilder();
        foreach (var step in Steps)
        {
            var state = step.Skipped ? "skipped" : step.Passed ? "ok" : "FAILED";
            builder.Append(step.Name).Append(": ").Append(state);
            if (!step.Skipped)
            {
                builder.Append(" (").Append(step.ElapsedMs).Append(" ms)");
            }

            if (step.Error != null)
            {
                builder.Append(" - ").Append(step.Error);
            }

            builder.AppendLine();
        }

        if (!CleanedUp)
        {
            builder.AppendLine($"Warning: the probe object '{Key}' could not be removed; delete it manually.");
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// A real round trip against a storage: upload a small payload, read its size, download and compare it, find it in
/// a listing, delete it and confirm it is gone. Uses a unique <c>restore-probe/&lt;guid&gt;</c> key (the provider
/// applies its configured <c>prefix</c>) and always tries to clean up.
/// </summary>
public static class StorageProbe
{
    public const string KeyDirectory = "restore-probe";

    public static async Task<StorageProbeResult> RunAsync(IStorage storage, CancellationToken ct = default)
    {
        var key = $"{KeyDirectory}/{Guid.NewGuid():N}";
        var payload = Encoding.UTF8.GetBytes($"ReStore probe {DateTime.UtcNow:O}");
        var steps = new List<StorageProbeStep>();
        var uploadAttempted = false;
        var deleted = false;
        var cleanedUp = false;

        var tempFile = Path.Combine(Path.GetTempPath(), $"restore-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            uploadAttempted = true;
            await StepAsync(steps, "upload", async () =>
            {
                using var stream = new MemoryStream(payload);
                await storage.UploadAsync(stream, key, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await StepAsync(steps, "size", async () =>
            {
                var size = await storage.GetSizeAsync(key, ct).ConfigureAwait(false);
                if (size != payload.Length)
                {
                    throw new InvalidOperationException(size == null
                        ? "the object was not found after upload"
                        : $"size mismatch: expected {payload.Length} bytes, got {size}");
                }
            }).ConfigureAwait(false);

            await StepAsync(steps, "download", async () =>
            {
                await storage.DownloadAsync(key, tempFile, ct).ConfigureAwait(false);
                var downloaded = await File.ReadAllBytesAsync(tempFile, ct).ConfigureAwait(false);
                if (!downloaded.AsSpan().SequenceEqual(payload))
                {
                    throw new InvalidOperationException("downloaded content differs from what was uploaded");
                }
            }).ConfigureAwait(false);

            if (!storage.SupportsListing)
            {
                steps.Add(new StorageProbeStep("list", true, true, 0, "listing is not supported by this storage"));
            }
            else
            {
                await StepAsync(steps, "list", async () =>
                {
                    await foreach (var entry in storage.ListAsync(KeyDirectory + "/", ct).ConfigureAwait(false))
                    {
                        if (string.Equals(entry.Path, key, StringComparison.Ordinal))
                        {
                            return;
                        }
                    }

                    throw new InvalidOperationException("the uploaded object was not returned by the listing");
                }).ConfigureAwait(false);
            }

            await StepAsync(steps, "delete", async () =>
            {
                await storage.DeleteAsync(key, ct).ConfigureAwait(false);
                deleted = true;
            }).ConfigureAwait(false);

            await StepAsync(steps, "verify-deleted", async () =>
            {
                if (await storage.ExistsAsync(key, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("the object still exists after delete");
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteLocal(tempFile);
            cleanedUp = deleted && steps.Any(s => s.Name == "verify-deleted" && s.Passed);
            if (uploadAttempted && !cleanedUp)
            {
                // Best effort, outside the caller's cancellation so an abort still removes the probe object.
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await storage.DeleteAsync(key, cleanup.Token).ConfigureAwait(false);
                    cleanedUp = !await storage.ExistsAsync(key, cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    cleanedUp = false;
                }
            }
        }

        return new StorageProbeResult(key, steps, cleanedUp);
    }

    private static async Task StepAsync(List<StorageProbeStep> steps, string name, Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await action().ConfigureAwait(false);
            steps.Add(new StorageProbeStep(name, true, false, stopwatch.ElapsedMilliseconds, null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            steps.Add(new StorageProbeStep(name, false, false, stopwatch.ElapsedMilliseconds, ex.Message));
        }
    }

    private static void TryDeleteLocal(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp file only; nothing to do.
        }
    }
}
