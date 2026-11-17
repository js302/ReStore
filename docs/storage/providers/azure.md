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
      "options": {
        "connectionString": "DefaultEndpointsProtocol=https;AccountName=...",
        "containerName": "restore-backups"
      }
    }
  }
}
```

**Configuration Parameters:**

- **connectionString**: The connection string from Step 1
- **containerName**: The name of the container to use (will be created if it doesn't exist)

## Optional: `prefix` option

Add `"prefix": "my-laptop"` to this source's `options` to keep the repository under a sub-folder of the container. It is applied to every path (uploads, downloads, listing, sharing) and is empty by default, so existing repositories are unaffected. It must not start with `/` or contain `..`. Changing it later points ReStore at a different (empty) repository; it does not move existing data.

Remote sources do not need a top-level `"path"`. If one is present it is ignored (ReStore logs a warning); use `prefix` instead.
