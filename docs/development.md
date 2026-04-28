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
│   ├── Interop/              # Interop services (tray, notifications)
│   ├── Views/                # UI pages (Dashboard, Backups, Settings)
│   │   ├── Pages/            # Navigation pages
│   │   └── Windows/          # Dialog windows
│   └── Services/             # GUI services (theme, tray, settings)
│
└── ReStore.Tests/            # Unit and integration tests
```

## Development

The project is built with .NET 9.0 and uses:

- WPF for the GUI with the WPF-UI library
- JSON for configuration
- SHA256 hashing for change detection
- Content-defined chunking and snapshot manifests for user-file backups
- ZIP compression for system backup components

To extend storage support, implement the `IStorage` interface and register your provider in the configuration.

## Testing

The repository includes an automated test project at `ReStore.Tests` (xUnit) that covers core backup/restore behavior (including encryption and fuzz/integration scenarios).

Run the full test suite:

```bash
dotnet test ReStore.Tests/ReStore.Tests.csproj
```

Or run tests for the entire solution:

```bash
dotnet test ReStore.sln
```

## Diffing Status

The repository currently contains two different concepts that are easy to confuse:

- `ChunkSnapshot` in the live backup flow creates point-in-time manifests and reuses previously uploaded content-addressed chunks.
- `verify` is a first-class CLI command for validating a manifest, its chunks, and the reconstructed file hashes without restoring content.
- `DiffManager` is an experimental binary diff prototype that can create and apply patch blobs in isolation, but it is not used by `Backup`, `Restore`, `FileWatcher`, or any storage provider in production.

If you work on diffing features, be explicit about whether you mean production chunk-manifest snapshots or experimental binary patch-chain research.
