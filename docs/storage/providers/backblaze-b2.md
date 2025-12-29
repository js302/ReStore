# Backblaze B2 Setup

Backup to Backblaze B2 Cloud Storage (S3 Compatible).

## Step 1: Create a Bucket

1. Log in to Backblaze B2
2. Create a Bucket
3. Create a new **Application Key**
4. Copy the **keyID** and **applicationKey**
5. Note your **S3 Endpoint** (e.g., `s3.us-west-000.backblazeb2.com`)

## Step 2: Configure ReStore

Open `%USERPROFILE%\ReStore\config.json`:

```json
{
  "storageSources": {
    "b2": {
      "path": "backups",
      "options": {
        "accessKeyId": "your_key_id",
        "secretAccessKey": "your_application_key",
        "serviceUrl": "https://s3.us-west-000.backblazeb2.com",
        "bucketName": "your-bucket-name"
      }
    }
  }
}
```

**Configuration Parameters:**

- **path**: Prefix for files
- **accessKeyId**: Your Key ID
- **secretAccessKey**: Your Application Key
- **serviceUrl**: The S3 Endpoint URL (must start with `https://`)
- **bucketName**: Your bucket name
