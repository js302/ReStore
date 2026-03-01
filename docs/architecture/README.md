# ReStore Architecture

This section documents the technical architecture of ReStore, a Windows backup and restore solution with dual interfaces: a WPF GUI and CLI.

## Overview

ReStore consists of two .NET 9.0 projects:

- **ReStore** (GUI): Modern WPF application with dashboard, backup browser, and settings management
- **ReStore.Core** (Library/CLI): Core backup functionality with multiple storage backends

Both share a unified configuration system stored at `%USERPROFILE%\ReStore\config.json`.

## Documentation Structure

- [Component Overview](components.md) - High-level component diagram and relationships
- [Data Flows](data-flows.md) - Backup, restore, and encryption data flow diagrams
- [Encryption Architecture](encryption.md) - Security model and cryptographic details
- [Configuration & State](configuration-state.md) - File formats and persistence layer
- [Technology Stack](technology-stack.md) - Dependencies and frameworks

## Quick Reference

### Project Structure

```
ReStore/                     # WPF GUI Application
├── Views/
│   ├── Pages/               # Main navigation pages
│   └── Windows/             # Dialog windows
├── Services/                # GUI services
└── Interop/                 # Windows-specific interop

ReStore.Core/                # Core Library + CLI
└── src/
    ├── backup/              # Backup management
    ├── core/                # Core backup/restore logic
    ├── monitoring/          # File watching
    ├── sharing/             # File sharing
    ├── storage/             # Storage providers
    └── utils/               # Utilities and configuration
```

### Key Files

| File                | Purpose                          |
| ------------------- | -------------------------------- |
| `config.json`       | Application configuration        |
| `system_state.json` | Backup history and file metadata |
| `.enc.meta`         | Encryption metadata per backup   |
| `appsettings.json`  | GUI-specific settings            |
| `theme.json`        | Theme preferences                |
