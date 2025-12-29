# Azure Blob Storage Setup

Backup to Microsoft Azure Blob Storage.

## Step 1: Create a Storage Account

1. Go to the [Azure Portal](https://portal.azure.com/)
2. Create a new **Storage account**
3. Go to **Access keys** under "Security + networking"
4. Copy the **Connection string**

## Step 2: Configure ReStore

Open `%USERPROFILE%\ReStore\config.json`:

```json
{
  "storageSources": {
    "azure": {
      "path": "backups",
      "options": {
        "connectionString": "DefaultEndpointsProtocol=https;AccountName=...",
        "containerName": "restore-backups"
      }
    }
  }
}
```

**Configuration Parameters:**

- **path**: Prefix for blobs (default: `"backups"`)
- **connectionString**: The connection string from Step 1
- **containerName**: The name of the container to use (will be created if it doesn't exist)
