# Google Cloud Storage (GCP) Setup

Backup to Google Cloud Storage buckets.

## Step 1: Create a Service Account

1. Go to the [Google Cloud Console](https://console.cloud.google.com/)
2. Select your project
3. Go to **IAM & Admin** > **Service Accounts**
4. Click **Create Service Account**
5. Name it (e.g., "restore-backup-sa") and click **Create and Continue**
6. Grant the role **Storage Object Admin** (allows reading/writing objects)
7. Click **Done**

## Step 2: Generate Key File

1. Click on the newly created service account
2. Go to the **Keys** tab
3. Click **Add Key** > **Create new key**
4. Select **JSON** and click **Create**
5. Save the downloaded JSON file to a secure location (e.g., `C:\Users\YourName\ReStore\gcp-key.json`)

## Step 3: Create a Bucket

1. Go to **Cloud Storage** > **Buckets**
2. Click **Create**
3. Name your bucket (must be globally unique)
4. Choose region and storage class
5. Click **Create**

## Step 4: Configure ReStore

Open `%USERPROFILE%\ReStore\config.json`:

```json
{
  "storageSources": {
    "gcp": {
      "path": "backups",
      "options": {
        "bucketName": "your-bucket-name",
        "credentialPath": "C:\\Users\\YourName\\ReStore\\gcp-key.json"
      }
    }
  }
}
```

**Configuration Parameters:**

- **path**: Prefix for objects (default: `"backups"`)
- **bucketName**: The name of your GCS bucket
- **credentialPath**: Absolute path to the service account JSON key file
