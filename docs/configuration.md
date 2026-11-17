# Configuration

You can configure ReStore through the GUI settings page, from the command line, or by editing the configuration file directly.

From the command line (no hand-editing, every change validated and the previous file kept under `backups/`): `restore config get <key>` and
`restore config set <key> <value>` read and change any setting by dotted key (`retention.keepDaily`, `storageSources.s3.options.bucketName`),
`restore config set-storage`, `config add-watch` and `config remove-watch` handle the common cases, `restore init` sets everything up in one command,
and `restore config schema` prints a JSON Schema with a description of every key in this file. See
[Setting up and configuring from the command line](usage/cli.md#setting-up-and-configuring-from-the-command-line).

## Key Settings

**Watch Directories**: Folders to monitor for automatic backups, each with optional storage override

**Global Storage**: Default storage destination for paths without specific configuration

**Backup Type**: Choose between Full, Incremental, or ChunkSnapshot. ChunkSnapshot stores point-in-time manifests and deduplicated chunks.

**Backup Interval** (`backupInterval`): How often each watched directory is swept for changes (unless it has its own [schedule](#backup-schedules-and-the-backup-window)), as a .NET TimeSpan string (`hh:mm:ss` or `d.hh:mm:ss`, for example `"01:00:00"` for one hour; default `01:00:00`). It is not a number of hours. The sweep complements the live file watcher by catching changes made while ReStore was not running. Zero or negative disables only these interval sweeps; the watcher keeps working. `systemBackup.backupInterval` uses the same format (default one day).

**Schedules and Backup Window** (`watchDirectories[].schedule`, `systemBackup.schedule`, `backupWindow`): back a folder up at fixed times of day (`daily 02:00`, `weekdays 18:30`, `cron 0 */6 * * *`) instead of every N hours, and only let automatic work start inside a chosen time range. See [Backup Schedules and the Backup Window](#backup-schedules-and-the-backup-window).

**Storage Providers**: Each source takes provider-specific `options` plus an optional `prefix` (see [Storage Providers](storage/README.md)); only Local storage uses the top-level `path`. Configure Local, S3, Google Drive, GCP, Azure, Dropbox, B2, SFTP, or GitHub storage with per-path and per-component selection

**Exclusions**: File patterns and paths to skip during backup

**Size Limits**: Maximum file size and backup size thresholds

**Encryption**: Password-based AES-256-GCM encryption for secure backups

**Retention**: Automatic pruning of old backups. Always keeps at least one backup per group (the newest), even if it exceeds max age.

**Chunk Diffing**: Snapshot manifest version (`manifestVersion`: `3` always writes the new format, `2` always writes the old one; configs migrated from older versions keep `2` until you set 3 or press Settings > "Upgrade backup format", because older ReStore versions cannot read v3; if the key is absent, new groups start on 3 and existing v2 groups stay on 2) `compression` (`auto` default: v3 chunks are Brotli-compressed when it saves at least ~3% and the file type is not already compressed; `off`: always store raw; has no effect on v2). Once a group has compressed chunks, `manifestVersion: 2` cannot reference them and the backup stops with an error telling you to set `3` (or `0`/remove the key); v2 can never hold compressed chunks, and chunking profile controls (`minChunkSizeKB`, `targetChunkSizeKB`, `maxChunkSizeKB`, rolling hash window, and safety limits).

**Scheduled Verification**: Periodic integrity checks of recent snapshots per group. Disabled by default, and restricted to local storage unless you opt out.

**Notifications**: Tray notifications for backup and verification failures, and for the first success after a run of failures.

## Configuration File Location

Both the GUI and CLI applications use a **unified configuration** located at:

```
%USERPROFILE%\ReStore\config.json
```

This is typically: `C:\Users\YourName\ReStore\config.json`

**First Time Setup:**

When you first launch ReStore (GUI or CLI), it will automatically:

1. Create the `%USERPROFILE%\ReStore` directory
2. Copy the latest packaged `config.example.json` into `%USERPROFILE%\ReStore\config.example.json`
3. Create `config.json` from the template (if not already present)
4. You can then configure all settings through:
   - **GUI**: Settings page, which covers every option
   - **Manual**: Edit `config.json` directly in a text editor

## Secrets in config.json

Any storage provider option value can be a secret reference instead of a literal:

- `dpapi:<base64>`: encrypted with Windows DPAPI for the current Windows user (with an app-specific entropy). Only the same user on the same PC can decrypt it; a config copied elsewhere fails with a message naming the option.
- `env:NAME`: read from the environment variable `NAME` when the provider is created. Use this for CI, other users or non-Windows machines. A missing variable fails with a message naming the option and the variable.

References are resolved only when a storage provider is built; config.json (and a Settings save) keeps the reference, and secret values are never logged. Plain values and `%VAR%` expansion keep working.

- **Settings**: the *Store encrypted for this Windows user* checkbox (on by default on Windows) saves the secret fields of a provider (passwords, keys, tokens) as `dpapi:` values. Untick it to save them as typed, or type `env:NAME` in a secret field. Fields showing an existing `dpapi:` or `env:` value are left unchanged unless you retype them.
- **CLI**: `restore config protect` rewrites plain secret options in config.json to `dpapi:` values (Windows only); `restore config show` prints the config with secret values replaced by `***` by default (references are shown as `env:NAME` / `dpapi:...`), safe to paste into a bug report; `restore config show --reveal` prints the secrets too.
- Secret options: `secretAccessKey`, `sessionToken`, `applicationKey`, `connectionString`, `token`, `accessToken`, `refreshToken`, `appSecret`, `password`, `passphrase`, `client_secret`.
- Any option value of the exact form `env:NAME` (letters, digits, underscore) is always treated as a reference, never as a literal. `appKey` (Dropbox) is also treated as a secret.
- The first-run wizard also saves literal secrets as `dpapi:` on Windows.
- Not covered: the S3/Azure default credential chains (no key configured) are not used; ReStore always needs the keys in the config.

## Logging

```json
{ "logging": { "level": "Info" } }
```

- `logging.level` (default `Info`): minimum level written to the console and `restore.log`: `Debug`, `Info`, `Warning` or `Error` (case-insensitive). Per-chunk transfer lines are `Debug`, so they only appear when you opt in. An unknown value is a validation error and the logger stays at `Info`. Older configs without this key behave as `Info`.
- The log file is held open and written through a buffer: warnings and errors are flushed immediately, other lines within about a second, and everything on exit.

## Transfer concurrency

Chunk uploads and restore downloads run in parallel within a single operation, bounded by the storage provider's own limit (S3, Azure, GCS, B2: 8; Dropbox: 4; Google Drive: 3; local: 4; SFTP and GitHub: 1). There is no config key for it. On providers that support listing, a backup with more than 64 candidate chunks lists `chunks/<namespace>/` once instead of issuing one existence request per chunk, and re-uploads any chunk whose stored size differs from the expected size.

## Background Protection and Watcher Settings

```json
{
  "protection": { "autoStart": true },
  "watcher": {
    "minSnapshotInterval": "00:15:00",
    "maxDebounceWait": "00:05:00"
  }
}
```

- `protection.autoStart` (default `true`): start background protection (file watcher plus interval scheduler) automatically when the app launches. It only takes effect when at least one watch directory is configured. Older configs without this key behave as `true`.
- `watcher.minSnapshotInterval` (TimeSpan, default 15 minutes): minimum time between two watcher-triggered snapshots. Changes made inside the interval are kept and backed up once it has elapsed. Zero or negative removes the minimum.
- `watcher.maxDebounceWait` (TimeSpan, default 5 minutes): the watcher waits for a quiet period (10 seconds) before backing up; this caps how long a continuously busy folder can postpone a backup. Zero disables the cap.

Missing or unparseable values fall back to the defaults. Failed watcher backups are retried with back-off, and a watcher overflow or a newly created or renamed directory triggers a full pass of the affected root.

## Backup Schedules and the Backup Window

By default every watched folder is swept when its last successful backup is older than `backupInterval`, and the system backup when it
is older than `systemBackup.backupInterval`. Two optional settings change that. Configs without them behave exactly as before.

```json
{
  "watchDirectories": [
    { "path": "D:\\Photos", "storageType": null, "schedule": "daily 02:00" },
    { "path": "D:\\Work", "storageType": null, "schedule": "weekdays 18:30" },
    { "path": "D:\\Archive", "storageType": null, "schedule": "off" },
    { "path": "D:\\Notes", "storageType": null }
  ],
  "systemBackup": { "enabled": true, "backupInterval": "1.00:00:00", "schedule": "weekly sun 03:00" },
  "backupWindow": { "enabled": true, "start": "22:00", "end": "06:00", "days": "all" }
}
```

### Schedule expressions

`watchDirectories[].schedule` and `systemBackup.schedule` take one expression (optional strings: absent or blank means "use
`backupInterval` / `systemBackup.backupInterval`"). Expressions are case-insensitive and extra spaces are ignored. Times are
24-hour `HH:mm` (`2:05` also works) in **local** time.

| Expression | Meaning |
|------------|---------|
| `off` | Never scheduled: the folder is backed up by the file watcher and by hand only |
| `every <N>m`, `every <N>h`, `every <N>d` | At least this long after the last backup, exactly like `backupInterval`. Between 5 minutes and 365 days: `every 30m`, `every 4h`, `every 1d` |
| `daily HH:mm[,HH:mm...]` | Every day at the given times: `daily 02:00`, `daily 06:00,18:00` |
| `weekdays HH:mm`, `weekends HH:mm` | Monday to Friday, or Saturday and Sunday |
| `<days> HH:mm` | Days as a list or range of `mon tue wed thu fri sat sun` (full names work too): `mon,wed,fri 07:15`, `mon-fri 18:30`, `sat-mon 09:00` |
| `weekly <day> HH:mm` | One day a week: `weekly sun 03:00` |
| `monthly <1-28|last> HH:mm` | A day of the month, or the last day of every month: `monthly 1 03:00`, `monthly last 23:30` |
| `cron <minute hour day-of-month month day-of-week>` | Standard five-field cron: `*`, lists (`1,15`), ranges (`1-5`), steps (`*/15`, `5-30/5`, `10/20`), month and day names (`jan-mar`, `mon-fri`), day of week `0-7` with `0` and `7` both Sunday. When both day fields are restricted (neither starts with `*`) either one matching is enough (classic cron: `0 0 13 * fri` is the 13th and every Friday); otherwise both must match. A cron expression that can never run (`0 0 31 2 *`) is refused |

An invalid expression is refused by the validator with the position and reason, for example
`expected HH:mm at 'daily 2pm' ('2pm' is not a time of day; use 24-hour HH:mm such as 02:00 or 18:30).`
`restore schedule preview "<expression>"` shows what an expression means and its next run times before you save it.

**When is a clock-scheduled folder due?** When the latest scheduled time at or before now is later than its last successful backup
(a run that found nothing to back up counts). A PC that was off at 02:00 therefore backs up **once** when it comes back, not once per
missed night, and a manual backup made after a scheduled time satisfies that time. A folder that was never backed up is due at
once, like today. Failed backups are retried with the existing back-off (5 minutes, doubling, at most an hour between attempts for
a clock schedule); a busy repository defers the run to the next sweep. `every` schedules keep the interval semantics, and `off`
schedules are never started by the scheduler (`status` shows no next due time for them).

**Daylight saving.** A time that does not exist on a day (the clocks spring forward over 02:30) runs at the first valid minute after
the gap (03:00); a time that happens twice (the clocks fall back over 01:30) runs once, at its first occurrence. Schedules never run twice
for the same local time.

### Backup window

`backupWindow` limits when **automatic** work may *start*: scheduled and watcher-triggered backups, the system backup, scheduled
verification and the retention pass. Work that is due outside the window waits for it to open and then runs (watcher changes stay
queued, nothing is lost). Manual backups (`restore backup`, "Back up now" in the app and the tray) always run, and a backup that
already started when the window closes is allowed to finish.

| Key | Meaning |
|-----|---------|
| `enabled` | `true` applies the window; `false` keeps it in the file without applying it (default when the object exists: `true`) |
| `start`, `end` | Local `HH:mm` (defaults `22:00` and `06:00`). The start is **inclusive** and the end **exclusive**: with `22:00` to `06:00`, 22:00:00 is inside and 06:00:00 is outside. The window may cross midnight. `start` equal to `end` means the whole day |
| `days` | The days a window **starts** on: `all` (default), `weekdays`, `weekends`, or days such as `mon,wed,fri` / `mon-fri`. With `22:00` to `06:00` on `mon`, the window is open Monday 22:00 until Tuesday 06:00; Tuesday 03:00 belongs to Monday's window and Monday 03:00 does not. For a whole-day window they are the days it is open |

A malformed window (hand-edited) is reported by `config validate` and ignored at run time, so automatic backups are never blocked by a typo.
`restore schedule next` shows what is waiting for the window and when it opens; `status` shows the window and each folder's next start.
The shipped example config carries a switched-off window (`"enabled": false`) as a template.

## Telemetry

```json
"telemetry": { "enabled": true, "crashReports": "ask" }
```

- `telemetry.enabled` (default `true`): anonymous usage statistics (which commands and features are used, success or failure, durations; never file names, paths or contents). Users are told once on first run. It is also switched off by `RESTORE_TELEMETRY_OPTOUT=1`, `DO_NOT_TRACK=1`, a `CI` environment and development builds; change it with `restore telemetry enable|disable` or in Settings. Everything recorded, and where it is stored, is listed in [Telemetry](telemetry.md). No collection endpoint is configured in this version: events stay in a bounded local queue.
- `telemetry.crashReports` (default `ask`): what happens to an anonymous, scrubbed crash report after a crash: `ask` keeps it on this PC and asks you (the CLI never prompts, it prints how to send it), `always` queues it to be sent with the usage statistics, `never` keeps nothing. It also needs `telemetry.enabled` and none of the opt-outs above. Change it with `restore telemetry crash-reports [ask|always|never]` or in Settings. What a report contains and how it is scrubbed: [Crash reports](telemetry.md#crash-reports).

## Upgrading from 1.7

- **Protection now starts by itself.** Existing configs that have watch directories but no `protection` section behave as `"protection": { "autoStart": true }`: the file watcher and scheduler start when the app launches. Set `"protection": { "autoStart": false }` (or use the Dashboard toggle) to keep the old behaviour of starting only when you press Start.
- **Backup format stays as it was.** `chunkDiffing.manifestVersion` remains `2` for existing configs until you set it to `3` or press Settings > "Upgrade backup format", because older ReStore versions cannot read v3 manifests. New groups on a fresh install start on `3`.
- **One-time full re-hash.** The first run after upgrading has no stat cache (size and timestamps per file) in `system_state.json`, so every file is read and hashed once; later runs only read files whose size or timestamp changed (plus the periodic deep scan, see [Change Detection and Deep Scan](#change-detection-and-deep-scan)). Nothing is re-uploaded for unchanged content.
- **Coming from 1.6?** Version 1.7.0 also changed chunk boundaries, so the first backup of each folder re-chunks and re-uploads once; see the [release note in the chunk model](architecture/chunk-manifest.md).
- `restore config show` now masks secrets by default; use `--reveal` to print them.

## Change Detection and Deep Scan

```json
{
  "backup": { "deepScanIntervalDays": 7 }
}
```

- `backup.deepScanIntervalDays` (integer, default `7`): an incremental run recognises an unchanged file by its size, last-write time and creation time, without reading it. A file whose recorded timestamp is within 2 seconds of the moment it was recorded is always re-read ("racy timestamp" rule). Every `deepScanIntervalDays` days, per folder and destination, a deep scan reads and hashes every file instead, which catches a change that kept the file's size and timestamps. `0` makes every run a deep scan (the slowest, most thorough mode). Negative values are rejected; missing values behave as `7`. Each run logs how many files were scanned, hashed and reused. A file rewritten so that its size, last-write time and creation time are all preserved (and a stale directory entry of an NTFS hard link, whose metadata is not updated when another link is written) is only caught by the deep scan; a deep scan is not recorded as complete when any file could not be read.

## Exclusions

```json
{
  "excludedPatterns": ["*.tmp", "~$*"],
  "excludeGlobs": ["node_modules/", "**/bin/", "*/cache/*"],
  "excludePresets": ["office-temp", "system"],
  "backup": { "hydrateCloudFiles": false }
}
```

**`excludedPatterns` (legacy, unchanged)**: `*` and `?` wildcards compared with the file NAME only, case-insensitive, files only. They never match or prune a folder (`*.log` does not skip a folder called `x.log`), and a pattern containing a slash never matches. Existing configs keep exactly this behaviour; nothing is migrated.

**`excludeGlobs` (new)**: directory-aware globs, case-insensitive. `*` matches within one path segment, `?` one character, `**` any number of segments (`**/` may match none). A trailing `/` makes the glob match folders only (`node_modules/` is the folder form of a name). A glob without a slash is matched against the name of every file and folder at any depth; one with a slash is matched against the end of the full path (`**/bin/`, `*/cache/*`); one starting with `/`, a drive letter or `%VAR%` must match the whole path. A glob that matches a folder excludes the folder and everything below it, and the walk does not enter it. Folders above the watched folder are never considered. The Settings page puts entries containing `/`, `\` or `**` here and plain wildcards in `excludedPatterns`. The file watcher and incremental snapshots apply the same rules, so excluded paths never trigger a backup.

**Presets** (`excludePresets`, additive; their entries use the glob syntax): `dev` (node_modules, .git, bin, obj, .vs, target, __pycache__, .venv), `caches` (Cache, Cache2, Code Cache, GPUCache, ShaderCache, Service Worker/CacheStorage, Temp folders and `*.tmp`), `office-temp` (`~$*`, `*.~lock*`) and `system` (Thumbs.db, desktop.ini, `$RECYCLE.BIN`, System Volume Information). Unknown names are ignored with a validation warning. A config without `excludePresets` keeps today's behaviour (empty list; its own `excludedPatterns` already cover most of `office-temp` and `system`); only a newly created config starts with `["office-temp", "system"]`.

**Links and cloud files**: directory junctions and symbolic links (name-surrogate reparse points) are never followed (one summary line is logged per run); other reparse directories such as OneDrive Files-On-Demand folders are entered normally. Symbolic links to FILES are followed: the content they point to is backed up. Cloud-only placeholder files (OneDrive Files On Demand: recall-on-data-access, recall-on-open or offline attributes) are not downloaded: they are skipped and reported with the reason "cloud-only (not downloaded)", and a previous snapshot's copy of such a file is kept (also for watcher-triggered incremental snapshots; watcher events for placeholders are ignored). Set `backup.hydrateCloudFiles` to `true` to download and back them up (default `false`).

**Destination**: a local storage folder or the ReStore data folder (`%USERPROFILE%\ReStore` or `RESTORE_HOME`) inside a watched folder is excluded automatically, and validation warns about it. If a watched folder is inside the destination, nothing is excluded automatically and validation warns instead.

## Schema Versioning and Upgrades

ReStore tracks `configSchemaVersion` in `config.json`.

- On startup, ReStore detects older config schemas and applies non-destructive migrations.
- Before writing migrated config, ReStore creates a backup file under `%USERPROFILE%\ReStore\backups\` named like `config.pre-migration.<timestamp>_<guid>.json`.
- Migrations preserve existing user values and only inject missing defaults or compatibility mappings.

Current compatibility behavior includes:

- Legacy `backupType: Differential` is mapped to `ChunkSnapshot`
- Missing `chunkDiffing` blocks are injected with safe defaults
- Missing/invalid `encryption.keyDerivationIterations` is repaired to `1000000`
- Missing `verification` blocks are injected disabled, with a 7-day interval and `localStorageOnly: true`
- Missing `notifications` blocks are injected with failure notifications on and per-success notifications off

You can validate your final config at any time with:

```powershell
dotnet run --project ReStore.Core -- --validate-config
```

## Application Data Location

The root defaults to `%USERPROFILE%\ReStore`; set the `RESTORE_HOME` environment variable to use another directory.

All application data is stored in a centralized user directory:

```
%USERPROFILE%\ReStore\
├── config.json              (Main configuration - shared by GUI and CLI - auto-created on first run)
├── config.example.json      (Latest packaged reference template)
├── appsettings.json         (GUI-specific settings - auto-generated)
├── backups\                 (Automatic pre-migration config backups)
├── logs\                    (Application logs, and crash-*.txt crash reports)
└── state\
    └── system_state.json    (Backup metadata and history - auto-generated)
```

Backup data itself goes to whatever the active storage provider points at. For local storage
the default is `%USERPROFILE%\ReStoreBackups`, which is configurable.

**Settings Available in GUI:**

All configuration options can be managed through the Settings page:

- **General Settings**: Theme, startup options, system tray integration
- **Storage Providers**: Local, Google Drive, AWS S3, GitHub, Azure, GCP, Dropbox, SFTP, and Backblaze B2 configuration, each with a Test button
- **Global Default Storage**: Set the default storage type for all backups
- **Watch Directories**: Add/remove folders to monitor with individual storage selection per path
- **Backup Configuration**: Type (Full/Incremental/ChunkSnapshot), interval, size limits, chunking profile
- **Encryption**: Enable/disable AES-256-GCM encryption with password protection for all backups
- **System Backup**: Enable/disable system state backups, choose which components to include (programs, environment variables, Windows settings), and select separate storage per component
- **Retention**: Configure how many backups to keep and max backup age
- **Exclusions**: File patterns and paths to exclude from backups

### What the backup types do

- `Full`: backs up all selected files.
- `Incremental`: backs up files that changed since the last recorded version of those files.
- `ChunkSnapshot`: records a new manifest snapshot and reuses already-uploaded content-addressed chunks.

New configs default to `ChunkSnapshot` for user-file backups.

## Retention Policies

Retention policies automatically prune old backups using the backup history in `%USERPROFILE%\ReStore\state\system_state.json`.

Rules:

- Retention applies to all backup groups (file backups per watched directory, and system backups like programs/environment/settings).
- The newest backup in each group is always kept (so there is always at least one backup), even if it is older than `maxAgeDays`.
- A snapshot with a label (e.g. "before Windows reinstall") is never deleted by retention, whatever its age or position. Remove the label first if it should become eligible for pruning.
- Rules are applied separately to each (group, storage type) pair: snapshots on one storage never keep or delete snapshots on another, and each storage keeps its own newest snapshot.

Example configuration:

```json
{
  "retention": {
    "enabled": true,
    "keepLastPerDirectory": 10,
    "maxAgeDays": 30
  }
}
```

If the `retention` block is absent, retention is **off** with `keepLastPerDirectory` 10 and `maxAgeDays` 30. The shipped `config.example.json` (and so a fresh install) turns retention on with `keepLastPerDirectory` 5.

### Grandfather-father-son tiers and rule mode

Besides keep-last and max-age, retention can keep one snapshot per period (restic-style):

```json
{
  "retention": {
    "enabled": true,
    "keepLast": 5,
    "keepWithinDays": 14,
    "keepHourly": 0,
    "keepDaily": 7,
    "keepWeekly": 4,
    "keepMonthly": 12,
    "keepYearly": 3,
    "mode": "any"
  }
}
```

| Setting | Meaning |
|---------|---------|
| `keepLastPerDirectory` (alias `keepLast`) | Keep the newest N snapshots. Floored at 1 (a value below 1 is treated as 1, and the validator rejects it while retention is enabled). This rule is always on. |
| `maxAgeDays` (alias `keepWithinDays`) | Keep every snapshot newer than N days. 0 = off. |
| `keepHourly`, `keepDaily`, `keepWeekly`, `keepMonthly`, `keepYearly` | Keep the newest snapshot of each of the last N hours / days / ISO weeks / months / years **that contain a snapshot**. 0 = tier off. |
| `mode` | `any` (default) or `all`, see below. |

- Buckets use local time (a DST repeat of an hour counts as two separate hour buckets). Within a bucket the newest snapshot wins; N counts buckets that have a snapshot, not calendar periods.
- `mode: "any"`: a snapshot is kept if **any** enabled rule keeps it (the union). With only `keepLastPerDirectory` and `maxAgeDays` this is exactly the behaviour of earlier versions.
- `mode: "all"`: a snapshot is kept only if **every** enabled rule keeps it (the intersection): keep-last is always one of the enabled rules, so it must be among the newest `keepLast`, newer than `keepWithinDays` (if > 0) and selected by every tier that is > 0. This is much stricter; try `restore retention --dry-run` first.
- In both modes the newest snapshot of each (group, storage type) and labelled snapshots are always kept.
- If both an alias and its original name are present, the alias wins; ReStore writes the original names back. Existing configs without the new keys behave exactly as before (tiers 0, mode `any`).
- Preview what a policy would delete without deleting anything: `restore retention --dry-run [--group <group>] [--json]`, or **Preview retention** on the Settings page (uses the values currently typed in the form, before saving).

## Scheduled Verification

Verification re-downloads every unique chunk of a snapshot, decrypts it if necessary, and
re-hashes each file to confirm the backup would actually restore. It catches bit-rot and a
provider silently dropping objects — failure modes that otherwise stay invisible until a
restore is attempted.

Because a run transfers the whole snapshot, it is **disabled by default** and limited to
local storage unless you explicitly opt out.

```json
{
  "verification": {
    "enabled": true,
    "verifyInterval": "7.00:00:00",
    "localStorageOnly": true,
    "snapshotsPerRun": 2,
    "mode": "full",
    "samplePercent": 5
  }
}
```

- `verifyInterval` uses `d.hh:mm:ss`. To turn verification off set `enabled` to `false`: an enabled config with a zero or negative interval fails validation. Intervals under one hour are accepted with a warning and are clamped to one minute.
- `localStorageOnly: false` extends verification to remote providers. On egress-billed
  storage this is a real recurring cost, so `--validate-config` warns when it is set.
- `snapshotsPerRun` bounds how many snapshots per folder are checked in one due cycle. The
  newest is always first; any remaining budget rotates through older snapshots,
  least-recently-verified first, so an older restore point is eventually covered instead of
  never being looked at. Each additional snapshot re-downloads the chunks it references, so
  raising this multiplies one cycle's transfer cost.
- `mode` is `full` (the default: every chunk downloaded and every file rebuilt, so the same behaviour as before modes
  existed), `sample` (every chunk checked for existence and stored size, and a random `samplePercent` of them fully
  verified) or `quick` (existence and stored size only, nothing downloaded). `sample` and `quick` make frequent checks on
  remote storage affordable but do not prove every file restores; the default stays `full` so enabling verification never gets
  weaker unless you choose it. An unreadable `mode` is rejected by `--validate-config` and treated as `full` at run time.
  `samplePercent` is 1 to 100 (default 5). The same modes are available on demand: `restore verify --mode ...`.
- Results are recorded in the existing verification telemetry and surfaced on the Dashboard's
  Backup Health card.

## Notifications

Tray notifications report the events worth interrupting for and stay silent otherwise —
notifying on every routine success trains people to ignore the notifications that matter.

```json
{
  "notifications": {
    "enabled": true,
    "notifyOnBackupFailure": true,
    "notifyOnVerificationFailure": true,
    "notifyOnRecovery": true,
    "notifyOnEveryBackupSuccess": false
  }
}
```

- `notifyOnRecovery` fires on the first successful cycle after one or more failing cycles.
- `notifyOnEveryBackupSuccess` is off by default; enable it only if you want per-cycle
  confirmation.
- Windows Focus Assist is respected: while it is active, notifications are suppressed.

## Application Behavior Settings

**Run at Windows Startup**: Configure ReStore to automatically launch when Windows starts. Packaged (MSIX) installs use a Windows startup task (visible under Task Manager > Startup; if you disable it in Windows Settings, ReStore reports that it was disabled by the user). Unpackaged installs add an entry to the Windows Registry (`HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`) that launches with `--startup`. A startup launch starts hidden in the tray, and background protection starts if `protection.autoStart` is on. By default, this is disabled and can be toggled from the Settings page.

**System Tray Integration**: When minimized, ReStore can run in the system tray, allowing you to keep the file watcher active in the background without cluttering your taskbar. By default, this is disabled and can be configured from the Settings page.

**CLI Access**: The `restore` command is automatically available system-wide after installation. This can be disabled from Settings → Apps → Advanced app settings → App execution aliases and toggling off `restore.exe`. Uninstalling ReStore will also remove the CLI access.
