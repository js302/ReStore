# CLI Usage

The `restore` command is automatically available system-wide after installing ReStore through the MSIX package.

**To disable the CLI usage** (if not needed):

1. Open Windows **Settings** → **Apps** → **Advanced app settings** → **App execution aliases**
2. Find **restore.exe** in the list
3. Toggle it **Off**

## Discovering the CLI (for scripts and AI agents)

The CLI describes itself, so a tool can learn it without reading this page:

```bash
restore help                    # every command by category, global options, exit codes
restore help restore            # one command: usage, arguments, options, examples, properties
restore help config import      # multi-word commands work too
restore backup --help           # same as `restore help backup` (also -h); needs no other arguments
restore commands                # the command list as plain text
restore commands --json         # the whole reference as one JSON document
restore --version               # same as `restore version`
restore doctor --json           # health check: what is wrong with this setup, and the fix
restore agent-guide             # the guide for AI agents (rules, exit codes, recipes)
restore version --json
```

`commands --json` has `schemaVersion` (1), `version`, `globalOptions`, `exitCodes` (`code`, `name`, `meaning`) and
`commands`. Each command has `name` (for example `config show`), `aliases`, `category`, `summary`, `description`, `usage`,
`positionals`, `options` (`name`, `takesValue`, `valueName`, `repeatable`, `allowedValues`, `description`), `examples`,
`supportsJson`, `mutates` (changes the repository, the configuration or this machine), `destructive` (deletes or overwrites
data), `windowsOnly` and `needs` (`config`: `none`, `loaded`, `storage-validation` or `full-validation`; `systemState`).
Help, `version` and `commands` work without a valid (or any) configuration and never create the data folder.

`restore version` prints (and `--json` returns as `version`, `informationalVersion`, `channel`, `architecture`, `os`,
`runtime`, `dataRoot`, `configFile`, `executable`) the version, the install channel and the locations ReStore uses. The channel is
`msix` when running with package identity, `portable` when a `ReStore.channel` file containing `portable` sits next to the
executable, and `dev` otherwise.

## Exit Codes and Options

| Code | Name (`error.code`) | Meaning |
|------|------|---------|
| `0` | | Success (also `help`, `version`, `commands`) |
| `1` | `failure` | The operation failed (failed verification or system restore component, backup error). `error.code` is `not_found` instead of `failure` when the snapshot or file the command was pointed at is not in the storage (for example `verify nothing/HEAD`); the exit code stays `1` |
| `2` | `usage` | Usage error: unknown command or option, missing option value, wrong number of arguments, invalid `--conflict` value or component name, `--json` on a command that cannot produce it |
| `3` | `config` | Configuration error: `config.json` cannot be loaded or fails validation |
| `4` | `needs_user` | Only a person can continue: the encryption password is not available without a prompt, an interactive sign-in is required (for example Google Drive consent), elevation or `--yes` is required. `run-due` and `--service` exit `4` when the repository is encrypted and no password is available without a prompt (hint: `unlock --remember` or the environment variable) |
| `5` | `busy` | Another ReStore process (the app, the service, another CLI) holds the repository and `--wait` ran out. Retry, or pass `--wait <seconds>` |
| `130` | `cancelled` | Cancelled with `Ctrl+C` |

Treat `4` as "ask the user" and `5` as "try again later"; `1` is a real failure. Codes are only ever added, never renumbered.

### Global options

These work with every command, before or after it (option names are case-insensitive):

| Option | Meaning |
|--------|---------|
| `--json` | Machine-readable output, see [JSON output](#json-output). A command that cannot produce JSON (`--service`) rejects it with exit `2` |
| `--quiet` | Nothing is written to stdout except `--json` documents; only errors go to stderr (the log file is unchanged). `help`, `version` and `commands` are the answer you asked for, so they ignore `--quiet`; so does the one-time key of `recovery-key` |
| `--non-interactive` | Never prompt, see [Non-interactive use](#non-interactive-use) |
| `--yes` | Confirms destructive actions in advance. It is parsed everywhere, but only `retention apply`, `repair`, `restore --conflict overwrite`, `system-restore`, `update install` and `encryption disable` ask: interactively they print a `y/N` question on stderr (declining exits `1`, nothing changed); non-interactively they need `--yes` and exit `4` without it, changing nothing. `--dry-run` / `--plan` previews never ask. Other commands never ask, however destructive (`schedule remove`, `system-undo`, `config import` run at once) |
| `--wait <seconds>` | How long to wait for another ReStore process that holds the repository. Default `30`; `0` fails at once with exit `5`. While waiting, one line on stderr says what is being waited for |
| `--password-stdin` | Read the encryption password from the first line of stdin |
| `--help`, `-h`, `/?` | Show the help of the command. With `--json` (`restore backup --help --json`, `restore help backup --json`) it is a JSON document: the same object as that command's entry in `restore commands --json` (a group such as `config` gives `group` and `commands`; plain `help --json` is the whole reference) |

Option names are case-insensitive and may appear before or after the positional arguments.
`system-backup` and `system-restore` are Windows-only and exit with `1` elsewhere. `restore --include` patterns
that match nothing exit with `1` and restore nothing.

Unknown options are never ignored. A mistyped option such as `--dryrun` is rejected with exit code `2`
and a hint (`Did you mean '--dry-run'?`) instead of silently running the real operation. A usage error prints the usage of the
command (or the list of subcommands of a group such as `config`) on stdout and the message on stderr.

`restore`, `verify`, `test-restore`, `status`, `system-restore`, `ls`, `versions` and `diff` only validate the storage they use, so an unrelated problem in
the config (for example a missing watch directory) does not block a restore. `backup <folder>` validates the whole configuration
except the watch directories (the folder comes from the command line, so `watchDirectories` may be empty) plus the storage that
folder uses. `system-backup`, `--service` and `config validate` (`--validate-config`) validate the whole configuration.
Only validation errors and warnings are printed by these commands; `config validate` also prints the informational messages.
Read-only commands only write `system_state.json` when they changed telemetry (a `--dry-run` or a failed lookup writes nothing).

### JSON output

With `--json` a command writes exactly one JSON document to stdout. Log lines and progress go to stderr instead, so stdout can be
parsed as is. Every document has `"ok": true|false` as its first property; further properties are added over time, never removed.
Every command except `--service` supports `--json` (`help` and `commands` give the command reference): `backup`, `restore` (also `--dry-run`, with the preview rows),
`verify`, `test-restore`, `snapshots`, `ls`, `versions`, `diff`, `adopt`, `repair`, `retention` (preview and `apply`), `pause`, `resume`, `activity`, `usage`,
`upgrade-format`, `schedule install|remove|status`, `diagnostics`, `share`, `logs`, `change-password`,
`recovery-key`, `export-key`, `import-key`, `system-backup`, `system-restore` (also `--plan`, with the plan rows),
`system-undo` (also `--list` and `--delete`), `config show|protect|export|import|validate|path|get|set|unset|add-watch|remove-watch|list-watch|set-storage|schema`,
`init`, `encryption status|enable|disable`, `unlock`, `lock`, `auth status|gdrive|dropbox`, `probe`, `status`, `run-due`,
`version` and `commands`. Run `restore help <command>` for what each does; the documents carry the data the human output prints
(counts, snapshot id and manifest path, skipped files, verification findings, plan rows, the recovery key, ...).

Whenever a command fails with `--json` (a usage error, a config error, a busy repository, a missing password, a plain failure,
`Ctrl+C`) stdout gets this document and stderr the human message:

```json
{"ok": false, "exitCode": 5, "error": {"code": "busy", "message": "Another ReStore process ...", "hint": "another ReStore process is using the repository; retry or pass --wait <seconds>"}}
```

`error.code` is one of `usage`, `config`, `failure`, `not_found`, `busy`, `needs_user`, `cancelled`; `error.details` may list further messages
(for example the config errors). `error.message`, `error.hint` and `error.details` are redacted like the log lines (secrets are masked).
A command that has its own result document for failures (`verify` findings, `run-due`, `probe`, `test-restore`, a failed `config import`)
writes that document with `ok: false`, `exitCode` and the same `error` object (a field of another meaning is never called `error`: the reason of a
failed `test-restore` is `reason`). Unexpected errors never end as a crash: they are exit `1` with this document.

A command that changes the saved state (`backup`, `adopt`, `repair`, `run-due`, ...) prints its document only after the final state save. If
that save finds the repository busy the run exits `5` and the document says `"ok": false`, `"stateSaved": false` and a `busy` error; if it fails
for another reason the exit code is `1` with `"stateSaved": false`. The document never says `ok: true` for a run that exits non-zero.

The documents are written with relaxed escaping (`'`, `<`, `>` and `&` appear as they are, not as `\u0027`); they are meant for terminals and pipes, not for embedding in HTML.

### Non-interactive use

A command never waits for a prompt in these cases, which make the CLI non-interactive: `--non-interactive`, redirected stdin,
the environment variable `RESTORE_NON_INTERACTIVE=1`, or `CI=true` (or `1`). In that mode sign-in flows that would open a
browser fail fast instead, and a password that is needed but not available exits with `4` and a hint naming the sources below.
`--service` and `run-due` always behave this way.

Where the encryption password comes from, first match wins:

1. `--password-stdin`: the first line of stdin (for an agent that pipes a secret: `echo ... | restore backup D:\Data --password-stdin`)
2. the environment variable `RESTORE_ENCRYPTION_PASSWORD`
3. the file named by `RESTORE_ENCRYPTION_PASSWORD_FILE`: its first line is the password
4. the password remembered by `restore unlock --remember` (Windows, see [Remembering the password](#remembering-the-password)); it is only used when it still matches the configured password check
5. an interactive masked prompt, written to stderr, only when the CLI is interactive

`change-password` additionally reads the new password from `RESTORE_NEW_ENCRYPTION_PASSWORD` (it prompts twice when interactive, and
exits `4` when it cannot ask).

### Text encoding when redirected

When stdin, stdout or stderr is redirected (a pipe or a file), ReStore reads and writes it as UTF-8 without a byte-order mark on
Windows, instead of the console's legacy code page. So non-ASCII file names in `--json` output are exact, and a non-ASCII password
piped to `--password-stdin` is read as the UTF-8 bytes it was sent as. An interactive console is left alone, and ReStore never changes
the console code page. The shell that does the piping must send UTF-8 too: Windows PowerShell 5.1 encodes what it pipes to a program
with `$OutputEncoding` (ASCII by default), so set it first when the password is not plain ASCII:

```powershell
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
"pässwörd-日本" | restore unlock --password-stdin
```

PowerShell 7 already uses UTF-8. In `cmd.exe`, `echo` writes the console's code page, so prefer `RESTORE_ENCRYPTION_PASSWORD_FILE` (a UTF-8 file) there.

## Setting up and configuring from the command line

Everything the Settings page does can be done from the CLI, so an agent (or a script) can set ReStore up from zero without
editing `config.json` by hand or opening the app. All of these commands support `--json`; every command that changes the
configuration validates the **resulting** configuration first. An invalid result exits `3` with the errors and writes nothing;
otherwise the previous `config.json` is kept under `backups/` (`config.pre-change.<time>.json`, like `config import`). Errors the
config already had before the change do not block an unrelated change. Unknown keys, `env:` and `dpapi:` values, `%VAR%` paths and
everything else in the file are kept exactly as they are.

The edited document is also checked against the config schema (`restore config schema`): the type, allowed values and minimum/maximum of
every key. A value the loader would silently reset to its default (`retention.keepWeekly abc`, `telemetry.crashReports maybe`, a negative
number) is refused with exit `3`, the key and the broken rule named, and the file unchanged. `--force` only lets `config set` write a key
the schema does not know.

**ReStore keeps running while you do this.** The tray app and `--service` watch `config.json`: an edit made by the CLI is picked up within a
second or so (the app reloads silently, or, when Settings has unsaved edits, offers "Reload / Keep my edits"; protection restarts only when
something it binds to changed). If both the CLI and the app change the same setting, the newer change wins; settings changed by only one of
them are all kept. Old copies in `backups/` are limited to the newest 20, and `config protect` (or `--protect`) also protects the secrets in them.

| Command | What it does |
|---------|--------------|
| `config path` | Prints the path of `config.json` (`--json`: `configFile`, `dataRoot`, `exists`) |
| `config get [<key>] [--reveal]` | Prints one setting or the whole config. Keys are dotted: `retention.keepDaily`, `storageSources.s3.options.bucketName`, `watchDirectories.0.path`. Secret option values are masked like in `config show` unless `--reveal` |
| `config set <key> <value>` / `config set <key> --value-stdin` | Sets a setting. The value is JSON when it parses as JSON (`7`, `false`, `[...]`, `{...}`, `"text"`), otherwise a plain string. `--value-stdin` reads it from stdin so a secret never appears in process arguments or an agent transcript. `--protect` stores a secret provider option as a `dpapi:` value (Windows). Unknown keys are refused unless `--force` |
| `config unset <key>` | Removes a setting so its default applies. A key that is not there is not an error (`changed: false`) |
| `config add-watch <dir> [--storage <type>]` | Adds a folder (stored as a full path; `%USERPROFILE%\Documents` style values are kept as written). Duplicates and folders that do not exist are refused (`--force` allows a missing folder). A folder inside an `excludedPaths` entry (the defaults include `%TEMP%`) or matched by an exclusion glob would back up nothing: it is added, but a warning (JSON `warnings`, and on stderr) names the rule |
| `config remove-watch <dir>` | Removes a folder; its snapshots stay in the storage. The last folder cannot be removed (exit `3`) |
| `config list-watch` | Lists the folders with `path`, `expandedPath`, `storageType`, `schedule` and `exists` |
| `config set-schedule <dir\|system> "<expression>"` | Gives a folder (or the system backup) a clock schedule such as `"daily 02:00"`, `"weekdays 18:30"`, `"cron 0 */6 * * *"` or `off`; see [Backup schedules](#backup-schedules-and-the-backup-window). The expression is checked first (exit `2` with the reason) and stored in its normalized form |
| `config clear-schedule <dir\|system>` | Removes that schedule so `backupInterval` (or `systemBackup.backupInterval`) applies again. Nothing to remove is not an error (`existed: false`) |
| `config set-window <start> <end> [--days <days>]` | Only start automatic work between `<start>` and `<end>` (local `HH:mm`, may cross midnight, equal times mean all day); `--days` is `all` (default), `weekdays`, `weekends` or `mon,wed,fri` / `mon-fri` |
| `config clear-window` | Removes the backup window |
| `config set-storage <type> [--path <p>] [--option name=value]... [--option-stdin name]... [--default] [--protect]` | Configures a storage provider: `local`, `s3`, `gdrive`, `dropbox`, `github`, `azure`, `gcp`, `sftp` or `b2`. Unknown types and options are refused, and so is a result that still lacks a required option. `--path` is only for `local` (remote types use `--option prefix=<folder>`). `--option-stdin name` takes the value from the next line of stdin (once per option, in the order given). `--default` also makes it the default storage. Afterwards run `restore probe --storage <type>`; `gdrive` and `dropbox` also need `auth` |
| `schedule preview "<expression>" [--count N]` | Parses a schedule expression and prints what it means and its next N (default 5) local run times, changing nothing. Invalid input exits `2` with the reason |
| `schedule next` | Per folder, the system backup, verification and retention: schedule, last run, next start and why something is not running now (window closed, paused, repository busy) |
| `config schema` | Prints the JSON Schema (draft 2020-12) of `config.json` with a description of every key |
| `init` | First-time setup in one command, see below |
| `encryption status`, `encryption enable`, `encryption disable` | See [Encryption](#encryption-from-the-command-line) |
| `unlock`, `lock` | See [Remembering the password](#remembering-the-password) |
| `auth status`, `auth gdrive`, `auth dropbox` | See [Signing in to OAuth providers](#signing-in-to-oauth-providers) |

Option values that start with `--` cannot be passed as arguments (they would be read as options); use `--value-stdin` for those.

### `init`

```bash
restore init --storage <type> [--path <p>] [--option name=value]... [--option-stdin name]... \
             [--watch <dir>]... [--encrypt] [--probe] [--protect] [--force]
```

Creates `config.json` if needed, configures the storage as `config set-storage ... --default` does, replaces the default watched
folders with the `--watch` folders (without `--watch` the default folders stay), optionally turns on encryption (`--encrypt`, password
from the [sources above](#non-interactive-use); asked for twice in a terminal; without a source exit `4` and nothing is written) and
optionally runs the [storage probe](#storage-probe) (`--probe`: exit `1` when it fails, `4` when the storage needs a sign-in). The
whole resulting configuration must be valid (exit `3` otherwise).

If the configuration is already customised (other watched folders than the defaults, another default storage, a configured
storage, encryption on) `init` refuses with exit `2` unless `--force` is given, so an agent cannot clobber a working setup.
The JSON document has `configFile`, `storage`, `watchDirectories`, `encryption`, `probe`, `backup`, `warnings`, `notes` and
`nextSteps` (`restore backup <folder>`, `restore schedule install`, `restore unlock --remember` when encrypted). A `--watch` folder inside an excluded path
(`excludedPaths`, for example the default `%TEMP%`) or matched by an exclusion glob would back up nothing, so `init` adds a warning that names the rule
(JSON `warnings`, and on stderr with `--json`) and a next step that shows the fix: `restore config get excludedPaths`, then `restore config set excludedPaths "[]"` (or a shorter list).

If both `--option-stdin` and `--password-stdin` are used, stdin holds the option values first (one line each, in the order of the options), then the password.

### Example: a complete agent-driven setup

Local storage:

```bash
restore init --storage local --path D:\Backups --watch %USERPROFILE%\Documents --watch D:\Photos --probe --json
restore backup D:\Photos --json
restore schedule install
```

Amazon S3 (the secret key arrives on stdin and is stored as a DPAPI value; nothing secret is on the command line):

```bash
echo %AWS_SECRET% | restore init --storage s3 --option region=eu-west-1 --option bucketName=my-backups ^
    --option accessKeyId=AKIA... --option-stdin secretAccessKey --protect --watch D:\Data --probe --json
```

Encrypted (the password comes from the first stdin line, after the option lines if there are any):

```bash
echo %BACKUP_PASSWORD% | restore init --storage local --path D:\Backups --watch D:\Data --encrypt --password-stdin --json
restore unlock --remember --password-stdin   # so scheduled runs need no environment variable (Windows)
restore backup D:\Data
restore export-key D:\key-copy\repository.keys.json   # keep an offline copy of the key file after the first backup
```

Changing settings afterwards:

```bash
restore config set retention.keepDaily 7
restore config set backupInterval 02:00:00
restore config set storageSources.s3.options.bucketName other-bucket
restore config add-watch D:\More --storage s3
restore config get retention --json
restore config schema > config.schema.json    # every key with a description
```

### Encryption from the command line

```bash
restore encryption status [--json]
restore encryption enable            # password: --password-stdin, RESTORE_ENCRYPTION_PASSWORD, ..._FILE, or a prompt
restore encryption disable --yes
```

`encryption status` reports `enabled`, `keyDerivationIterations`, `saltPresent`, `verificationTokenPresent` and
`rememberedPasswordPresent`. `encryption enable` applies the same rules as the app: at least 8 characters (exit `2` otherwise), a
weak password is accepted with a warning, and it refuses when encryption is already on (use `change-password`). Store the password
safely: it cannot be recovered. After the first backup keep an offline copy of the key file (`restore export-key`).
`encryption disable` needs `--yes` when there is no terminal (exit `4` otherwise); only new backups become unencrypted, existing
encrypted backups still need the password.

### Remembering the password

```bash
restore unlock [--remember]    # check the password; with --remember keep it for later runs (Windows)
restore lock                   # forget it
```

`unlock` reads the password like any command (exit `4` when none is available, exit `1` when it is wrong). `--remember` stores it
protected with the current Windows user's DPAPI key in `state\remembered-password.bin`. From then on every CLI command, `run-due`
from Task Scheduler and `--service` find it (after `--password-stdin`, the environment variable and the password file) without
any environment variable. **Trade-off:** anyone who runs as this Windows user can use the remembered password, which is the same
trust model as `dpapi:` provider secrets in `config.json`; on a shared account prefer `RESTORE_ENCRYPTION_PASSWORD_FILE` with a
file only you can read, or do not remember. A remembered password that no longer matches the configuration (the password was
changed) is ignored with a warning; run `unlock --remember` again. `change-password` updates it when one is stored, and
`encryption enable` removes a stale one.

### Signing in to OAuth providers

```bash
restore auth status [--json]
restore auth gdrive [--no-browser]
restore auth dropbox                  # step 1: prints the authorize URL
restore auth dropbox --code <code>    # step 2: exchanges the code for a refresh token
```

`auth status` lists, for `gdrive` and `dropbox`, whether the storage is configured and whether a token is present (it opens no
browser and calls no server).

`auth gdrive` runs the same one-off sign-in as the app for the `gdrive` storage (needs `client_id` and `client_secret`) and stores the token
where the storage looks for it. It opens a browser; with `--no-browser` it prints the URL on stderr for a person to open on this computer.
Without a terminal (non-interactive) it exits `4` with instructions unless `--no-browser` is given. The sign-in waits 5 minutes.

`auth dropbox` is made for agents: step 1 needs `appKey` in the `dropbox` storage, prints an authorize URL (authorization code
flow with PKCE, offline access, no redirect URI) and remembers the PKCE verifier in `state\dropbox-pkce.json`. A person opens the URL,
approves, and gives the code Dropbox shows to the agent. Step 2 (`--code`) exchanges it for a refresh token and writes it as
`refreshToken` (a `dpapi:` value on Windows) next to `appKey` and `appSecret`; the code is single-use and expires quickly.

## Start File Watcher

Monitor directories for changes and backup automatically:

```bash
restore --service
```

## Manual Backup

Backup a specific directory (uses configured storage from config.json):

```bash
restore backup "C:\Users\YourName\Documents"

# Or override storage type
restore backup "C:\Users\YourName\Documents" --storage gdrive
```

The backup pipeline writes chunked snapshot artifacts:

- Snapshot manifests under `snapshots/<group-key>/...manifest.json`
- Snapshot head pointer at `snapshots/<group-key>/HEAD`
- Deduplicated chunks under `chunks/<prefix>/<chunk-id>.chunk`

### Folders that would back up nothing

`backup <folder>` refuses a folder that lies inside an excluded path (`excludedPaths` in `config.json`; the defaults include
`%TEMP%` and `%LOCALAPPDATA%\Temp`) or whose own name matches an `excludeGlobs` / `excludePresets` pattern: previously it
exited `0` after backing up nothing. The command exits `3` and names the rule and the file to change:

```json
{"ok": false, "exitCode": 3, "error": {"code": "config", "message": "Not backing up 'C:\\Users\\me\\AppData\\Local\\Temp\\proj': it is excluded by the excludedPaths entry '%LOCALAPPDATA%\\Temp', so no file in it would be backed up.", "hint": "change or remove that rule in ... or pass --allow-excluded to back the folder up anyway"}}
```

`--allow-excluded` backs the folder up anyway: `excludedPaths` entries that contain the folder are ignored for this run (entries
inside it, patterns, hidden/system files and the size limit still apply). A run that finds no files at all (an empty folder, or
everything excluded) succeeds but warns; the `--json` document of `backup` has a `warnings` list (empty when there is nothing to report).

## Restore Files

Restore from a snapshot manifest or HEAD path:

```bash
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents"
restore restore "snapshots/documents_abcd1234ef567890/snapshot_20260101010101_abcdef.manifest.json" "C:\Restore\Documents"
```

Every restore prints a preview first — file count, total size, and which destination files
already exist and would be affected.

### Preview without writing anything

```bash
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --dry-run
```

### Overwrite protection

`--conflict` decides what happens to files that already exist at the destination. The
default is `skip`, which never modifies a file you already have.

```bash
# Keep existing files, restore only what is missing (default)
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --conflict skip

# Restore alongside as "name (restored).ext"
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --conflict keepboth

# Replace existing files (asks y/N when interactive: "Overwrite up to N existing file(s) in <target>?";
# scripts and agents pass --yes, without it they exit 4 and write nothing; preview with --dry-run)
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --conflict overwrite --yes

# Abort before writing anything if any file would be replaced
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --conflict fail
```

Restored files are written to a temporary sibling file and moved into place only after their
hash matches the manifest, so an interrupted restore never leaves a truncated file where
your real file was.

### Restore a subset

`--include` filters by manifest-relative path and may be repeated. `**` spans directory
separators, `*` and `?` do not.

```bash
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --include "reports/**"
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents" --include "reports/**" --include "*.docx"
```

### Cancellation

`Ctrl+C` cancels an in-flight backup, restore, or verify. A cancelled backup leaves `HEAD`
pointing at the previous snapshot, and a cancelled restore discards only the file being written (files already restored stay in place).

## Verify Snapshot Integrity

Verify manifest and chunk-store integrity without restoring files:

```bash
restore verify "snapshots/documents_abcd1234ef567890/HEAD"
restore verify "snapshots/documents_abcd1234ef567890/snapshot_20260101010101_abcdef.manifest.json" --storage s3
restore verify "snapshots/documents_abcd1234ef567890/HEAD" --mode quick
restore verify "snapshots/documents_abcd1234ef567890/HEAD" --mode sample --sample-percent 10
```

`--mode` chooses how thorough the check is (the default is `full`):

| Mode | What it checks | Downloads |
|------|----------------|-----------|
| `full` | Manifest root hash, every chunk's existence, decryption and content hash, and every reconstructed file's hash and size | Every unique chunk |
| `sample` | Manifest root hash, the existence and stored size of every chunk, plus a full check (download, decrypt, hash) of a random `--sample-percent` (default 5, 1 to 100) of the chunks | That percentage |
| `quick` | Manifest root hash, and the existence and recorded stored size of every chunk (one listing or one size lookup per chunk) | Nothing |

`quick` and `sample` are cheap enough to run often on egress-billed storage, but only `full` proves every file can be
rebuilt: a corrupted chunk that keeps its size is invisible to `quick`, and `sample` only sees it if the chunk is in the sample.
`sample` prints the seed it used; pass it again with `--seed <N>` to re-check exactly the same chunks. `--sample-percent` and
`--seed` are only valid with `--mode sample`.

The command exits with a non-zero exit code if validation fails.

## Test Restore

Prove that a snapshot really restores: restore a random sample of its files into a temporary folder, compare each with the
manifest, and delete the folder. Nothing is written outside the temporary folder.

```bash
restore test-restore "snapshots/documents_abcd1234ef567890/HEAD" --files 10
restore test-restore "snapshots/documents_abcd1234ef567890/HEAD" --seed 7 --json
```

`--files` (default 5) is the number of files; it is clamped to the number in the snapshot, and files with content are
preferred over empty ones. The seed is reported so the same files can be tested again. Each file is restored through the
normal restore path, which compares the rebuilt file's hash with the manifest; a file also fails if it is missing or its size
differs. The command exits `0` when every sampled file passed and `1` otherwise (with `--json`: `passed`, `seed`, per-file
results). In the GUI, use **Test restore** on the Dashboard (newest snapshot) or the Verify action of a snapshot in the Backup
Browser.

## Backup Schedules and the Backup Window

By default a folder is backed up when its last backup is older than `backupInterval`. Give a folder its own schedule to run it at
fixed times (`daily 02:00`), and set a backup window so automatic work only starts inside a chosen time range (for example overnight). Grammar, DST behaviour and the exact
window rules: [Backup Schedules and the Backup Window](../configuration.md#backup-schedules-and-the-backup-window).

```bash
restore schedule preview "weekdays 18:30" --count 3      # check an expression first (exit 2 when invalid)
restore config set-schedule D:\Photos "daily 02:00"       # one folder nightly
restore config set-schedule D:\Work "every 4h"             # another every four hours
restore config set-schedule D:\Archive off                 # never scheduled (watcher and manual only)
restore config set-schedule system "weekly sun 03:00"     # the system backup
restore config set-window 22:00 06:00 --days mon-fri      # automatic work only starts 22:00-06:00, Mon-Fri evenings
restore schedule next --json                              # what runs when, and why something is waiting
restore config clear-schedule D:\Photos
restore config clear-window
```

`schedule next --json` has `paused`, `repository` (`busy`), `backupWindow` (`active`, `openNow`, `nextOpenUtc`, ...), `nextDueUtc` and
`targets`: one entry per folder plus `system`, `verification` and `retention`, each with `schedule` (`expression`, `description`,
`source`: `folder`, `default` or the setting that supplies it), `lastUtc`, `dueUtc`, `nextStartUtc`, `nextStartLocal`, a `state` and a
`reason`. States: `scheduled`, `due` (runs on the next sweep), `waiting-for-window`, `paused`, `busy`, `off`, `disabled`,
`folder-missing`. The preview document has `expression`, `canonical`, `kind`, `description`, `timeZone`, `nextRuns` (`utc`, `local`) and `note`.

The app, `restore --service` and `restore run-due` share one due-time implementation, so they always agree. Manual backups
(`restore backup`, "Back up now") ignore the window.

## Status

`restore status [--json]` prints the state of protection without changing anything: the storage and encryption settings, for each
watched folder the last backup, last successful run, snapshot count, the files added/changed in the last run and when the next
backup is due (and whether it is overdue or has had no successful backup for twice its interval, i.e. is stale), the system
backup, retention and scheduled-verification settings and when each last ran, and whether another ReStore operation currently
holds the repository (`repository.busy`). Per folder it adds `schedule` (`expression`, `description`, `source`) and `waitingForWindow`
(`nextDueUtc` is when the folder will actually start, after the backup window); at the top level `backupWindow` and, for the system
backup, its `schedule`, `scheduleDescription` and `nextDueUtc`. It exits `0` whenever it could produce the report, so monitoring should read the
fields (for example `staleGroups`), not the exit code. Failed runs are not persisted, so a failure shows as a stale or overdue
folder and in the log.

## Doctor

```bash
restore doctor [--probe] [--json]
```

One read-only health check to run first when something is wrong. It never changes the configuration, the state or the storage (it
only writes and deletes one tiny temp file in the data folder to test that it is writable). Each check has an `id`, a `title`, a
`status` (`ok`, `warn`, `fail` or `skip`), a `detail` and, when something is off, a `fix` (usually a command). The exit code is `0` when no
check failed (warnings do not fail) and `1` otherwise; the JSON document has `summary` (`ok`, `warn`, `fail`, `skip`), `version`, `channel` and
`checks`. Details never contain secrets: messages from the log and the activity list are masked.

| Check id | What it looks at |
|----------|------------------|
| `version` | Version, install channel (`msix`, `portable`, `dev`) and architecture |
| `update` | A newer version, from the cached update check only (no network); says when it was last checked |
| `data-root` | The data folder exists and is writable |
| `disk-space` | Free space at the data folder and at the `local` storage in use; warns below 1 GB |
| `config` | `config.json` loads and validates (error and warning counts) |
| `watch-directories` | Watched folders exist |
| `watch-exclusions` | No watched folder lies inside an excluded path or pattern (it would back up nothing) |
| `storage` | A default storage is configured and every storage in use has its required options |
| `storage-probe` | With `--probe`: the real round trip of `restore probe` against every storage in use (needs network for remote storages) |
| `encryption` | When enabled, whether the password is available without a prompt (environment, password file, remembered); warns when not, because scheduled runs would fail |
| `oauth` | Google Drive and Dropbox in use have a token |
| `state` | The system state file loads |
| `repository-lock` | Whether another ReStore process holds the repository, and what it is doing |
| `pause` | Background protection is not paused |
| `protection` | Something runs backups automatically: the app, or the scheduled task `\ReStore\run-due` (Windows); warns when nothing does |
| `staleness` | Folders with no successful backup for more than twice the interval |
| `failures` | Failed operations in the last 7 days and the newest message |
| `path` | Windows: `restore` resolves on `PATH`; warns when both the MSIX alias and a portable copy are found, naming the winner |
| `msix-virtualization` | MSIX install with dev-tools settings in system backups: restores into AppData may be redirected; use the portable CLI |
| `telemetry` | Whether telemetry is on, the crash-report mode and any pending crash reports (see [Telemetry](#telemetry)) |

## Agent guide

```bash
restore agent-guide [--json]
```

Prints the guide for AI agents that operate ReStore on a user's behalf (the same text as [docs/agents.md](../agents.md)): the golden
rules (`--json`, `--non-interactive`, exit codes, preview before changing, never `--yes` without consent, never send crash
reports without asking) and recipes for the common jobs. The guide is embedded in the binary, so it always matches the installed version.
With `--json` the document has `version` and `guide` (the markdown text).

## Updates

`restore update check [--force] [--json]` asks GitHub whether a newer version exists. It exits `0` whether or not there is one (and
`1` only when the check itself failed). It reuses an answer younger than an hour unless `--force`. JSON: `current`, `latest`,
`updateAvailable`, `critical`, `releaseNotesUrl`, `channel`, `canInstall`, `reason` (why it cannot be installed, for example a
build from source), `installCommand`, `skippedVersion`, `checkedUtc`, `fromCache`.

`restore update install [--dry-run] [--yes] [--json]` downloads, verifies and installs the update for the install channel
(MSIX: hands over to Windows and the process ends; portable: replaces the files in place, the new version runs next start). It asks
`y/N` in a terminal; non-interactive runs need `--yes` and otherwise exit `4`. It exits `5` while another ReStore process holds the
repository (see `--wait`), `1` when the update cannot be installed (source build, no matching file, failed verification).
`--dry-run` prints what would happen and changes nothing. JSON: `current`, `latest`, `updateAvailable`, `installed`, `dryRun`,
`channel`, `asset`, `downloadPath`, `restartRequired`, `logPath`, `message`.

`restore update skip <version>` / `restore update skip --clear` hide a version from the hint and the GUI banner.

After a normal command the CLI may print `ReStore X is available (you have Y). Update: restore update install - notes: <url>` on
stderr, from the cached answer only and at most once a day. `status` shows the cached state as one line, and `status --json` has an
`update` object (`current`, `latest`, `updateAvailable`, `critical`, `releaseNotesUrl`, `skippedVersion`, `lastCheckUtc`).
`run-due` and `--service` refresh the cached answer (at most daily). How checks work, privacy and how to turn them off: [Updates](../updates.md).

## Run Due Work (Task Scheduler)

`restore run-due [--json]` performs one sweep of exactly what the background scheduler does on each tick, then exits:
interval backups that are due, the system backup if due, scheduled verification if due (using `verification.mode`), and the
daily retention pass. Because it is the same code, it respects every setting the scheduler does, including `backupInterval`, each
folder's [schedule](#backup-schedules-and-the-backup-window) and the backup window: work that is due outside the window is not
started (exit `0`) and runs on the first sweep after the window opens.
It never prompts for a password: with encryption enabled set `RESTORE_ENCRYPTION_PASSWORD` (or `RESTORE_ENCRYPTION_PASSWORD_FILE`), or keep a remembered password (`unlock --remember`). Without one it exits `4` (`needs_user`, with the hint) and does no work. Exit codes: `0` done (or nothing due), `1` a backup failed, `3` invalid config, `4` needs a person (no password), `5` repository busy.

Run it periodically (for example every 15 minutes) from Windows Task Scheduler instead of leaving the app or `--service`
running:

```bash
schtasks /Create /SC MINUTE /MO 15 /TN "ReStore run-due" /TR "restore.exe run-due --quiet"
```

`restore schedule install` creates a better-suited task for you (it also runs without a console window), see [Scheduled Task](#scheduled-task-windows). While
protection is [paused](#pause-and-resume-protection) the sweep does nothing and exits `0`.

| Exit code | Meaning |
|-----------|---------|
| `0` | Nothing failed. Includes "nothing is due" and work that was deferred because another operation held the repository (it stays due and is picked up by the next run) |
| `1` | A backup, the system backup, a verification or the retention pass failed, or the state could not be saved |
| `5` | The repository stayed busy until `--wait` ran out (also when only the final state save found it busy) |
| `3` | Configuration error |

With `--json` the document has `ok`, `exitCode`, `nothingDue`, `backups`, `systemBackup`, `verification`, `retention`,
`deferred`, `paused`, `backupWindow` (`closed`, `deferred`: operations waiting for the window) and `failures`. Retry back-off between failed runs is held in memory by the running scheduler, so each `run-due`
invocation retries whatever is due; the Task Scheduler interval is the retry interval.

Every `run-due` records its own outcome in `state/run-due-last.json` (start and finish time, exit code, error code and the counts of the sweep; no paths
or messages), whatever way it ended (success, failure, busy, configuration error). `restore schedule status` reports it, because the exit code of a task
started through `conhost.exe --headless` is not passed on to Task Scheduler.

## Browse Snapshots, File History and Differences

`ls`, `versions` and `diff` read snapshot manifests only: they download no chunks, need no encryption password
(manifests are not encrypted) and write nothing. `--json` prints one JSON document to stdout and nothing else.
`versions` needs a storage provider that can list its contents. The same views are in the app: **Backups** >
**Browse** on a snapshot.

```bash
# List a folder of a snapshot (default: the top level); --path is a manifest-relative path
restore ls "snapshots/documents_abcd1234ef567890/HEAD"
restore ls "snapshots/documents_abcd1234ef567890/HEAD" --path "reports/2026" --json

# Every distinct version of one file across the group's snapshots, newest first.
# <group> is the backed-up folder path or its snapshots/ folder name (see `restore snapshots`).
restore versions "C:\Users\YourName\Documents" "reports/2026/plan.docx"
restore versions documents_abcd1234ef567890 "reports/2026/plan.docx" --storage s3 --json

# What changed between two snapshots (A is the baseline)
restore diff "snapshots/documents_abcd1234ef567890/snapshot_20260101010101_abcdef.manifest.json" "snapshots/documents_abcd1234ef567890/HEAD"
```

- `ls` shows folders first, then files, with size and modification time (folder sizes are totals). A path that is not
  in the snapshot exits with `1`.
- `versions` collapses consecutive snapshots that held the same content into one line (`xN` is how many); each line names
  the oldest snapshot manifest holding that content, which you can pass to `restore` with `--include <file>` to
  restore that version. A file that appears in no snapshot exits with `1`.
- `diff` lists files added (only in B), removed (only in A) and changed (different content or size), and counts the
  unchanged ones. Differences are reported with exit code `0`.

## Preview Retention

```bash
restore retention --dry-run [--group <group>] [--json]
```

Lists, per group and storage type, which snapshots the configured retention policy would keep or delete, with the reason
for each (see [Retention Policies](../configuration.md#retention-policies)). It never deletes anything and `--dry-run` is
required (or use `retention plan`). It works even when `retention.enabled` is `false` (a note says so). `--group` takes a group name as shown in the
preview. With `--json` the document is written to stdout (log lines go to stderr).

## Apply Retention

```bash
restore retention plan [--group <group>] [--storage <type>] [--json]    # same as --dry-run
restore retention apply [--group <group>] [--storage <type>] [--yes] [--json]
```

`retention apply` deletes the snapshots the retention policy marks, now, with the same code the daily automatic retention
uses (manifests, and chunks nothing else references). It lists what it will delete, then asks `y/N` on stderr. Without a
terminal (`--non-interactive`, redirected stdin, `CI=true`) it exits `4` and deletes nothing unless `--yes` is given, so an
agent should run `retention plan` first and pass `--yes` only after that. If nothing is marked it deletes nothing and needs no
confirmation. `--storage` limits it to the snapshots stored in that storage type; `--group` to one backup group. Retention
must be enabled (`retention.enabled`) or the command exits `1`.

With `--json`: `applied`, `enabled`, `mode`, `planned`, `deleted`, `failed` and `groups` (each with the `snapshots` that were
planned: `path`, `storage`, `timestamp`, `label`, `reasons`, `deleted`). It exits `1` when some planned snapshot could not be
deleted (see the log); the rest stays in the plan for the next run.

## Pause and Resume Protection

```bash
restore pause [--for <duration> | --until-tomorrow | --indefinitely]
restore pause status
restore resume
```

The same pause as the tray menu: `--for` takes `30m`, `2h`, `1d`, `1h30m`; `--until-tomorrow` resumes at the next 08:00 that
is at least an hour away; without an option the pause lasts until `restore resume`. The pause is stored in
`state/protection_pause.json` and read by everything that runs backups on its own: the running app (within about 10 seconds),
`restore --service` and `restore run-due` (before every sweep and every backup). Manual `backup` commands and "Back up now" are
not affected. `pause` and `resume` need no configuration. JSON: `paused`, `indefinite`, `untilUtc`, `until` (`resume`:
`paused: false`, `wasPaused`).

## Activity

```bash
restore activity [--limit N] [--type backup|restore|verify|system-backup|system-restore|repair] [--failed] [--json]
```

The data of the Activity page, newest first (default 20 entries): backup runs and recorded operations with their result
(`succeeded`, `warning`, `failed`, `cancelled`) and message. `--failed` shows only failed operations. JSON: `count` and `items`
(`type`, `target`, `startUtc`, `endUtc`, `durationSeconds`, `result`, `message`, `storage`).

## Storage Usage

```bash
restore usage [--storage <type>] [--refresh] [--json]
```

The Dashboard storage panel: stored size, size of the latest versions, what deduplication and compression save, the snapshots
retention would delete and the space that frees, per backup group and storage. Sizes come from the local history and are close,
not exact (`estimated` is always `true`; `totals.hasEstimates` marks snapshots made before chunk sizes were recorded).
`--refresh` is "Refresh from storage": it lists `chunks/` in each storage and adds `measured` (`chunkCount`, `bytes` of the real
objects, including orphans); it needs the storage reachable and exits `1` when a listing fails. With `--storage` the totals are the
sums of that storage's groups.

## Upgrade Backup Format

```bash
restore upgrade-format [<groupKeyOrPath>] [--json]
```

The Settings "Upgrade backup format..." action: sets `chunkDiffing.manifestVersion` to `3`, so from the next backup on every backup
set is written in the newest snapshot format (file dates, attributes and empty folders are kept, snapshot lists are smaller).
Older snapshots stay readable; versions of ReStore older than this one cannot read v3 snapshots. Encrypted repositories upload
their data once more on the next backup (chunk ids change); unencrypted ones upload nothing again. Running it again changes
nothing (`changed: false`). The setting is global: the optional group is only checked to exist. Restart the app afterwards so it
reloads the configuration.

## Scheduled Task (Windows)

```bash
restore schedule install [--every <minutes>]    # default 15
restore schedule status
restore schedule remove
```

Creates the Windows Task Scheduler task `\ReStore\run-due` that runs `restore run-due --quiet` every N minutes (1 to 1440) as
the current user, only while that user is logged on (no Windows password is stored), hidden and also on battery; a missed run
starts as soon as possible. A console program started by Task Scheduler would still flash a window, so the task runs
`%SystemRoot%\System32\conhost.exe --headless "<restore.exe>" run-due --quiet`, which runs the command with no window at all (when
`conhost.exe` does not exist, on a very old Windows, the task runs `restore.exe` directly and a window may flash; `schedule install --json`
says which one in `hidden` and `launcher`). Installing again replaces the task. The MSIX install runs the `restore.exe` app execution alias
(`%LOCALAPPDATA%\Microsoft\WindowsApps\restore.exe`, which survives updates); a portable copy runs from where it is. `schedule`
uses `schtasks.exe` and fails with exit `1` on other systems.

`schedule status` reports `installed`, `enabled`, `intervalMinutes`, `executable` (the restore.exe, not conhost), `state`, `lastRun`, `lastRunUtc` (when the
time can be read) and `nextRun`. Because `conhost.exe` always hands Task Scheduler an exit code of 0, the result of the last run comes from `run-due` itself
(`state/run-due-last.json`): `lastResult` / `lastResultText` are its exit code and a one-line summary, `lastResultSource` is `run-due` (or `task-scheduler`
when no `run-due` has recorded anything yet), and `lastRunResult` has `startedUtc`, `finishedUtc`, `exitCode`, `ok`, `errorCode` and the counts.
`schedule install` and `status` add a warning in `warnings`
(not a failure) when encryption is enabled and a task would have no password: set `RESTORE_ENCRYPTION_PASSWORD` or
`RESTORE_ENCRYPTION_PASSWORD_FILE` as a persistent user environment variable, or keep a remembered password
(`state/remembered-password.bin`), otherwise every scheduled run exits `4` (`needs_user`) and `schedule status` shows that as the last result.

## Diagnostics and Logs

```bash
restore diagnostics [--json]
restore logs [--tail N] [--path] [--json]
```

`diagnostics` prints the report the About page copies ("Copy diagnostics"): versions, OS, `config.json` with secrets masked
and the last 200 log lines with secrets masked. It works even when the configuration is broken. With `--json` the fields are
structured (`version`, `channel`, `os`, `config` as an object or `null` with `configProblem`, `logLines`) and `report` is the text.
`logs` prints the location of the log file and its last lines (default 50, secrets masked); `--path` prints only the location.
JSON: `path`, `exists`, `sizeBytes`, `lines`.

## Share a File

```bash
restore share <file> [--storage <type>] [--expires <duration>] [--json]
```

The Share window: uploads the file (unencrypted) to `shared/<guid>/<name>` in a storage that can create links (`s3`, `azure`,
`gcp`, `dropbox`, `b2`) and prints the link. `--expires` takes `1h`, `2d`, ... (default `7d`; Dropbox links do not expire).
Without `--storage` the global storage is used when it can share, otherwise the first configured one that can. JSON: `file`,
`storage`, `link`, `expires`, `expiresUtc`, `note`.

## Config Secrets

```bash
restore config show [--reveal]
restore config protect
```

All `config` commands are subcommands (`config show`, `config protect`, `config export`, `config import`, `config validate`, and the
ones of [Setting up and configuring from the command line](#setting-up-and-configuring-from-the-command-line));
`restore config` alone is a usage error that lists them. `restore config validate` (older spelling: `restore --validate-config`)
validates the whole configuration, prints every error, warning and informational message, and exits `3` when it is invalid; with `--json` the
document has `valid`, `errors`, `warnings` and `info`.

`config protect` rewrites plain secret provider options (passwords, keys, tokens) in config.json to `dpapi:` values for the current Windows user and reports which options changed (never their values); it is Windows only and a second run changes nothing. `config show` prints config.json with secret option values replaced by `***` (`env:NAME` and `dpapi:...` references stay visible); pass `--reveal` to print the file as stored, secrets included (`--redact` is still accepted and is the default). See [Secrets in config.json](../configuration.md#secrets-in-configjson).

## Storage Probe

```bash
restore probe [--storage <type>] [--json]
```

Runs a real round trip against the storage (upload a small object under `restore-probe/<guid>`, check its size, download and compare it, find it in a listing, delete it, confirm it is gone) and prints each step with its latency. The object is always cleaned up. Exit code 0 when every step passed, 1 otherwise. `--json` writes the result to stdout.

## Export and Import Settings

```bash
restore config export <file>
restore config import <file> [--merge|--replace]
```

`config export` writes config.json plus the app preferences (tray, provider list, theme) with every secret option value removed; `env:NAME` references are kept, `dpapi:` blobs are removed because they only decrypt for the same Windows user. `config import` validates the resulting configuration first and leaves everything untouched (exit code 3) when it is invalid. `--merge` (default) overlays the file on the current config; `--replace` replaces it. Identifier options such as `accessKeyId`, `accountName` or `username` are kept (they are not secrets); option values that are URLs with embedded credentials or SAS/presigned signatures are removed too. Current secrets of providers with the same name are kept when the file has none. The previous config.json is saved under `backups/`.

## Telemetry

```bash
restore telemetry status [--json]
restore telemetry enable
restore telemetry disable
restore telemetry preview [--json]
restore telemetry reset-id
restore telemetry clear
restore telemetry crash-reports [ask|always|never] [--json]
restore telemetry crashes [list] [--json]
restore telemetry crashes show <id> [--json]
restore telemetry crashes send <id|--all> [--json]
restore telemetry crashes delete <id|--all> [--json]
```

Anonymous usage statistics (see [Telemetry](../telemetry.md) for exactly what is recorded). `status` reports `enabled`, the `reason` when it is off (`config`, `env-optout`, `env-do-not-track`, `env-ci`, `dev-channel`), `configEnabled`, `installId`, `endpointConfigured`, `queuedEvents`, `noticeShown`, `noticeShownUtc`, `noticeSurface` and `channel`. `enable` and `disable` change `telemetry.enabled` in config.json (`disable` also deletes the queued events). `preview` prints the queued events exactly as they would be sent (`--json`: `count` and `events`). `reset-id` replaces the random install id (`installId` in `--json`); `clear` deletes the queued events (`cleared`, `removed`).

Crash reports (see [Crash reports](../telemetry.md#crash-reports)): `crash-reports` shows or sets `telemetry.crashReports` (`crashReports`, `changed`, `enabled`, `reason` in `--json`). `crashes list` prints the pending scrubbed reports (`count`, `queued`, `reports[]` with `id`, `createdUtc`, `exceptionType`, `exceptionCount`, `bytes`); `crashes show <id>` prints exactly the JSON that would be sent (`--json`: `id`, `report`); `crashes send <id|--all>` moves reports into the upload queue (`queued`, `requested`, `endpointConfigured`; exit code 1 when telemetry is off or the id is unknown); `crashes delete <id|--all>` removes them (`deleted`). `status` also reports `crashReports`, `pendingCrashReports` and `queuedCrashReports`. When a command crashes with `ask` (the default) the CLI never prompts; it prints once on stderr how to review and send the pending report. Nothing is sent until a collection endpoint is configured.

The first time a command runs, a short notice about telemetry is printed on stderr (never on stdout, so `--json` output is unaffected; `--quiet` postpones it). Nothing is recorded in that first run. Set `RESTORE_TELEMETRY_OPTOUT=1` or `DO_NOT_TRACK=1` to turn telemetry off for a process or machine.

## System Backup

Backup installed programs, environment variables, and Windows settings:

```bash
# Backup all components (uses configured storage per component)
restore system-backup all

# Backup specific components
restore system-backup programs
restore system-backup environment
restore system-backup settings

# Override storage for every component of this run (instead of the configured per-component storage)
restore system-backup programs --storage github
```

## System Restore

Restore system components:

```bash
restore system-restore "system_backups/programs/programs_backup_<timestamp>.zip" programs
restore system-restore "system_backups/environment/env_backup_<timestamp>.zip" environment
restore system-restore "system_backups/settings/settings_backup_<timestamp>.zip" settings
```

Without `--plan` the command asks `y/N` on stderr (component and number of changes) when interactive; scripts and agents pass `--yes`, and without it
they exit `4` and change nothing (declining exits `1`). `--plan` never asks.

`--plan` shows what the backup would change without changing anything. Environment-variable values are masked in the plan, in the text and in
`--json`: you see the variable's name, whether it changes (`changed`) and the value length (`oldValueLength`, `newValueLength`); names that look like
secrets (containing TOKEN, SECRET, PASSWORD, PASSWD, KEY, CREDENTIAL, AUTH or CONNECTION) get no length either; PATH-like variables list the entries
added and removed (`pathAdded`, `pathRemoved`), because those are folder names, not secrets. `--reveal` shows the values as they are. Without `--plan`
the command applies the restore immediately (after saving an undo point).

## CLI Usage with Encryption

When encryption is enabled, the CLI prompts for your password (on stderr) during restore and verify operations when it is interactive:

```bash
# Restore encrypted chunk snapshot
restore restore "snapshots/documents_abcd1234ef567890/HEAD" "C:\Restore\Documents"

# Verify encrypted chunk snapshot
restore verify "snapshots/documents_abcd1234ef567890/HEAD"

# Restore encrypted system backups (will prompt for password)
restore system-restore "system_backups/programs/programs_backup_20250817.zip.enc" programs
restore system-restore "system_backups/environment/env_backup_20250817.zip.enc" environment
restore system-restore "system_backups/settings/settings_backup_20250817.zip.enc" settings
```

For non-interactive use (scripts, CI, AI agents) provide the password without a prompt: `--password-stdin`,
`RESTORE_ENCRYPTION_PASSWORD` or `RESTORE_ENCRYPTION_PASSWORD_FILE` (see [Non-interactive use](#non-interactive-use)). Without
one of them a non-interactive command exits with code `4`.

`--service` and `run-due` never prompt. If encryption is enabled and no password is available from those sources, they print
an error and exit with code `4` (`needs_user`; hint: `unlock --remember` or the environment variable), the same as every other command that needs a person. `run-due` records it in `state/run-due-last.json` (`exitCode` 4, `errorCode` `needs_user`).

## Recovering backups on a new PC

`snapshots`, `adopt` and `repair` work from the storage contents alone (the provider must support listing).

```
restore snapshots --storage s3 [--group <key or original path>] [--json]
restore adopt --storage s3 [--map "C:\Users\old\Documents=D:\Documents"]
restore repair --storage s3 [--dry-run] [--grace-hours 24] [--yes]
restore restore snapshots/<key>/HEAD @original --map "C:\Users\old=D:\Users\new"
```

- `change-password` (encrypted v3 repositories) re-wraps the repository key under a new password without uploading anything: the
  current password is read like for other commands, the new one from `RESTORE_NEW_ENCRYPTION_PASSWORD` or prompted twice.
  `recovery-key` prints a one-time recovery key that also unlocks the repository: the key alone on stdout (or `recoveryKey` in the `--json` document) and a one-line warning on stderr. The key is the command's result, so `--quiet` does not suppress it.
- Each backed-up folder has `snapshots/<key>/group.json` (original path, machine, install id; encrypted when encryption is on).
  Repositories written before it existed are still found through their `HEAD` and manifests.
- `adopt` adds the discovered snapshots to this PC's history and chunk bookkeeping; running it again changes nothing.
- `repair` is a mark-and-sweep: it marks every chunk referenced by any manifest under `snapshots/` (all folders, all
  machines), then deletes chunks that nothing references and that are older than the grace window (default 24 hours;
  chunks with an unknown age are kept). It aborts without deleting anything if listing fails, a manifest or `HEAD`
  cannot be read, or no manifests are visible although local history has snapshots. It re-reads `snapshots/`
  immediately before deleting.
- **Run `repair` when other machines sharing the same bucket/prefix are idle.** A backup on another machine that reuses an
  old unreferenced chunk is only protected once its manifest is written; the grace window and the re-check narrow
  the race but cannot close it for a backup that runs for a long time. Use `--dry-run` first to see what would be deleted. Without `--dry-run`, `repair` asks `y/N` on stderr ("Delete N unreferenced chunks (X MB)?") when interactive and needs `--yes` otherwise (exit `4`, nothing is deleted).
