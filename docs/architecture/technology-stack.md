# Technology Stack

## Runtime

| Layer           | Technology                            | Version        |
| --------------- | ------------------------------------- | -------------- |
| GUI             | WPF (Windows Presentation Foundation) | .NET 9.0       |
| Core            | .NET Class Library                    | .NET 9.0       |
| Target Platform | Windows                               | net9.0-windows |

## GUI Dependencies

| Package                  | Purpose                        |
| ------------------------ | ------------------------------ |
| WPF-UI                   | Modern UI controls and styling |
| Hardcodet.NotifyIcon.Wpf | System tray integration        |

## Core Dependencies

| Package                      | Purpose                         |
| ---------------------------- | ------------------------------- |
| System.IO.Compression        | Zip archive creation/extraction |
| System.Text.Json             | JSON serialization              |
| System.Management            | Windows WMI queries             |
| System.Security.Cryptography | AES-256-GCM, PBKDF2-SHA256      |

## Storage Provider SDKs

| Package                 | Provider                |
| ----------------------- | ----------------------- |
| AWSSDK.S3               | Amazon S3, Backblaze B2 |
| Azure.Storage.Blobs     | Azure Blob Storage      |
| Google.Apis.Drive.v3    | Google Drive            |
| Google.Cloud.Storage.V1 | Google Cloud Storage    |
| Dropbox.Api             | Dropbox                 |
| SSH.NET                 | SFTP                    |
| Octokit                 | GitHub API              |

## Build & Packaging

| Tool         | Purpose                       |
| ------------ | ----------------------------- |
| MSBuild      | Build system                  |
| .NET SDK 9.0 | Compilation and runtime       |
| NuGet        | Package management            |
| MSIX         | Windows application packaging |

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

## GUI Build Integration

The GUI build process automatically copies the CLI to `bin\Debug\cli\restore.exe` via a custom MSBuild target in [ReStore.csproj](../../ReStore/ReStore.csproj). When editing CLI functionality, rebuild both projects.
