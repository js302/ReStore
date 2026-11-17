using ReStore.Core.src.core;
using ReStore.Core.src.backup;
using ReStore.Core.src.cli;
using ReStore.Core.src.utils;

namespace ReStore.Core
{
    // The specs of the commands that ship with the CLI. Their handlers are the private Run* methods of Program.cs.
    public partial class Program
    {
        private static readonly OptionSpec StorageOption = new(
            "--storage", "Use this storage type instead of the configured one (a key of storageSources in config.json, e.g. local, s3, gdrive).", "storageType");

        private static readonly OptionSpec DryRunOption = new("--dry-run", "Preview only; change nothing.");

        internal static IEnumerable<CommandSpec> BuiltInSpecs() =>
        [
            // ---- Backup & restore ----
            new CommandSpec
            {
                Name = "backup",
                Category = CommandCategory.BackupRestore,
                Summary = "Back up a folder to the configured storage.",
                Description = "Writes a snapshot (manifest, HEAD pointer and deduplicated chunks) and applies retention. The folder does not have to be one of the configured watch directories. "
                    + "Encrypted repositories need the password: --password-stdin, RESTORE_ENCRYPTION_PASSWORD or RESTORE_ENCRYPTION_PASSWORD_FILE. Files that could not be read are skipped and reported (JSON: skipped). "
                + "A folder inside an excluded path (excludedPaths, e.g. the default %TEMP%) or matching an exclusion glob would back up nothing, so it fails with exit 3 naming the rule unless --allow-excluded is given; a run that finds no files warns (JSON: warnings).",
                Positionals = [new PositionalSpec("sourceDir", "Folder to back up.")],
                Options = [StorageOption, new OptionSpec("--allow-excluded", "Back the folder up even though it lies inside an excludedPaths entry or matches an exclusion pattern (exit 3 otherwise).")],
                Examples = ["backup %USERPROFILE%\\Desktop", "backup %USERPROFILE%\\Documents --storage gdrive", "backup D:\\Data --json --non-interactive"],
                Mutates = true,
                ValidateWatchDirectories = false,
                State = StateRequirement.Mutating,
                Validate = p => string.IsNullOrWhiteSpace(p.Positionals[0]) ? "The folder to back up is empty." : null,
                Handler = RunBackupAsync
            },
            new CommandSpec
            {
                Name = "restore",
                Category = CommandCategory.BackupRestore,
                Summary = "Restore a snapshot into a folder (preview first; existing files are kept by default).",
                Description = "Every restore prints a preview first: file count, size and which destination files already exist. --dry-run stops after the preview. "
                    + "--conflict decides what happens to existing files: skip (default) never replaces a file, overwrite replaces, keepboth writes 'name (restored).ext' alongside, fail aborts before writing if any file would be replaced. "
                    + "Use @original as <targetDir> to restore to the folder the snapshot was taken from (remapped by --map). --include may be repeated; '*' and '**' are supported against manifest paths. "
                    + "--conflict overwrite asks y/N on stderr when interactive and needs --yes otherwise (exit 4, nothing is written); preview with --dry-run.",
                Positionals =
                [
                    new PositionalSpec("backupPath", "Snapshot manifest or HEAD path in the storage (see 'snapshots')."),
                    new PositionalSpec("targetDir", "Folder to restore into, or @original.")
                ],
                Options =
                [
                    StorageOption,
                    new OptionSpec("--conflict", "What to do with files that already exist at the destination.", "policy", AllowedValues: ["skip", "overwrite", "keepboth", "fail"]),
                    new OptionSpec("--include", "Only restore manifest paths matching this glob.", "pattern", Repeatable: true),
                    new OptionSpec("--map", "Rewrite the destination: old=new path prefix.", "old=new", Repeatable: true),
                    DryRunOption
                ],
                Usage =
                [
                    "restore <backupPath> <targetDir> [--storage <storageType>] [--conflict skip|overwrite|keepboth|fail] [--include <pattern>]... [--map <old>=<new>]... [--dry-run] [--yes]"
                ],
                Examples =
                [
                    "restore snapshots/documents_abc123/HEAD C:\\Restored --dry-run",
                    "restore snapshots/documents_abc123/HEAD C:\\Restored --conflict keepboth",
                    "restore snapshots/documents_abc123/HEAD C:\\Restored --include \"docs/**\"",
                    "restore snapshots/documents_abc123/HEAD C:\\Restored --conflict overwrite --yes",
                    "restore snapshots/documents_abc123/HEAD @original --map C:\\Users\\old=D:\\Users\\new"
                ],
                Mutates = true,
                Destructive = true,
                Config = ConfigRequirement.Storage,
                Validate = ValidateRestore,
                IsPreview = parsed => parsed.HasOption("--dry-run"),
                Handler = RunRestoreAsync
            },
            new CommandSpec
            {
                Name = "verify",
                Category = CommandCategory.BackupRestore,
                Summary = "Check a snapshot's integrity (manifest and chunks) without restoring.",
                Description = "--mode full (default) downloads and hashes every chunk; quick checks that every chunk exists with its recorded stored size, no downloads; "
                    + "sample does the quick checks and fully verifies a random --sample-percent (default 5) of the chunks. The seed used is reported and can be passed again with --seed. "
                    + "Exits 1 when verification fails.",
                Positionals = [new PositionalSpec("snapshotManifestOrHeadPath", "Snapshot manifest or HEAD path in the storage.")],
                Options =
                [
                    StorageOption,
                    new OptionSpec("--mode", "How thorough the check is.", "mode", AllowedValues: ["full", "sample", "quick"]),
                    new OptionSpec("--sample-percent", "Percentage of chunks to verify in full (1-100); only with --mode sample.", "1-100"),
                    new OptionSpec("--seed", "Seed of a previous sample, to repeat it; only with --mode sample.", "N")
                ],
                Examples =
                [
                    "verify snapshots/documents_abc123/HEAD",
                    "verify snapshots/documents_abc123/HEAD --mode sample --sample-percent 10",
                    "verify snapshots/documents_abc123/HEAD --mode quick"
                ],
                Config = ConfigRequirement.Storage,
                Validate = p => TryValidateVerifyOptions(p.Options, out var error) ? null : error,
                Handler = RunVerifyAsync
            },
            new CommandSpec
            {
                Name = "test-restore",
                Category = CommandCategory.BackupRestore,
                Summary = "Prove a snapshot restores: restore a random sample of files to a temp folder and compare them.",
                Description = "Restores a random sample of --files (default 5) files to a temporary folder, checks them against the manifest, then deletes the folder. The seed is reported so the same files can be tested again. Exits 1 when any sampled file fails.",
                Positionals = [new PositionalSpec("snapshotManifestOrHeadPath", "Snapshot manifest or HEAD path in the storage.")],
                Options =
                [
                    StorageOption,
                    new OptionSpec("--files", "How many files to test (default 5).", "N"),
                    new OptionSpec("--seed", "Seed of a previous run, to test the same files.", "N")
                ],
                Examples = ["test-restore snapshots/documents_abc123/HEAD --files 10", "test-restore snapshots/documents_abc123/HEAD --seed 7 --json"],
                Config = ConfigRequirement.Storage,
                Validate = p => TryGetPositiveInt(p.Options, "--files", out _, out var error) && TryGetNonNegativeInt(p.Options, "--seed", out _, out error) ? null : error,
                Handler = RunTestRestoreAsync
            },

            // ---- Browse ----
            new CommandSpec
            {
                Name = "snapshots",
                Category = CommandCategory.Browse,
                Summary = "List the backups found in the storage (works on a new PC from the storage alone).",
                Description = "Reads the storage contents, so it also finds backups made on other machines. The provider must support listing. --group takes a group key or the original folder path.",
                Options = [StorageOption, new OptionSpec("--group", "Only this group (group key or original folder path).", "groupKeyOrPath")],
                Examples = ["snapshots --storage s3", "snapshots --group C:\\Users\\me\\Documents --json"],
                Config = ConfigRequirement.Storage,
                Handler = RunSnapshotsAsync
            },
            new CommandSpec
            {
                Name = "ls",
                Category = CommandCategory.Browse,
                Summary = "List a folder inside a snapshot (manifest only, no chunk downloads).",
                Description = "Read-only and manifest-only: no chunk is fetched and no password is needed. Shows folders first, then files with size and modification time. A path that is not in the snapshot exits 1.",
                Positionals = [new PositionalSpec("snapshotManifestOrHeadPath", "Snapshot manifest or HEAD path in the storage.")],
                Options = [StorageOption, new OptionSpec("--path", "Manifest-relative folder to list (default: the top level).", "folder")],
                Examples = ["ls snapshots/documents_abc123/HEAD --path projects/2026"],
                Config = ConfigRequirement.Storage,
                Handler = RunBrowseAsync
            },
            new CommandSpec
            {
                Name = "versions",
                Category = CommandCategory.Browse,
                Summary = "Show every distinct version of one file across a group's snapshots.",
                Description = "Consecutive snapshots with the same content collapse into one line; each names the oldest snapshot manifest holding that content, which you can pass to 'restore' with --include <file>. Needs a provider that can list. A file in no snapshot exits 1.",
                Positionals =
                [
                    new PositionalSpec("groupKeyOrPath", "Backed-up folder path or its snapshots/ folder name."),
                    new PositionalSpec("relativePath", "File path relative to the backed-up folder.")
                ],
                Options = [StorageOption],
                Examples = ["versions C:\\Users\\me\\Documents projects/plan.docx"],
                Config = ConfigRequirement.Storage,
                Handler = RunBrowseAsync
            },
            new CommandSpec
            {
                Name = "diff",
                Category = CommandCategory.Browse,
                Summary = "Show what changed between two snapshots (A is the baseline).",
                Description = "Lists files added (only in B), removed (only in A) and changed, and counts the unchanged ones. Differences are reported with exit 0.",
                Positionals = [new PositionalSpec("manifestPathA", "Baseline snapshot manifest path."), new PositionalSpec("manifestPathB", "Newer snapshot manifest or HEAD path.")],
                Options = [StorageOption],
                Examples = ["diff snapshots/documents_abc123/snapshot_A.manifest.json snapshots/documents_abc123/HEAD"],
                Config = ConfigRequirement.Storage,
                Handler = RunBrowseAsync
            },

            // ---- Repository & keys ----
            new CommandSpec
            {
                Name = "adopt",
                Category = CommandCategory.Repository,
                Summary = "Add the snapshots found in the storage to this PC's history (e.g. after a reinstall).",
                Description = "Works from the storage contents alone; running it again changes nothing. --map remaps the original folder paths of another machine.",
                Options = [StorageOption, new OptionSpec("--map", "Remap a folder path: old=new.", "old=new", Repeatable: true)],
                Examples = ["adopt --storage s3 --map C:\\Users\\old\\Documents=D:\\Documents"],
                Mutates = true,
                Config = ConfigRequirement.Storage,
                State = StateRequirement.Mutating,
                Validate = ValidateMap,
                Handler = RunAdoptAsync
            },
            new CommandSpec
            {
                Name = "repair",
                Category = CommandCategory.Repository,
                Summary = "Mark-and-sweep the storage: delete unreferenced chunks, re-adopt orphan snapshots.",
                Description = "Marks every chunk referenced by any manifest under snapshots/ (all folders, all machines), then deletes chunks that nothing references and that are older than --grace-hours (default 24; chunks of unknown age are kept). "
                    + "It aborts without deleting anything if listing fails or a manifest cannot be read. Run it while other machines sharing the same bucket/prefix are idle, and try --dry-run first. Without --dry-run it asks y/N on stderr when interactive and needs --yes otherwise (exit 4, nothing is deleted).",
                Options = [StorageOption, DryRunOption, new OptionSpec("--grace-hours", "Keep unreferenced chunks younger than this many hours (default 24).", "N")],
                Examples = ["repair --storage s3 --dry-run", "repair --storage s3 --yes"],
                Mutates = true,
                Destructive = true,
                Config = ConfigRequirement.Storage,
                State = StateRequirement.Mutating,
                Validate = ValidateGraceHours,
                IsPreview = parsed => parsed.HasOption("--dry-run"),
                Handler = RunRepairAsync
            },
            new CommandSpec
            {
                Name = "retention",
                Category = CommandCategory.Repository,
                Summary = "Preview (--dry-run, plan) or apply (apply) the retention policy: which snapshots are kept and which deleted.",
                Description = "'retention --dry-run' (or 'retention plan') lists, per group and storage type, which snapshots the configured retention policy would keep or delete, with reasons, and never deletes. "
                    + "'retention apply' deletes exactly those snapshots now (the same code the daily automatic retention uses); it asks y/N on stderr when interactive and needs --yes otherwise (exit 4). Retention must be enabled in the configuration to apply it.",
                Positionals = [new PositionalSpec("action", "apply (delete now) or plan (preview, same as --dry-run).", Required: false)],
                Options = [DryRunOption, new OptionSpec("--group", "Only this backup group (a path as shown in the preview).", "group"), StorageFilterOption],
                Usage = ["retention --dry-run [--group <group>] [--storage <storageType>]", "retention plan [--group <group>] [--storage <storageType>]", "retention apply [--group <group>] [--storage <storageType>] [--yes]"],
                Examples = ["retention --dry-run --group C:\\Users\\me\\Documents", "retention plan --json", "retention apply --yes --json"],
                Mutates = true,
                Destructive = true,
                Config = ConfigRequirement.Loaded,
                Validate = ValidateRetention,
                Handler = RunRetentionAsync
            },
            new CommandSpec
            {
                Name = "change-password",
                Category = CommandCategory.Repository,
                Summary = "Change the password of an encrypted v3 repository (re-wraps the key, no re-upload).",
                Description = "Reads the current password like other commands and the new one from RESTORE_NEW_ENCRYPTION_PASSWORD (or prompts twice; without a terminal set the variable). The old password stops working. Keep a copy of keys/repository.keys.json (see export-key): without it encrypted v3 data cannot be read.",
                Options = [StorageOption],
                Mutates = true,
                Config = ConfigRequirement.Storage,
                Handler = RunKeyCommandAsync
            },
            new CommandSpec
            {
                Name = "recovery-key",
                Category = CommandCategory.Repository,
                Summary = "Add a one-time recovery key to an encrypted v3 repository and print it.",
                Description = "The recovery key is shown once; store it somewhere safe. It unlocks the repository without the password.",
                Options = [StorageOption],
                Mutates = true,
                Config = ConfigRequirement.Storage,
                Handler = RunKeyCommandAsync
            },
            new CommandSpec
            {
                Name = "export-key",
                Category = CommandCategory.Repository,
                Summary = "Save a copy of the repository key file (keep it offline).",
                Description = "The file contains only password-wrapped key slots, no plaintext secret, but without it encrypted v3 backups cannot be read if the storage copy is lost.",
                Positionals = [new PositionalSpec("file", "Where to write the key file.")],
                Options = [StorageOption],
                Mutates = true,
                Config = ConfigRequirement.Storage,
                Handler = RunKeyFileTransferAsync
            },
            new CommandSpec
            {
                Name = "import-key",
                Category = CommandCategory.Repository,
                Summary = "Put a saved key file back into the storage (e.g. after losing keys/repository.keys*.json).",
                Positionals = [new PositionalSpec("file", "Key file written by export-key.")],
                Options = [StorageOption],
                Mutates = true,
                Config = ConfigRequirement.Storage,
                Handler = RunKeyFileTransferAsync
            },

            // ---- System ----
            new CommandSpec
            {
                Name = "system-backup",
                Category = CommandCategory.System,
                Summary = "Back up installed programs, environment variables, Windows settings and developer tools.",
                Description = "Without a component (or with 'all') every component is backed up to its configured storage; --storage overrides the storage of every component for this run.",
                Positionals = [new PositionalSpec("component", "programs, environment, settings, devtools or all (default).", Required: false)],
                Options = [StorageOption],
                Examples = ["system-backup all", "system-backup programs --storage local"],
                Mutates = true,
                WindowsOnly = true,
                State = StateRequirement.Mutating,
                Validate = p => p.Positionals.Count == 1 && !SYSTEM_COMPONENT_TYPES.Contains(p.Positionals[0], StringComparer.OrdinalIgnoreCase)
                    ? $"Unknown system-backup component '{p.Positionals[0]}'. Expected one of: {string.Join(", ", SYSTEM_COMPONENT_TYPES)}." : null,
                Handler = RunSystemBackupAsync
            },
            new CommandSpec
            {
                Name = "system-restore",
                Category = CommandCategory.System,
                Summary = "Restore a system backup (environment variables are applied; programs and settings are reported or planned).",
                Description = "--plan shows what the backup would change on this machine without changing anything. Environment-variable values are masked in the plan (name, change and length only; names that look like secrets get no length; PATH-like variables list the entries added and removed); --reveal shows the values. Before applying changes an undo point is saved (revert with system-undo). Environment variables are restored; program installs and Windows-settings imports are only reported on the CLI. Without --plan it asks y/N on stderr when interactive and needs --yes otherwise (exit 4, nothing is changed).",
                Positionals =
                [
                    new PositionalSpec("backupPath", "Storage path of the system backup archive."),
                    new PositionalSpec("component", "programs, environment, settings or devtools (default: taken from the path).", Required: false)
                ],
                Options = [StorageOption, new OptionSpec("--plan", "Show the plan only; change nothing."), new OptionSpec("--reveal", "Show environment-variable values in the plan (masked by default).")],
                Examples = ["system-restore system_backups/programs/<file> programs", "system-restore system_backups/environment/<file> environment --plan", "system-restore system_backups/environment/<file> environment --yes"],
                Mutates = true,
                Destructive = true,
                WindowsOnly = true,
                Config = ConfigRequirement.StorageOverrideOnly,
                Validate = p => p.Positionals.Count == 2
                    && !SYSTEM_COMPONENT_TYPES.Contains(p.Positionals[1], StringComparer.OrdinalIgnoreCase)
                    && !p.Positionals[1].StartsWith("system_", StringComparison.OrdinalIgnoreCase)
                    ? $"Unknown system-restore component '{p.Positionals[1]}'. Expected one of: {string.Join(", ", SYSTEM_COMPONENT_TYPES)}." : null,
                Handler = RunSystemRestoreAsync
            },
            new CommandSpec
            {
                Name = "system-undo",
                Category = CommandCategory.System,
                Summary = "Revert a system restore from its undo point, or list/delete undo points.",
                Positionals = [new PositionalSpec("undoPointDir", "Undo point folder (see --list).", Required: false)],
                Options = [new OptionSpec("--list", "List the undo points."), new OptionSpec("--delete", "Delete an undo point.", "undoPointDir")],
                Usage = ["system-undo <undoPointDir>", "system-undo --list", "system-undo --delete <undoPointDir>"],
                Examples = ["system-undo --list", "system-undo %USERPROFILE%\\ReStore\\restore-undo\\20260101-120000"],
                Mutates = true,
                Destructive = true,
                Config = ConfigRequirement.Loaded,
                Validate = p => p.Positionals.Count + (p.HasOption("--list") ? 1 : 0) + (p.HasOption("--delete") ? 1 : 0) == 1
                    ? null : "Usage: system-undo <undoPointDir> | system-undo --list | system-undo --delete <undoPointDir>",
                Handler = RunSystemUndoAsync
            },

            // ---- Configuration ----
            new CommandSpec
            {
                Name = "config show",
                Category = CommandCategory.Configuration,
                Summary = "Print config.json with secret values masked (--reveal prints it as stored).",
                Description = "Secret option values (passwords, keys, tokens) are replaced by ***; env: and dpapi: references stay visible. --redact is accepted and is the default. With --json the document has the parsed configuration under 'config'.",
                Options = [new OptionSpec("--reveal", "Print the file as stored, secrets included."), new OptionSpec("--redact", "Mask secrets (the default).")],
                Examples = ["config show", "config show --reveal"],
                Config = ConfigRequirement.Loaded,
                State = StateRequirement.None,
                PayloadOnStdout = true,
                Validate = p => p.HasOption("--redact") && p.HasOption("--reveal") ? "--redact and --reveal cannot be combined." : null,
                Handler = RunConfigAsync
            },
            new CommandSpec
            {
                Name = "config protect",
                Category = CommandCategory.Configuration,
                Summary = "Encrypt plain secret options in config.json for this Windows user (DPAPI).",
                Description = "Rewrites plain secret provider options (passwords, keys, tokens) to dpapi: values and reports which options changed (never their values). Windows only; a second run changes nothing. Use env:NAME references on other platforms.",
                Examples = ["config protect"],
                Mutates = true,
                Config = ConfigRequirement.Loaded,
                State = StateRequirement.None,
                PayloadOnStdout = true,
                Handler = RunConfigAsync
            },
            new CommandSpec
            {
                Name = "config export",
                Category = CommandCategory.Configuration,
                Summary = "Write the settings to a file with every secret removed.",
                Description = "Writes config.json plus the app preferences with every secret option value removed (env: references are kept; dpapi: blobs are removed because they only decrypt for the same Windows user).",
                Positionals = [new PositionalSpec("file", "File to write.")],
                Examples = ["config export settings.json"],
                Mutates = true,
                Config = ConfigRequirement.Loaded,
                State = StateRequirement.None,
                PayloadOnStdout = true,
                Handler = RunConfigAsync
            },
            new CommandSpec
            {
                Name = "config import",
                Category = CommandCategory.Configuration,
                Summary = "Apply settings from an exported file (validated first; --merge or --replace).",
                Description = "Validates the resulting configuration first and leaves everything untouched (exit 3) when it is invalid. --merge (the default) overlays the file on the current config; --replace replaces it. Current secrets of providers with the same name are kept. The previous config.json is saved under backups/.",
                Positionals = [new PositionalSpec("file", "File written by config export.")],
                Options = [new OptionSpec("--merge", "Overlay the file on the current config (default)."), new OptionSpec("--replace", "Replace the current config.")],
                Usage = ["config import <file> [--merge|--replace]"],
                Examples = ["config import settings.json --merge"],
                Mutates = true,
                Config = ConfigRequirement.Loaded,
                State = StateRequirement.None,
                PayloadOnStdout = true,
                Validate = p => p.HasOption("--merge") && p.HasOption("--replace") ? "--merge and --replace cannot be combined." : null,
                Handler = RunConfigAsync
            },
            new CommandSpec
            {
                Name = "config validate",
                Aliases = ["--validate-config"],
                Category = CommandCategory.Configuration,
                Summary = "Validate the whole configuration and print every error, warning and info message.",
                Description = "Exits 3 when the configuration is invalid. With --json: valid, errors, warnings and info lists. Other commands only print errors and warnings. '--validate-config' is the older spelling.",
                Examples = ["config validate", "config validate --json"],
                Config = ConfigRequirement.Loaded,
                State = StateRequirement.None,
                Handler = ValidateConfigurationAsync
            },
            new CommandSpec
            {
                Name = "probe",
                Category = CommandCategory.Configuration,
                Summary = "Prove the storage works: upload, read, list and delete a small test object.",
                Description = "Runs a real round trip under restore-probe/ (upload, size check, download and compare, find in a listing, delete, confirm gone) and prints each step with its latency. The object is always cleaned up. Exits 1 when a step fails.",
                Options = [StorageOption],
                Examples = ["probe --storage s3"],
                Config = ConfigRequirement.Storage,
                State = StateRequirement.None,
                Handler = RunProbeAsync
            },

            // ---- Automation ----
            new CommandSpec
            {
                Name = "--service",
                Category = CommandCategory.Automation,
                Summary = "Run the file watcher and scheduler in the foreground until Ctrl+C.",
                Description = "Never prompts: with encryption enabled set RESTORE_ENCRYPTION_PASSWORD (or RESTORE_ENCRYPTION_PASSWORD_FILE), otherwise it exits 1. Not available with --json.",
                SupportsJson = false,
                Mutates = true,
                ForceUnattended = true,
                RequiresServicePassword = true,
                State = StateRequirement.Mutating,
                Handler = RunServiceAsync
            },
            new CommandSpec
            {
                Name = "run-due",
                Category = CommandCategory.Automation,
                Summary = "Run one scheduler sweep (due backups, system backup, verification, retention) and exit.",
                Description = "For Windows Task Scheduler. Never prompts. A busy repository defers the work to the next run (exit 0). While protection is paused ('pause') the sweep does nothing and exits 0 (JSON: paused). It follows the per-folder schedules and the backup window of the configuration ('config set-schedule', 'config set-window'): work due outside the window waits for it to open and exits 0 (JSON: backupWindow.closed, backupWindow.deferred). Exits 1 when a backup, system backup, verification or the retention pass failed.",
                Examples = ["run-due --quiet"],
                Mutates = true,
                Destructive = true,
                ForceUnattended = true,
                RequiresServicePassword = true,
                State = StateRequirement.Mutating,
                Handler = RunDueAsync
            },
            new CommandSpec
            {
                Name = "status",
                Category = CommandCategory.Automation,
                Summary = "Show protection status: last backup per folder, next due, verification, stale folders.",
                Description = "Read-only. Exits 0 whenever the report could be produced, so monitoring should read the fields (staleGroups, repository.busy), not the exit code. Per folder: schedule (expression, description, source), nextDueUtc (when it will start, after the backup window) and waitingForWindow; also backupWindow and the system backup's schedule. 'schedule next' explains why each target is not running now.",
                Examples = ["status --json"],
                Config = ConfigRequirement.Loaded,
                Handler = RunStatusAsync
            },
        ];

        private static string? ValidateRestore(ParsedCommand parsed)
        {
            if (parsed.Options.TryGetValue("--conflict", out var conflictValues) && !TryParseConflictValue(conflictValues[^1], out _))
            {
                return "Invalid --conflict value. Expected one of: skip, overwrite, keepboth, fail.";
            }

            return ValidateMap(parsed);
        }

        private static string? ValidateMap(ParsedCommand parsed)
        {
            if (parsed.Options.TryGetValue("--map", out var mapValues))
            {
                try
                {
                    PathMapping.Parse(mapValues);
                }
                catch (ArgumentException ex)
                {
                    return ex.Message;
                }
            }

            return null;
        }

        private static string? ValidateGraceHours(ParsedCommand parsed) =>
            parsed.Options.TryGetValue("--grace-hours", out var graceValues)
            && !(double.TryParse(graceValues[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var graceHours) && graceHours >= 0)
                ? "Invalid --grace-hours value. Expected a number of hours, 0 or more."
                : null;
    }
}
