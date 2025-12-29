# Local Storage Setup

Local storage is the simplest option and requires no additional setup beyond specifying the backup directory path.

**Configuration:**

In `%USERPROFILE%\ReStore\config.json`, configure the local storage source:

```json
{
  "storageSources": {
    "local": {
      "path": "%USERPROFILE%\\ReStoreBackups",
      "options": {}
    }
  }
}
```

**Configuration Parameters:**

- **path**: The directory where backups will be stored
- **options**: Empty object (no additional options needed for local storage)

**Notes:**

- The path can be on a local drive, external drive, or network share
- You can use environment variables like `%USERPROFILE%`, `%APPDATA%`, etc.
- Ensure the directory has sufficient free space for your backups
- For network shares, use UNC paths like `\\server\share\backups`
