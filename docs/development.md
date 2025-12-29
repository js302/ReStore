# Development

## Project Structure

```
ReStore/
├── ReStore.Core/             # Core CLI application
|   |
│   ├── Program.cs            # CLI entry point
│   ├── src/
│   │   ├── core/             # Backup, restore, and state management
│   │   ├── storage/          # Storage provider implementations
|   |   ├── sharing/          # File sharing functionality
│   │   ├── monitoring/       # File watching and change detection
│   │   ├── utils/            # Configuration, logging, and utilities
│   │   └── backup/           # System backup functionality
│   └── config/               # Configuration files
│
└── ReStore/                  # WPF GUI application
    |
    ├── App.xaml              # Application entry point
    ├── Assets/               # Icons, images, and resources
    ├── Interop/              # Interop services (tray, notifications)
    ├── Views/                # UI pages (Dashboard, Backups, Settings)
    ├── Services/             # GUI services (theme, tray, settings)
    └── config/               # GUI configuration files
```

## Development

The project is built with .NET 9.0 and uses:

- WPF for the GUI with the WPF-UI library
- JSON for configuration
- SHA256 hashing for change detection
- ZIP compression for backups

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
