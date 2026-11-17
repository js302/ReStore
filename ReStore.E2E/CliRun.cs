using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReStore.E2E;

internal sealed record RunOptions
{
    /// <summary>Text written to the child's stdin; null writes nothing. The pipe is closed afterwards unless <see cref="KeepStdinOpen"/>.</summary>
    public string? Stdin { get; init; }

    /// <summary>Redirected stdin that is never written and never closed (a hung pipe, as an agent harness may leave it).</summary>
    public bool KeepStdinOpen { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Extra or overriding environment variables (null value removes one).</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Windows: create the child in its own process group so <see cref="RunningCli.Interrupt"/> (Ctrl+Break) works.</summary>
    public bool NewProcessGroup { get; init; }
}

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed, string CommandLine)
{
    public string Describe() =>
        $"$ {CommandLine}\nexit {ExitCode} after {Elapsed.TotalSeconds:F1}s\n--- stdout ---\n{Truncate(Stdout)}\n--- stderr ---\n{Truncate(Stderr)}";

    private static string Truncate(string text) => text.Length <= 6000 ? text : text[..3000] + "\n...\n" + text[^3000..];

    /// <summary>Stdout parsed as exactly one JSON document (trailing text of any kind is a failure).</summary>
    public JsonElement Json()
    {
        try
        {
            using var document = JsonDocument.Parse(Stdout);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new Xunit.Sdk.XunitException($"stdout is not exactly one JSON document ({ex.Message}).\n{Describe()}");
        }
    }

    /// <summary>Asserts exit 0 and <c>"ok": true</c> and returns the document.</summary>
    public JsonElement Ok()
    {
        var json = Json();
        Assert.True(ExitCode == 0 && json.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, "expected success.\n" + Describe());
        return json;
    }

    /// <summary>Asserts the documented failure envelope for <paramref name="exitCode"/> and returns <c>error</c>.</summary>
    public JsonElement Failed(int exitCode, string errorCode)
    {
        var json = Json();
        Assert.True(ExitCode == exitCode, $"expected exit {exitCode}.\n{Describe()}");
        Assert.True(json.GetProperty("ok").ValueKind == JsonValueKind.False, "ok must be false.\n" + Describe());
        Assert.True(json.GetProperty("exitCode").GetInt32() == exitCode, "exitCode in the document must equal the process exit code.\n" + Describe());
        var error = json.GetProperty("error");
        Assert.True(error.GetProperty("code").GetString() == errorCode, $"expected error.code '{errorCode}'.\n{Describe()}");
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()), "error.message must not be empty.\n" + Describe());
        Assert.True(error.TryGetProperty("hint", out _), "error.hint must be present (it may be null).\n" + Describe());
        return error;
    }
}

/// <summary>A started CLI process with captured output.</summary>
internal sealed class RunningCli : IDisposable
{
    private readonly ChildProcess _child;
    private readonly Capture _stdout;
    private readonly Capture _stderr;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _timeout;

    internal RunningCli(ChildProcess child, string commandLine, TimeSpan timeout)
    {
        _child = child;
        CommandLine = commandLine;
        _timeout = timeout;
        _stdout = new Capture(child.StandardOutput);
        _stderr = new Capture(child.StandardError);
    }

    public string CommandLine { get; }

    public int Id => _child.Id;

    public string StderrSoFar => _stderr.Text;

    public bool HasExited => _child.WaitForExit(0);

    /// <summary>Like <see cref="WaitForStderr"/>, but returns false (instead of failing) when the process exits before stderr matches.</summary>
    public bool TryWaitForStderr(string pattern, TimeSpan? timeout = null)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        var limit = timeout ?? TimeSpan.FromSeconds(60);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed <= limit)
        {
            if (regex.IsMatch(_stderr.Text))
            {
                return true;
            }

            if (HasExited)
            {
                _stderr.Drain();
                return regex.IsMatch(_stderr.Text);
            }

            Thread.Sleep(10);
        }

        KillTree();
        throw new Xunit.Sdk.XunitException($"Timed out after {limit.TotalSeconds:F0}s waiting for stderr to match /{pattern}/.\n{Snapshot().Describe()}");
    }

    /// <summary>Waits until stderr matches <paramref name="pattern"/>; fails if the process exits first or the time runs out.</summary>
    public Match WaitForStderr(string pattern, TimeSpan? timeout = null)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(60);
        while (true)
        {
            var match = regex.Match(_stderr.Text);
            if (match.Success)
            {
                return match;
            }

            if (HasExited)
            {
                // Pump threads may still hold the last bytes.
                _stderr.Drain();
                match = regex.Match(_stderr.Text);
                if (match.Success)
                {
                    return match;
                }

                throw new Xunit.Sdk.XunitException($"The process exited before stderr matched /{pattern}/.\n{Snapshot().Describe()}");
            }

            if (deadline.Elapsed > limit)
            {
                KillTree();
                throw new Xunit.Sdk.XunitException($"Timed out after {limit.TotalSeconds:F0}s waiting for stderr to match /{pattern}/.\n{Snapshot().Describe()}");
            }

            Thread.Sleep(10);
        }
    }

    public void Interrupt() => _child.Interrupt();

    public void KillTree() => _child.KillTree();

    public CliResult WaitForExit(TimeSpan? timeout = null)
    {
        var limit = timeout ?? _timeout;
        if (!_child.WaitForExit((int)Math.Min(limit.TotalMilliseconds, int.MaxValue)))
        {
            _child.KillTree();
            _child.WaitForExit(10_000);
            throw new Xunit.Sdk.XunitException($"Timed out after {limit.TotalSeconds:F0}s (killed).\n{Snapshot().Describe()}");
        }

        _stdout.Drain();
        _stderr.Drain();
        return Snapshot();
    }

    private CliResult Snapshot() => new(
        _child.WaitForExit(0) ? _child.ExitCode : -1, _stdout.Text, _stderr.Text, _clock.Elapsed, CommandLine);

    public void Dispose()
    {
        if (!_child.WaitForExit(0))
        {
            _child.KillTree();
            _child.WaitForExit(5_000);
        }

        _child.Dispose();
    }

    /// <summary>Reads a pipe on a background thread into a string that can be inspected while the process runs.</summary>
    private sealed class Capture
    {
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();
        private readonly Task _pump;

        public Capture(Stream stream)
        {
            _pump = Task.Run(() =>
            {
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, 4096);
                var buffer = new char[4096];
                int read;
                try
                {
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        lock (_gate)
                        {
                            _text.Append(buffer, 0, read);
                        }
                    }
                }
                catch (IOException)
                {
                    // Pipe closed under us (process killed).
                }
            });
        }

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _text.ToString();
                }
            }
        }

        /// <summary>Waits for end of stream; a grandchild that inherited the pipe could keep it open, so the wait is bounded.</summary>
        public void Drain() => _pump.Wait(TimeSpan.FromSeconds(10));
    }
}
