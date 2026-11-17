# Installation

## Download Pre-built Installer (Recommended)

1. **[Download the latest MSIX installer](https://github.com/js302/ReStore/releases/latest)**
2. **Double-click the MSIX file** to install ReStore

ReStore checks GitHub for new versions and can update itself (`restore update install`, or **Update now** in the app); see [Updates](updates.md).

## Portable CLI (no install, no admin)

If you only need the command line (scripts, servers, AI agents), install the portable `restore.exe`. It needs no administrator
rights, no certificate and no MSIX package:

```powershell
irm https://github.com/js302/ReStore/releases/latest/download/install.ps1 | iex
```

The script reads `latest.json` of the release, downloads the zip for your processor (x64 or arm64), verifies its SHA-256, unpacks it
to `%LOCALAPPDATA%\Programs\ReStore\cli` and puts that folder at the front of your user `PATH`. Open a new terminal afterwards and run
`restore --version`. Your configuration, state and logs live in `%USERPROFILE%\ReStore` and are never touched by the script. The
portable copy updates itself with `restore update install` (see [Updates](updates.md)).

### Options of `install.ps1`

| Parameter | Environment variable (for the one-liner) | Meaning |
|-----------|------------------------------------------|---------|
| `-Version 1.8.0` | `RESTORE_INSTALL_VERSION` | Install this version instead of the latest |
| `-InstallDir <dir>` | `RESTORE_INSTALL_DIR` | Install here instead of `%LOCALAPPDATA%\Programs\ReStore\cli` |
| `-NoPathUpdate` | `RESTORE_INSTALL_NO_PATH=1` | Do not touch the user `PATH` |
| `-Uninstall` | | Remove the installed files and the `PATH` entry (configuration and backups stay) |
| `-ManifestUrl <url>` | | Read the release description from another URL (mirrors, testing) |

The one-liner cannot take parameters, so use the environment variables with it, or download the script and pass parameters:

```powershell
$env:RESTORE_INSTALL_VERSION = "1.8.0"; irm https://github.com/js302/ReStore/releases/latest/download/install.ps1 | iex
& ([scriptblock]::Create((irm https://github.com/js302/ReStore/releases/latest/download/install.ps1))) -Version 1.8.0 -NoPathUpdate
```

### Manual zip install

Download `ReStore-cli_<version>_win-<arch>.zip` from the [releases page](https://github.com/js302/ReStore/releases/latest), check its
SHA-256 against `latest.json`, and unzip it into any folder (for example `C:\Tools\ReStore`). Keep `ReStore.channel` next to `restore.exe`:
it marks the copy as portable, which is how `restore version` and `restore update install` know to update it in place. Add the folder to
your `PATH` yourself (or call `restore.exe` by its full path). Remove it by deleting the folder.

### Portable CLI and the MSIX alias together

The MSIX install provides `restore` through an app execution alias in `%LOCALAPPDATA%\Microsoft\WindowsApps`. If both are installed, the
first one on `PATH` wins. `install.ps1` puts its folder at the front of your user `PATH`, so the portable copy normally wins; with a manual
install the order is whatever you chose. `restore doctor` shows which one wins and warns when two copies are found, because they can be
different versions. To use only one, run `install.ps1 -Uninstall` (portable) or switch `restore.exe` off in
**Settings, Apps, Advanced app settings, App execution aliases** (MSIX alias).

## Prerequisites

- Windows 10 version 1809 (build 17763) or later
- ~250MB free disk space

## Build from Source

**Prerequisites:**

- .NET 10.0 SDK or later
- Windows OS (Windows 10 or later recommended)

1. Clone the repository:

   ```bash
   git clone https://github.com/js302/ReStore.git
   cd ReStore
   ```

2. Build the solution:

   ```bash
   dotnet build ReStore.sln
   ```

3. Run the GUI application:

   ```bash
   dotnet run --project ReStore
   ```

## Troubleshooting

### Restored files do not appear under `%APPDATA%` or `%LOCALAPPDATA%` (Store / MSIX install)

The MSIX version of ReStore runs with Windows file-system virtualization. When a packaged app writes under
`%APPDATA%` (`C:\Users\<you>\AppData\Roaming`) or `%LOCALAPPDATA%` (`C:\Users\<you>\AppData\Local`), Windows redirects
the write into the app's private copy of that folder, at
`%LOCALAPPDATA%\Packages\<package family name>\LocalCache\Roaming` (or `\Local`). Other programs do not see those files.

This affects restores **into** those folders and anything else ReStore writes there:

- **Restoring into AppData:** pick a different target folder (for example under `Documents` or the Desktop) and copy
  the files across yourself, or use the installer (non-MSIX) build of ReStore. The restore dialog shows a warning when
  the destination is under AppData and the app is packaged.
- **`theme.json`:** the theme preference is stored in `%LOCALAPPDATA%\ReStore\theme.json`. In the MSIX version that
  path is virtualized, so the real file is under
  `%LOCALAPPDATA%\Packages\<package family name>\LocalCache\Local\ReStore\theme.json`. Deleting it resets the theme to
  "System".
- Your backups, `config.json`, logs and `appsettings.json` live under `%USERPROFILE%\ReStore`, which is not
  virtualized; the backup *source* folders are read normally.

If the data root is overridden with the `RESTORE_HOME` environment variable, `theme.json` moves to the `local` folder
under that root.
