# Technology Stack

## Runtime

| Layer           | Technology                            | Version         |
| --------------- | ------------------------------------- | --------------- |
| GUI             | WPF (Windows Presentation Foundation) | .NET 10.0       |
| Core            | .NET Class Library                    | .NET 10.0       |
| Target Platform | Windows                               | net10.0-windows |

## GUI Dependencies

| Package                  | Purpose                        |
| ------------------------ | ------------------------------ |
| WPF-UI                   | Modern UI controls and styling |
| Hardcodet.NotifyIcon.Wpf | System tray integration        |

## Core Dependencies

| Package                      | Purpose                                   |
| ---------------------------- | ----------------------------------------- |
| System.IO.Compression        | System backup archive creation/extraction |
| System.Text.Json             | JSON serialization                        |
| System.Security.Cryptography | AES-256-GCM, PBKDF2-SHA256, chunk hashing |

## Storage Provider SDKs

| Package                 | Provider                |
| ----------------------- | ----------------------- |
| AWSSDK.S3               | Amazon S3, Backblaze B2 |
| Azure.Storage.Blobs     | Azure Blob Storage      |
| Google.Apis.Drive.v3    | Google Drive            |
| Google.Apis.Auth         | Google OAuth (Drive)    |
| Google.Cloud.Storage.V1 | Google Cloud Storage    |
| Dropbox.Api             | Dropbox                 |
| SSH.NET                 | SFTP                    |
| Octokit                 | GitHub API              |

## Build & Packaging

| Tool          | Purpose                       |
| ------------- | ----------------------------- |
| MSBuild       | Build system                  |
| .NET SDK 10.0 | Compilation and runtime       |
| NuGet         | Package management            |
| MSIX          | Windows application packaging |

## Build Commands

```powershell
# Build entire solution
dotnet build ReStore.sln

# Run GUI
dotnet run --project ReStore

# Run CLI
dotnet run --project ReStore.Core -- --help

# Run tests
dotnet test ReStore.Tests
```

## Multi-Architecture Builds

Both projects declare `RuntimeIdentifiers=win-x64;win-arm64`. `ReStore.Core` is an executable
project that the WPF host references as a shared implementation assembly, so `ReStore.csproj`
sets `ValidateExecutableReferencesMatchSelfContained=false` and forwards its RID to the
reference. `build-msix.ps1` packages both architectures, and the MSIX registers the `restore`
execution alias that puts the CLI on `PATH`.
