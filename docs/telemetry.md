# Anonymous Usage Statistics (Telemetry)

ReStore can collect anonymous statistics about which features are used, whether they succeed and how long they take. This page lists everything that is recorded, what is never recorded, and every way to switch it off.

## Current state of this version

**No collection endpoint is configured in this version.** Events are only kept locally (bounded) and can be inspected with `restore telemetry preview`. Nothing leaves your PC. When a collection endpoint is added in a later release, it will receive exactly what `restore telemetry preview` shows, and this page will say so.

## What is never collected

File names, folder names, paths, file contents, backup group names, storage bucket/container/repository names, host names, user names, account names, passwords or keys, raw exception messages and stack traces. (Only if you opt in, a crash report carries a scrubbed message and method names; see [Crash reports](#crash-reports).) ReStore itself stores no IP address; what a future collection server logs about connections will be documented here before it is switched on.

This is enforced in code by an allow-list: every event and property below is the complete list, unknown properties are dropped, and every text value must come from a closed set shown here (or be a number, a yes/no value or a coarse bucket).

## Turning it off

Telemetry is **on by default**. You are told once: the first CLI run prints a notice on stderr (never on stdout, so `--json` output is unaffected; `--quiet` postpones it to the next normal run), and the GUI shows it in the first-run wizard, or, for existing users, as a one-time banner on the Dashboard. **Nothing is recorded in the run that shows the notice**; collection starts with the next run, so you can opt out first.

Any one of these turns it off:

| Method | Notes |
|---|---|
| `restore telemetry disable` | Writes `"telemetry": { "enabled": false }` to config.json and deletes queued events. `restore telemetry enable` turns it back on. |
| Settings > Application Behavior > "Share anonymous usage statistics" | GUI toggle for the same setting. |
| `"telemetry": { "enabled": false }` in config.json | See [Configuration](configuration.md#telemetry). |
| Environment variable `RESTORE_TELEMETRY_OPTOUT=1` (also `true`, `yes`) | Per process or per machine. |
| Environment variable `DO_NOT_TRACK=1` | The [Do Not Track convention](https://donottrack.sh/). |
| Environment variable `CI` set to `true` or `1` | Telemetry is automatically off in CI. |
| Development builds | Off unless `RESTORE_TELEMETRY_DEV=1`. |

`restore telemetry status` shows whether it is on and, when it is off, why.

## Local files

All under the ReStore data folder (`%USERPROFILE%\ReStore` by default), in `state\`:

- `telemetry.json`: a random install id (a GUID unrelated to your repository or Windows identity; `restore telemetry reset-id` replaces it), when and where the notice was shown, and the upload backoff.
- `telemetry-queue.jsonl`: queued events, one JSON object per line, at most 2000 events or 1 MB (the oldest are dropped). `restore telemetry clear` deletes it.

## Events

Every event carries these common properties: `name`, `ts` (UTC), `installId`, `sessionId` (random per process), `appVersion`, `channel` (`msix`, `portable`, `dev`), `arch` (`x64`, `arm64`, ...), `osVersion` (for example `10.0.26100`), `surface` (`cli`, `gui`, `service`), `interactive` (true/false), and `agent` when the process was started by a known AI tool or editor (from `RESTORE_AGENT`, `CLAUDECODE`, `CURSOR_TRACE_ID`, `TERM_PROGRAM=vscode`, `GEMINI_CLI`, or a `CODEX_*` variable; a custom `RESTORE_AGENT` value is reduced to lowercase letters, digits and `-`, 32 characters at most). Event-specific properties are under `props`.

| Event | When | Properties (closed sets) |
|---|---|---|
| `cli.command` | A CLI command finished | `command` (a command name of the CLI reference), `exitCode` (0-255), `durationBucket`, `json` (true/false), `flags` (the option **names** used, sorted, such as `--json,--yes`; never their values) |
| `app.start` | The GUI started | `startMode` (`normal`, `tray`, `startup`) |
| `service.start` | `restore --service` started | none |
| `activity` | A backup, restore, verify, test restore, system backup, system restore or repair finished (GUI, CLI and scheduler) | `type` (`Restore`, `Verify`, `SystemBackup`, `SystemRestore`, `Repair`, `Backup`), `result` (`Succeeded`, `Warning`, `Failed`, `Cancelled`), `durationBucket`; for backups also `storageType` (`local`, `gdrive`, `s3`, `github`, `dropbox`, `sftp`, `other`), `fileCountBucket`, `bytesBucket`, `skippedCountBucket` |
| `error.unhandled` | The GUI crash handler or the CLI top-level error path ran | `surface`, `exceptionType` (the .NET type name, only for `System.*` and `ReStore.*` types; no message, no stack trace) |
| `crash.report` | A crash report was queued (see [Crash reports](#crash-reports)); only with `telemetry.crashReports` set to `always` or after you chose to send it | `id`, `exceptions` (scrubbed; see above) |
| `telemetry.notice` | Recorded by the first run after the notice was shown | none |

Not recorded in this version (not cheap to add cleanly): whether the repository is encrypted and the manifest version.

Buckets:

- Duration: `<1s`, `1-10s`, `10-60s`, `1-10m`, `10-60m`, `>1h`
- Counts: `0`, `1-10`, `11-100`, `101-1k`, `1k-10k`, `10k-100k`, `>100k`
- Bytes: `<1MB`, `1-100MB`, `100MB-1GB`, `1-10GB`, `10-100GB`, `>100GB`

Managing telemetry itself (`restore telemetry ...`) is not recorded.

## Inspecting what would be sent

```
restore telemetry preview          # the queued events, exactly as they would be sent
restore telemetry preview --json
```

The GUI shows the same data under Settings > Application Behavior > "Show collected data".

## Crash reports

When ReStore crashes you can choose to send an anonymous crash report along with the usage statistics. **Like telemetry, nothing leaves your PC in this version: no collection endpoint is configured**, so reports only wait locally.

### Settings

`telemetry.crashReports` in config.json (see [Configuration](configuration.md#telemetry)), also `restore telemetry crash-reports [ask|always|never]` and Settings > Application Behavior > "Crash reports":

| Value | What happens after a crash |
|---|---|
| `ask` (default) | The scrubbed report is kept on this PC and you are asked. GUI: a small non-blocking window "ReStore ran into a problem. Send an anonymous crash report?" with Send, Always send (sets `always`), Don't send (deletes the report) and View report, shown right after the error dialog and again on the next start while a report is pending. CLI: never prompts (scripts and agents); once on stderr it prints how to review and send the report. |
| `always` | The report is queued for sending together with the usage statistics. |
| `never` | No report is kept. (The local `crash-*.txt` file under `logs\` that ReStore has always written is unchanged and never sent.) |

Crash reports additionally need telemetry to be on: every way of switching telemetry off (config, `RESTORE_TELEMETRY_OPTOUT`, `DO_NOT_TRACK`, `CI`, development builds) also stops crash reports, and `restore telemetry disable` deletes the pending ones. At most 5 reports are recorded per process (a repeat of the same crash is recorded once) and at most 20 are kept pending (the oldest are dropped).

### What a crash report contains

One `crash.report` event with the common properties of every event (`name`, `ts`, `installId`, `sessionId`, `appVersion`, `channel`, `arch`, `osVersion`, `surface`; no `interactive`/`agent`) and these under `props`:

- `id`: random, 12 hex characters.
- `exceptions`: the exception and its inner exceptions (for an `AggregateException` its children), at most 5 levels deep and 10 in total, each with `depth`, `type` (.NET type name; `<external>` unless it is a `System.*`, `Microsoft.*`, `Windows.*`, `ReStore.*` or well-known SDK type such as `Amazon.*`, `Azure.*`, `Google.*`, `Dropbox.*`, `Octokit.*`, `Renci.*`), `hResult`, `message` (scrubbed, at most 500 characters), `frames` (at most 50, each only `Namespace.Type.Method`, no file paths, line numbers or argument values; frames of other namespaces become `<external>`) and `framesOmitted`.

Message scrubbing: secrets are masked first (the same redaction as the diagnostics report), then every URL, e-mail address, quoted value (`'...'`, `"..."`, except `(Parameter 'name')`), path of any kind (drive paths, UNC, `%VAR%\...`, `/home/...`, relative paths with backslashes; a path is removed to the end of its line because it may contain spaces), file name with a well-known extension, GUID, IP address and long base64/hex token is replaced by a placeholder (`<url>`, `<email>`, `<value>`, `<path>`, `<file>`, `<guid>`, `<ip>`, `<token>`), as are your Windows user name, the machine name and the storage, bucket, container, repository, owner, account, host, user and group names found in config.json. Like all scrubbing this is best effort for free text, which is why you can always read the exact report first.

### Reviewing, sending and deleting

```
restore telemetry crash-reports           # the setting
restore telemetry crashes list            # pending reports (ids)
restore telemetry crashes show <id>       # the exact JSON that would be sent
restore telemetry crashes send <id>       # or --all: move to the upload queue
restore telemetry crashes delete <id>     # or --all
```

The GUI lists pending reports on the About page (Send, View report, Delete). Pending reports are files `state\crash-reports\<id>.json`. Sending moves a report into the same queue as the usage events (`telemetry-queue.jsonl`, `restore telemetry preview` shows it); `restore telemetry status` shows the setting, the pending count and the queued crash report count.

### How crash reports are sent

Queued crash reports travel in the same batches (`{ "events": [ ... ] }`, same endpoint, no separate path), with the same 5 second timeout, budget and exponential backoff as other events, and are removed only after a 2xx response. Without an endpoint they stay queued (the queue is bounded) and nothing is sent.

## How sending will work

When an endpoint exists, queued events are POSTed as `{ "events": [ ... ] }` with a 5 second timeout and are removed from the queue only after a successful (2xx) response; failures back off exponentially (up to 6 hours). The CLI tries for at most 1.5 seconds at the end of a command; the GUI and the service every 30 minutes and at exit. Self-hosters and testers can point it at their own server with `RESTORE_TELEMETRY_ENDPOINT=https://...`.
