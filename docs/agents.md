# ReStore guide for AI agents

This page is written for an AI agent (or any script) that operates ReStore on a user's behalf. It is short on purpose.
`restore agent-guide` prints the same text from the installed binary, so it always matches the version you are driving.
The full reference is [CLI usage](usage/cli.md); `restore commands --json` is the same reference as machine-readable JSON.

## What ReStore is

A backup and restore tool for Windows. Folders are saved as point-in-time snapshots (a manifest plus deduplicated,
optionally AES-256-GCM encrypted chunks) to a storage: a local folder, S3, Google Drive, Dropbox, Azure, GCP, GitHub, SFTP
or Backblaze B2. It also backs up installed programs, environment variables and Windows settings. There is a GUI app and
a command-line tool, `restore`; this guide is about the command line. Settings live in `%USERPROFILE%\ReStore\config.json`.

## Install

- Portable CLI, no admin, no install package (recommended for agents):
  `irm https://github.com/js302/ReStore/releases/latest/download/install.ps1 | iex`
  It puts `restore.exe` in `%LOCALAPPDATA%\Programs\ReStore\cli` and on the user PATH; open a new terminal afterwards.
- MSIX (the Store-style installer, also installs the GUI): it provides `restore` through an app execution alias.

Verify with `restore --version`.

## First commands

<!-- e2e -->
```
restore --version
restore doctor --json
restore commands --json
```

`restore doctor` is read-only and lists every problem with a `fix` (usually a command). Run it first, and again whenever
something fails. It exits 0 when no check failed (warnings do not fail) and 1 otherwise. `restore commands --json` lists
every command, option, exit code and whether a command changes data (`mutates`) or deletes it (`destructive`).

## Golden rules

1. Always pass `--json` and `--non-interactive`. You get exactly one JSON document on stdout (logs go to stderr) and the
   command never waits for a prompt. Every document has `"ok"`; a failure is
   `{"ok": false, "exitCode": N, "error": {"code", "message", "hint"}}`. Read `error.hint`: it names the next step.
2. Read the exit code:

   | Code | Meaning | What you do |
   |------|---------|-------------|
   | 0 | success | continue |
   | 1 | failure | read `error.message`; run `restore doctor --json`. `error.code` is `not_found` when the snapshot or file you named is not in the storage: list the real paths with `restore snapshots --json` |
   | 2 | usage error | fix the command line (`restore help <command>`) |
   | 3 | configuration error | `restore config validate --json`, then fix the config |
   | 4 | needs the human | a password, a sign-in, elevation or a confirmation is needed: stop and ask the user |
   | 5 | busy | another ReStore process holds the repository: retry with `--wait 120` (seconds) |
   | 130 | cancelled | Ctrl+C |

3. Preview before you change anything: `restore restore ... --dry-run`, `restore system-restore ... --plan`,
   `restore retention --dry-run` and `restore repair --dry-run` change nothing and say what they would do. Show the user
   what they report before running the real command. A `system-restore` plan masks environment-variable values (names, whether they
   change and their length; PATH-like variables list the entries they add or remove); do not add `--reveal` unless the user asks
   to see the values.
4. Every destructive command asks for confirmation, so know which ones need `--yes`.
   The commands that require `--yes` when nothing can answer a question (non-interactive or no terminal) are exactly these;
   without `--yes` they change nothing and exit 4 (`needs_user`, with a hint naming `--yes` and the preview command):
   - `retention apply`
   - `repair`
   - `restore --conflict overwrite`
   - `system-restore`
   - `update install`
   - `encryption disable`

   Interactively they ask `y/N` on stderr (naming what will happen); answering anything but `y` exits 1 and changes nothing.
   Preview first: `retention --dry-run`, `repair --dry-run`, `restore ... --dry-run` and `system-restore ... --plan` never ask and
   change nothing; `repair` without `--dry-run` and `system-restore` without `--plan` are the confirmed real runs, and
   `restore` asks only with `--conflict overwrite` (the other conflict policies never replace a file). Pass `--yes` only when the
   user explicitly agreed to that exact action. `schedule remove`, `system-undo` and `config import` do **not** ask and do not
   need `--yes`; they act at once, so look first and tell the user what will happen: `restore system-undo --list` shows the
   undo points, `restore config export` saves the current settings before an import (and `config validate` checks the result
   afterwards), and `restore schedule status` shows what `schedule remove` will delete. Show the user the result and run the
   real command only after they explicitly agree.
5. Never print, log or put secrets on a command line. Pass secret option values with `--option-stdin <name>` (or
   `config set <key> --value-stdin`) and the encryption password with `--password-stdin` or the environment variable
   `RESTORE_ENCRYPTION_PASSWORD` / `RESTORE_ENCRYPTION_PASSWORD_FILE`. `config show` and `diagnostics` already mask secrets.
   Redirected input and output are UTF-8 (no byte-order mark), so non-ASCII names in `--json` output and non-ASCII passwords
   are exact. From Windows PowerShell 5.1 run `$OutputEncoding = [System.Text.UTF8Encoding]::new($false)` before piping a
   secret that is not plain ASCII (PowerShell 7 already uses UTF-8); see [the CLI reference](usage/cli.md#text-encoding-when-redirected).
6. Tell the user about notices you see on stderr: the one-time telemetry notice (anonymous usage statistics, see
   [telemetry](telemetry.md); `restore telemetry status` shows the state, `restore telemetry disable` turns it off) and the
   "a new version is available" line (see [updates](updates.md)). Do not enable telemetry or install an update on your own.
7. Never send crash reports without asking the user. `restore telemetry crashes list` shows pending reports and
   `restore telemetry crashes show <id>` shows exactly what would be sent; only run `restore telemetry crashes send` after
   the user has agreed. Do not change `restore telemetry crash-reports` on your own either.

Useful global options: `--wait <seconds>` (wait for a busy repository, default 30), `--quiet`, and `--storage <type>` on many
commands (use another configured storage for one command).

## Recipes

### Set up a local backup

<!-- e2e -->
```
restore init --storage local --path D:\Backups --watch %USERPROFILE%\Documents --probe --json --non-interactive
restore backup %USERPROFILE%\Documents --json --non-interactive
```

`init` creates `config.json`, sets the storage, replaces the watched folders and probes the storage. It refuses to overwrite
a customised setup unless you pass `--force`; ask the user before forcing. Add more folders with `restore config add-watch <dir>`
and list them with `restore config list-watch`. A folder inside an excluded path (for example `%TEMP%`) backs up nothing: `init` and
`config add-watch` warn about it (JSON `warnings`), and `backup <dir>` refuses it with exit 3 unless you pass `--allow-excluded`;
the rule is in `excludedPaths` (`restore config get excludedPaths`, change it with `restore config set excludedPaths <json list>`).

### Set up S3 (or another remote storage)

```
echo <secretAccessKey> | restore init --storage s3 --option region=eu-west-1 --option bucketName=my-backups --option accessKeyId=<id> --option-stdin secretAccessKey --watch D:\Data --probe --json --non-interactive
```

Other types and their options: `restore help config set-storage`. S3-compatible services (MinIO, Wasabi, R2) take
`--option serviceUrl=<endpoint>` in addition. Check afterwards with `restore probe --storage s3 --json`.
Add `--protect` to store secrets as Windows-protected values instead of plain text.

### Encrypted repository

<!-- e2e -->
```
echo <password> | restore encryption enable --password-stdin --json --non-interactive
```

Ask the user to choose the password and to store it safely: it cannot be recovered. At least 8 characters. After the first
backup tell the user to keep an offline copy of the key file (`restore export-key <file>`). For scheduled runs to work the
password must be available without a prompt: set `RESTORE_ENCRYPTION_PASSWORD_FILE`, or on Windows run
`restore unlock --remember --password-stdin` once (it stores the password for this Windows user only). Later commands on an
encrypted repository need `--password-stdin` or one of those sources, otherwise they exit 4.

### Google Drive or Dropbox sign-in (two steps with the human)

A person must approve access in a browser, so you cannot finish this alone.

- Google Drive: put the OAuth client in the config (`restore config set-storage gdrive --option client_id=<id> --option-stdin client_secret`),
  then ask the user to run `restore auth gdrive` in a terminal on this computer. In a non-interactive run it exits 4, or with
  `--no-browser` it prints a URL on stderr that the user opens.
- Dropbox: `restore auth dropbox --json` prints an authorize URL (needs `appKey`). Give the URL to the user, who approves and
  gives you the code; then run `restore auth dropbox --code <code> --json`.

Check with `restore auth status --json` (`tokenPresent`). `restore doctor` reports a missing sign-in too.

### Run backups in the background

Backups do not run by themselves from the command line. On Windows install the scheduled task, which runs
`restore run-due --quiet` every 15 minutes while the user is logged on, without a window:

```
restore schedule install --every 15 --json
restore schedule status --json
```

`schedule status` reports how the last `run-due` went (its own exit code, error code and counts; Task Scheduler's own result is
not meaningful for a hidden task). The GUI app (when running) and `restore --service` also back up in the background.
`restore pause --for 2h` and `restore resume` pause and resume all of them.

When a folder should back up at set times instead of every `backupInterval`, give it a clock schedule and check it:

<!-- e2e -->
```
restore schedule preview "daily 02:00" --count 3 --json
restore config set-schedule <dir> "daily 02:00" --json
restore config set-schedule system "weekly sun 03:00"
restore schedule next --json
```

The target is a watched folder or the word `system`; `restore config clear-schedule <target>` goes back to the default. Forms:
`off`, `every <N>m|h|d` (at least 5m), `daily HH:mm[,HH:mm]`, `weekdays HH:mm`, `weekends HH:mm`, `<days> HH:mm`
(`mon,wed,fri` or `mon-fri`), `weekly <day> HH:mm`, `monthly <1-28|last> HH:mm` and `cron <5 fields>`; times are local. An invalid
expression exits 2 with the reason, so run `schedule preview` first. To keep scheduled work out of working hours use
`restore config set-window 22:00 06:00` (`--days mon-fri`; `config clear-window` removes it): work due outside the window waits,
manual backups still run. `schedule next` shows for every folder when it runs and why it is not running now (for example
`waiting-for-window`).

Encrypted repositories need the password without a prompt (see above), or the scheduled run (`run-due`, `--service`) exits 4 (`needs_user`, hint `unlock --remember` or `RESTORE_ENCRYPTION_PASSWORD`) and does nothing: that is a request for the user, so stop and ask. `schedule status` and `doctor` report the same.

The ReStore app (tray) and `--service` pick up `config.json` changes you make with these commands within a second or so and restart
their background work only when something it depends on changed; you do not need to restart them. `config set` and the other
editing commands validate against the config schema, so `ok: true` means the value was stored as given (a wrong type or an
out-of-range value exits 3 and writes nothing).

### Check health

<!-- e2e -->
```
restore doctor --json
restore status --json
restore activity --failed --json
```

`doctor` checks the setup (config, storage, password, sign-ins, lock, schedule, PATH, updates, telemetry). `status` shows each
folder's last and next backup and the stale ones. `doctor --probe` also does a real upload, download and delete against every
storage in use.

### Restore a file or a folder somewhere else

<!-- e2e -->
```
restore snapshots --json
restore restore <snapshotManifestOrHeadPath> D:\Restored --dry-run --json --non-interactive
restore restore <snapshotManifestOrHeadPath> D:\Restored --include "docs/report.docx" --json --non-interactive
```

`snapshots` lists the backups with their paths (`.../HEAD` is the newest snapshot of a folder). Show the user the dry-run
preview, then run the same command without `--dry-run`. Existing files are never replaced unless you pass
`--conflict overwrite` (do that only on the user's say-so); `--conflict keepboth` writes `name (restored).ext` next to them.
`@original` as the target restores to where the files came from. `restore ls <path>` lists a snapshot's folders without
downloading anything, and `restore verify <path> --mode quick` checks integrity.

### Find the versions of a file

<!-- e2e -->
```
restore versions <groupKeyOrPath> <relativePath> --json
```

Each version names the oldest snapshot holding that content; pass that snapshot to `restore restore` with `--include <file>`.
`restore diff <manifestA> <manifestB>` shows what changed between two snapshots.

### Connect existing backups on a new PC

Install ReStore, configure the same storage (`restore init --storage ... --probe`, same encryption password), then:

<!-- e2e -->
```
restore snapshots --json
restore adopt --json --non-interactive
```

`adopt` adds the snapshots found in the storage to this PC's history. Use `--map <old>=<new>` when the folders live in other
places on this PC. Then restore with `restore restore <path> @original --map <old>=<new> --dry-run`.

### Recover from a busy or locked repository

Exit 5 means another ReStore process (the GUI, the scheduled run, a service, another CLI) is working. Retry the command with
`--wait 120`. `restore doctor --json` (check `repository-lock`) says what holds it. Do not delete lock files and do not kill
processes without asking. If the same command keeps failing, show the user the output of `restore diagnostics` (secrets are masked).

### Update ReStore

```
restore update check --json
restore update install --dry-run --json
```

Tell the user when a newer version exists; install it (`restore update install --yes`) only after they agree. MSIX installs
hand over to Windows, which closes ReStore.

## What not to do

- Do not edit `system_state.json` or other files in the data folder `%USERPROFILE%\ReStore\state` by hand. Use `restore config set`
  and `restore config unset` (validated, with an automatic backup) for `config.json`.
- Do not delete, rename or upload objects in the storage by hand. Use `restore retention apply` (after `--dry-run`) to prune.
- Run `restore repair` only when no other machine or process is using the same storage, and `--dry-run` first.
- Do not run `restore system-restore` without showing the user its `--plan` first, and do not run `restore system-undo` unasked.
- Do not put the encryption password, access keys or tokens into a command line, a log, a file in a repository or your reply.
- Do not turn telemetry or crash reports on, send crash reports, or skip or install updates, without asking.
- Do not guess a storage path or credentials. Ask the user.

## When something fails

1. Read `error.code`, `error.message` and `error.hint` of the JSON document, and the exit code.
2. Run `restore doctor --json` and read every `fail` and `warn` with its `fix`.
3. For configuration problems run `restore config validate --json`.
4. Tell the user plainly what failed and what you need from them (a password, a sign-in, a decision).
