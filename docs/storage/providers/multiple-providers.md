# Using Multiple Storage Providers

You can configure multiple storage sources in the same `config.json` file. ReStore supports all storage providers simultaneously with flexible per-path and per-component routing:

```json
{
  "globalStorageType": "local",
  "watchDirectories": [
    {
      "path": "C:\\Users\\YourName\\Documents",
      "storageType": "gdrive"
    },
    {
      "path": "C:\\Users\\YourName\\Desktop",
      "storageType": "azure"
    },
    {
      "path": "C:\\Users\\YourName\\Pictures",
      "storageType": "sftp"
    }
  ],
  "systemBackup": {
    "enabled": true,
    "programsStorageType": "github",
    "environmentStorageType": "local",
    "settingsStorageType": "dropbox"
  },
  "storageSources": {
    "gdrive": {
      "path": "./backups",
      "options": {
        "client_id": "your_client_id",
        "client_secret": "your_client_secret",
        "token_folder": "your_token_folder",
        "backup_folder_name": "ReStoreBackups"
      }
    },
    "gcp": {
      "path": "backups",
      "options": {
        "bucketName": "your-bucket-name",
        "credentialPath": "C:\\Users\\YourName\\ReStore\\gcp-key.json"
      }
    },
    "azure": {
      "path": "backups",
      "options": {
        "connectionString": "...",
        "containerName": "restore-backups"
      }
    },
    "sftp": {
      "path": "/home/user/backups",
      "options": {
        "host": "sftp.example.com",
        "username": "user",
        "password": "password"
      }
    },
    "local": {
      "path": "%USERPROFILE%\\ReStoreBackups",
      "options": {}
    }
  }
}
```

## Storage Selection Logic

- **Per-Path**: Each watched directory can specify its own `storageType`, or use `null` to fall back to global default
- **Per-Component**: System backups (programs, environment, settings) can each use different storage destinations
- **Global Fallback**: The `globalStorageType` is used when no specific storage is configured

## CLI Usage

When using the CLI, you can override storage selection with the `--storage` flag:

```bash
# Backup to configured storage (from config.json)
restore backup "C:\Users\YourName\Documents"

# Override storage type for this backup
restore backup "C:\Users\YourName\Documents" --storage gdrive

# System backup with storage override
restore system-backup all --storage s3
restore system-backup programs --storage github
```

## GUI Usage

In the Settings page, you can:

- Set the global default storage in the "Global Default Storage" dropdown
- Select storage per watched directory in each directory's storage dropdown
- Use "Apply Current Global Storage to All Paths" to quickly set all directories
- Configure system backup storage separately for programs, environment, and settings
