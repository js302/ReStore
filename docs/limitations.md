# Limitations

- System backup is Windows-only
- Automatic program restoration requires Winget
- Some programs may install newer versions than what was backed up
- Windows settings backup captures registry-based settings only; some settings stored in other locations may not be included
- WiFi passwords and network credentials are not included in the settings backup
- The `Differential` backup mode currently selects whole files changed since the last full backup for a watched directory; it is not a true binary delta backup
- `ReStore.Core/src/core/DiffManager.cs` is currently an experimental prototype and is not wired into the production backup/upload flow
