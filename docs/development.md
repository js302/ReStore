# Development

## Project Structure

```
ReStore/
├── ReStore.Core/             # Core CLI application
│   │
│   ├── Program.cs            # CLI entry point
│   ├── src/
│   │   ├── core/             # Backup, restore, and state management
│   │   ├── storage/          # Storage provider implementations
│   │   ├── sharing/          # File sharing functionality
│   │   ├── monitoring/       # File watching and change detection
│   │   ├── utils/            # Configuration, logging, and utilities
│   │   └── backup/           # System backup functionality
│   └── config/               # Configuration files
│
├── ReStore/                  # WPF GUI application
│   │
│   ├── App.xaml              # Application entry point
│   ├── Assets/               # Icons, images, and resources
│   ├── Interop/              # Win32 interop (window backdrop, maximised bounds)
│   ├── Views/                # UI pages (Dashboard, Backups, Settings)
│   │   ├── Pages/            # Navigation pages
│   │   └── Windows/          # Dialog windows
│   └── Services/             # GUI services (config/state sharing, theme, tray, notifications)
│
└── ReStore.Tests/            # Unit and integration tests
```

## Development

The project is built with .NET 10.0 and uses:

- WPF for the GUI with the WPF-UI library
- JSON for configuration
- SHA256 hashing for change detection
- Content-defined chunking and snapshot manifests for user-file backups
- ZIP compression for system backup components

To extend storage support, implement the `IStorage` interface and register your provider in
`StorageFactory`, then add its config block to `config.example.json`.

### Line Endings

The repository ships a `.gitattributes` that normalises line endings (stored LF, checked
out CRLF for C#/XAML/project/PowerShell files). Without it, an editor rewriting the tree
as CRLF makes every tracked file appear modified, which buries real changes in a
thousands-of-lines diff. If `git status` ever shows unexpected whole-file changes, check
`git diff --ignore-cr-at-eol` — if that output is empty, the diff is purely line endings.

### Multi-Architecture Builds

Both projects declare `RuntimeIdentifiers=win-x64;win-arm64`. `ReStore.Core` is an
executable project that the WPF host references as a shared implementation assembly, so
`ReStore.csproj` sets `ValidateExecutableReferencesMatchSelfContained=false` and forwards
its RID to the reference. `build-msix.ps1` packages both architectures.

```bash
dotnet build ReStore/ReStore.csproj -r win-x64
dotnet build ReStore/ReStore.csproj -r win-arm64
```

### Versioning and Publish Output

The version, authors, company and copyright are defined once in `Directory.Build.props` at the repository root and apply to every project. `ReStore.csproj` fails the build if the `Identity` version in `Package.appxmanifest` or the `assemblyIdentity` version in `app.manifest` is not `<Version>.0`, so bump all three together (the MSIX build script stamps the manifest again at pack time). `Optimize`, `DebuggerSupport` and `EventSourceSupport=false` apply to Release builds only.

`config.example.json` has one copy, `ReStore.Core/config/config.example.json`; it flows to the GUI output through the project reference.

Hardcodet.NotifyIcon.Wpf declares a framework reference to all of `Microsoft.WindowsDesktop.App`, which would add about 30 MB of WinForms assemblies to a self-contained publish although only `System.Drawing.Common` is used. `dotnet publish ReStore/ReStore.csproj -c Release -r win-x64` removes `System.Windows.Forms*`, `WindowsFormsIntegration`, `System.Design`, `System.Drawing.Design`, `System.Windows.Controls.Ribbon` and `Microsoft.VisualBasic*`, and fails if any WinForms assembly is still in the output folder. Do not enable `PublishTrimmed` (unsupported for WPF).

## Testing

Tests never touch your real profile: `AppPaths` resolves its root from the `RESTORE_HOME` environment variable (default `%USERPROFILE%\ReStore`) and the test project overrides it with a temporary directory. You can set `RESTORE_HOME` yourself to run the CLI against a scratch data directory.

The repository includes an automated test project at `ReStore.Tests` (xUnit) that covers core backup/restore behavior (including encryption and fuzz/integration scenarios).

Run the full test suite:

```bash
dotnet test ReStore.Tests/ReStore.Tests.csproj
```

Or run tests for the entire solution:

```bash
dotnet test ReStore.sln
```

### Test categories and where they run

Tests carry an xUnit trait when they are slow or need something special; the default run (and the PR jobs) exclude the slow ones.

| Category | What | Run locally | Where CI runs it |
|---|---|---|---|
| (none) | Unit, integration and CLI tests | `dotnet test ReStore.Tests --filter "Category!=Perf&Category!=Fuzz&Category!=Compat&Category!=E2E&Category!=Heavy"` | `build-test` (Windows, with coverage) and `linux-tests` (Ubuntu, with the coverage floor) |
| `Perf` | Throughput floors, allocation bounds, a 1 GB streamed file and 50k small files with generous time/memory budgets | `RESTORE_PERF_TESTS=1 dotnet test ReStore.Tests --filter "Category=Perf" --logger "console;verbosity=detailed" -- xUnit.ParallelizeTestCollections=false` (the numbers print as `BENCHMARK` / `PERF` lines; skipped unless `RESTORE_PERF_TESTS=1`; run it serially so timings and allocations are not shared with a sibling test) | `nightly.yml` (Ubuntu, daily + manual) |
| `Fuzz`, `Compat` | Parser/property tests; the compatibility corpus and old-CLI data | `dotnet test ReStore.Tests --filter "Category=Fuzz"` (or `Compat`) | `nightly.yml` |
| `E2E` | Real-process CLI tests | see the E2E project | its own CI job |
| Heavy (`RESTORE_HEAVY_TESTS=1`) | Very large state-file tests | `RESTORE_HEAVY_TESTS=1 dotnet test ReStore.Tests --filter SnapshotChunkListTests` | `nightly.yml` perf job sets the variable |
| Windows system tests | Real Task Scheduler, `reg.exe`, DPAPI, user environment variables, `ProcessRunner` quoting/timeouts/tree kill | PowerShell: `$env:RESTORE_WINDOWS_SYSTEM_TESTS=1; dotnet test ReStore.Tests --filter "FullyQualifiedName~WindowsTests"` | `build-test` (Windows) sets the variable |

Platform-specific tests use `[WindowsFact]` / `[WindowsTheory]`, `[NonWindowsFact]` and `[PosixPermissionsFact]` (`ReStore.Tests/TestAttributes.cs`), so a test that cannot run on the current OS shows up as **skipped**, never as a silent pass. The Windows system tests use `[WindowsSystemFact]` and additionally need `RESTORE_WINDOWS_SYSTEM_TESTS=1` because they create and delete real objects (a task under `\ReStoreTests\`, keys under `HKCU\Software\ReStoreTests`, `RESTORE_TEST_SYS_*` user variables); each cleans up in a `finally`.

CI quality gates (`.github/workflows/`): every build uses `-warnaserror` (GUI x64 and arm64, Core, tests); `dotnet test` uses `--blame-hang-timeout 5m`; the Linux job fails below a Core line-coverage floor (85.5 %, measured 86.48 %) and the Windows job below a loose 80 % (ratchet both up as coverage grows); `vulnerable-packages` fails on any High/Critical advisory (`dotnet list package --vulnerable --include-transitive`); `codeql.yml` runs CodeQL (C#, `security-extended`, manual build of Core + tests on Ubuntu and the GUI on Windows); Dependabot proposes weekly NuGet and GitHub Actions updates. To measure the current coverage: `dotnet test ReStore.Tests --collect:"XPlat Code Coverage"` and read `line-rate` in the cobertura file, or run `generate-test-coverage.ps1`.

### Running provider conformance tests against emulators

`StorageConformanceTests` is the behavioural contract every storage provider must satisfy. `EnvConfiguredStorageConformanceTests` runs it against a real provider selected by environment variables, and passes vacuously when they are not set, so a plain `dotnet test` never needs credentials or network:

- `RESTORE_CONFORMANCE_TYPE`: `s3`, `b2`, `azure`, `gcp`, `sftp`, `dropbox`, `gdrive`, `github` or `local`.
- `RESTORE_CONFORMANCE_OPTIONS`: JSON object of that provider's options, as in `config.json`.

`ReStore.Tests/emulators/run-conformance.sh` starts a Docker emulator on a free localhost port for each provider, runs the suite against it and removes the container again (it needs Docker, Python 3 and the .NET SDK):

```bash
ReStore.Tests/emulators/run-conformance.sh              # s3 b2 azure gcp sftp
ReStore.Tests/emulators/run-conformance.sh azure gcp    # a subset
COVERAGE_DIR=/tmp/cov-conf ReStore.Tests/emulators/run-conformance.sh s3   # also collect cobertura coverage
IMAGE_PREFIX=mirror.gcr.io/ ReStore.Tests/emulators/run-conformance.sh      # pull Docker Hub images through a mirror
```

| Provider | Emulator image | Notes |
|----------|----------------|-------|
| `s3`, `b2` | `adobe/s3mock` | S3 and Backblaze B2 both speak the S3 API; the optional `serviceUrl` option points them at the emulator (path-style addressing). |
| `azure` | `mcr.microsoft.com/azure-storage/azurite` | The well-known `devstoreaccount1` connection string with a custom `BlobEndpoint`. |
| `gcp` | `fsouza/fake-gcs-server` | Reached through `STORAGE_EMULATOR_HOST` (set to `http://127.0.0.1:<port>/storage/v1/`), which the default-credential client honours. |
| `sftp` | `linuxserver/openssh-server` | Password login; the server key is trusted on first use. |

To run one provider by hand, set the two variables and filter the suite, for example `RESTORE_CONFORMANCE_TYPE=azure RESTORE_CONFORMANCE_OPTIONS='{...}' dotnet test ReStore.Tests --filter EnvConfiguredStorageConformanceTests`. Dropbox, Google Drive and GitHub have no emulator; they are covered by unit tests with fake HTTP handlers, and can be run through the same variables against a real throwaway account. CI runs the five emulator-backed providers in the `storage-conformance` job and uploads their coverage as artifacts.

## Diffing Status

- `ChunkSnapshot` in the live backup flow creates point-in-time manifests and reuses previously uploaded content-addressed chunks.
- `verify` is a first-class CLI command for validating a manifest, its chunks, and the reconstructed file hashes without restoring content.

Content-defined chunking in `ChunkingService` handles insertions and deletions well because boundaries follow content, so an edit shifts only the chunks it touches and dedup works across files and snapshots. (An earlier standalone binary-diff prototype, `DiffManager`, was removed.)
