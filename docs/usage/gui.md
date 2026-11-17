# GUI Application Usage

The GUI provides:

- **Dashboard**: Statistics, a backup health panel, recent backup history, and quick actions
- **Backup Browser**: View and manage backup history with restore, verify, label, delete, and open-location actions
- **Settings**: Configure watch directories, storage providers, and backup options
- **System Tray**: Minimize to tray and control the file watcher in the background

## First launch

The first time you start ReStore with nothing configured, a three-step wizard opens: pick the
folders to protect, choose a destination and test the connection, and optionally turn on
encryption. It offers to run the first backup when you finish, so you do not have to touch
`config.json` by hand.

You can skip the wizard and configure everything from the Settings page instead.

## Backup health panel

The Dashboard shows what the backup telemetry actually says rather than just a success count:

- Bytes deduplication has saved, as an exact figure rather than an estimate
- Integrity-check pass rate, and when verification last ran
- Restore success rate across past attempts
- Each watched folder with its last successful backup, colour-coded by staleness
- A "needs attention" list covering restore failure categories, verification problems, folders
  that have never been backed up, and retention being switched off

Folder rows link straight through to a restore.

## Protection status and background operation

The Dashboard shows a status chip for background protection: **Active** (watcher and scheduler running), **Needs password** (encryption is on and the password has not been entered, so backups are paused until you unlock), **Paused**, or **Stopped**, plus the time of the next scheduled run. Protection is owned by the app rather than by a page, so moving between pages never starts a second watcher. It starts automatically at launch when `protection.autoStart` is on and at least one folder is watched, and it is stopped cleanly (an in-flight backup is cancelled and state saved) when you exit or sign out.

The next run time on the Dashboard chip and in the status bar follows each folder's schedule and the backup window (when the window is closed it shows when the window opens). In Settings, each watched folder has a **Schedule** selector (use the default interval, every N hours, daily at, weekdays at, or a custom expression such as `mon,wed,fri 07:15` or `cron 0 */6 * * *`) with a live "Next runs" preview; an invalid entry is explained and never saved. **Backup Configuration** has a **Backup window** section (enable, start, end, days) and **System Backup** has its own schedule selector. "Back up now" ignores the window. Details: [Backup Schedules and the Backup Window](../configuration.md#backup-schedules-and-the-backup-window).

CLI equivalents: `restore pause` / `resume` / `pause status` (the tray's pause, honoured by the running app within seconds), `restore activity` (Activity page), `restore usage [--refresh]` (storage panel and "Refresh from storage"), `restore retention plan|apply`, `restore upgrade-format` (Settings "Upgrade backup format..."), `restore diagnostics` (About "Copy diagnostics"), `restore schedule install|status|remove|preview|next` (the latter two for backup schedules), `restore config set-schedule|set-window` and `restore share <file>`; see [CLI Usage](cli.md).

When a backup, restore, verification, label, delete or repair is running, buttons that would conflict with it are disabled until it finishes; ReStore performs one repository-changing operation at a time. A scheduled or watcher backup that finds the repository busy skips that cycle and retries later.

The Dashboard also lists the last backup runs per folder (files added, changed, removed and skipped, duration, bytes uploaded), and shows a "Backups are out of date" banner (with a once-a-day notification) for folders with no recent successful backup.

## Connecting to existing backups

On a new PC, the first-run wizard and Settings offer **Connect to existing backups**: after you choose the provider, ReStore lists the backup groups found there (with their original paths), lets you remap a path to this PC, and adds them to this PC's history so they appear in the Backup Browser and can be restored. Settings also has **Repair**, which shows a preview of leftover data that no snapshot uses before deleting anything. See the [CLI guide](cli.md#recovering-backups-on-a-new-pc) for the equivalent commands and caveats.

## Progress and cancellation

Manual backups, restores and verifications open a progress window showing the current phase,
per-file and per-byte counts, and a live log. Cancel stops the operation at its next safe
checkpoint; closing the window does the same rather than orphaning the work.

A cancelled backup leaves the previous snapshot as the current one. A cancelled restore discards the
file that was being written; files already completed stay in place.

## Restoring

Restores always show a preview before writing anything: how many files, how large, and which
existing files at the destination would be affected — separating files that are already
identical from ones that genuinely differ. You choose per-file what to include and what should
happen to conflicts (skip, keep both, or overwrite; aborting on conflict is available only in the CLI with `--conflict fail`).

The default is to skip, so nothing you already have is replaced unless you say so.

## Labelling a restore point

Snapshots are listed by timestamp, which is not much help months later. The **Label** action on
a Backup Browser row lets you name one — "before Windows reinstall", say — and the label shows
as a chip on that row from then on.

## Snapshot verification

Each snapshot row in the Backup Browser has a **Verify Snapshot** action.

- Available for snapshot manifest and HEAD artifacts only
- Validates manifest integrity, chunk presence/hash/size, and reconstructed file hashes
- Results are shown in the UI, and telemetry counters are persisted to `system_state.json`

Verify asks how thorough the check should be: **Quick** (every chunk exists with its recorded size; downloads nothing),
**Sample** (quick checks plus a full check of a random percentage of the chunks, `verification.samplePercent`) or **Full**
(every chunk downloaded and every file rebuilt). A full verification downloads every unique chunk the snapshot references, so it
costs bandwidth on metered providers. The same dialog offers **Test restore**, which restores a few random files to a temporary
folder, compares them with the backup and deletes them; the Dashboard also has a **Test Restore** button for the newest snapshot.
ReStore can also run verification on a schedule — see
[Configuration](../configuration.md#scheduled-verification).

## Post-Installation Configuration

After installation, visit the Settings page to:

- Enable "Run at Windows Startup" to launch ReStore (hidden in the tray) automatically when you sign in
- Configure your storage providers
- Add directories to watch
