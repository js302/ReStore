using ReStore.Core;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;
using System.Text.Json.Nodes;

namespace ReStore.Core.src.cli;

/// <summary>What a command handler gets: the parsed arguments and whatever its spec asked the pipeline to prepare.</summary>
internal sealed class CliContext
{
    private readonly Lazy<Logger> _logger = new(() => new Logger());
    private const int MaxLoggedErrors = 21; // the first becomes the message, up to 20 more the details
    private readonly List<string> _errors = [];
    private JsonObject? _pendingDocument;
    private System.Text.Json.JsonSerializerOptions? _pendingOptions;
    private IConfigManager? _config;
    private SystemState? _state;
    private SizeAnalyzer? _sizeAnalyzer;

    public CliContext(Program.ParsedCommand parsed, CommandSpec spec, TextWriter jsonOut)
    {
        Parsed = parsed;
        Spec = spec;
        JsonOut = jsonOut;
    }

    public Program.ParsedCommand Parsed { get; }
    public CommandSpec Spec { get; }

    /// <summary>The real stdout: the only place a <c>--json</c> document (or config text) is written. Console.Out may be redirected.</summary>
    public TextWriter JsonOut { get; }

    /// <summary>Set by <c>run-due</c> so the pipeline can write <c>state/run-due-last.json</c> once the final exit code is known.</summary>
    public RunDueRecord? RunDueOutcome { get; set; }

    /// <summary>When the command started (UTC).</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>The CLI logger. Created on first use so commands that need no data folder (help, version) never create one.</summary>
    public Logger Logger => _logger.Value;

    /// <summary>The loaded configuration; only available when the spec's <see cref="CommandSpec.Config"/> is not <see cref="ConfigRequirement.None"/>.</summary>
    public IConfigManager Config
    {
        get => _config ?? throw new InvalidOperationException($"'{Spec.Name}' does not load the configuration.");
        set => _config = value;
    }

    /// <summary>The system state; only available when the spec's <see cref="CommandSpec.State"/> is not <see cref="StateRequirement.None"/>.</summary>
    public SystemState State
    {
        get => _state ?? throw new InvalidOperationException($"'{Spec.Name}' does not load the system state.");
        set => _state = value;
    }

    public SizeAnalyzer SizeAnalyzer
    {
        get => _sizeAnalyzer ??= new SizeAnalyzer();
        set => _sizeAnalyzer = value;
    }

    /// <summary>Cancelled by Ctrl+C for commands that load the state.</summary>
    public CancellationToken Cancellation { get; set; }

    public string? StorageOverride => Parsed.GetOption("--storage");

    public bool Json => Parsed.Json;

    /// <summary>True once a JSON document was written; the pipeline then does not add its generic error document.</summary>
    public bool JsonWritten { get; private set; }

    /// <summary>Error recorded by the pipeline (exception mapped to an exit code), preferred over logged error lines for the JSON error.</summary>
    public CliError? Failure { get; set; }

    /// <summary>Error lines logged so far (redacted), oldest first.</summary>
    public IReadOnlyList<string> LoggedErrors => _errors;

    public void RecordLoggedError(string message)
    {
        lock (_errors)
        {
            // A multi-day --service run with a recurring error must not grow this list without bound.
            if (_errors.Count < MaxLoggedErrors)
            {
                _errors.Add(message);
            }
        }
    }

    /// <summary>
    /// While true, <see cref="WriteJsonAsync"/> keeps the document back until <see cref="FlushJsonAsync"/> so the pipeline can
    /// still change it when the final state save fails (a document never says <c>ok: true</c> for a run that exits non-zero).
    /// </summary>
    public bool BufferJson { get; set; }

    /// <summary>Writes the buffered document, if any. <paramref name="stateSaveFailure"/> turns it into a failure with <c>stateSaved: false</c>.</summary>
    public async Task FlushJsonAsync(int? stateSaveExitCode = null, CliError? stateSaveFailure = null)
    {
        BufferJson = false;
        if (_pendingDocument is not { } node)
        {
            return;
        }

        _pendingDocument = null;
        if (stateSaveExitCode is { } exitCode && stateSaveFailure != null)
        {
            CliJson.MarkStateNotSaved(node, exitCode, Redact(stateSaveFailure));
        }

        await JsonOut.WriteLineAsync(node.ToJsonString(_pendingOptions ?? CliJson.Options)).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the JSON document of a command to stdout. <paramref name="exitCode"/> decides <c>ok</c>; a failing document
    /// also gets <c>exitCode</c> and a generic <c>error</c> unless the handler supplied its own.
    /// </summary>
    public async Task WriteJsonAsync(object document, int exitCode = Program.ExitCodes.Success, System.Text.Json.JsonSerializerOptions? options = null)
    {
        JsonWritten = true;
        var effective = options ?? CliJson.Options;
        var node = CliJson.BuildNode(document, exitCode, exitCode == 0 ? null : BuildError(exitCode), effective);
        if (BufferJson)
        {
            _pendingDocument = node;
            _pendingOptions = effective;
            return;
        }

        await JsonOut.WriteLineAsync(node.ToJsonString(effective)).ConfigureAwait(false);
    }

    /// <summary>Paths, tokens and similar in an error shown to the caller are redacted like the log lines are.</summary>
    internal static CliError Redact(CliError error) => error with
    {
        Message = DiagnosticsReport.RedactText(error.Message),
        Hint = error.Hint == null ? null : DiagnosticsReport.RedactText(error.Hint),
        Details = error.Details == null ? null : [.. error.Details.Select(DiagnosticsReport.RedactText)]
    };

    /// <summary>The JSON error object for a failed command: the pipeline's error if any, else the first logged error line.</summary>
    public CliError BuildError(int exitCode)
    {
        if (Failure != null)
        {
            return Redact(Failure);
        }

        var code = CliErrorCodes.ForExitCode(exitCode);
        string[] errors;
        lock (_errors)
        {
            errors = [.. _errors];
        }

        var message = errors.Length > 0 ? errors[0] : $"The command failed with exit code {exitCode}.";
        string? hint = exitCode switch
        {
            Program.ExitCodes.Busy => CliErrorCodes.BusyHint,
            Program.ExitCodes.NeedsUser => CliRuntime.PasswordUnavailableMessage != null ? CliErrorCodes.PasswordHint : null,
            Program.ExitCodes.Usage => $"run 'restore.exe help {Spec.Name}' for the usage",
            Program.ExitCodes.Config => "run 'restore.exe config validate' to see what is wrong",
            _ => null
        };
        return Redact(new CliError(code, message, hint, errors.Length > 1 ? [.. errors.Skip(1).Take(20)] : null));
    }
}
