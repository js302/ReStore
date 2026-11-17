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
      "options": {
        "keyId": "your_key_id",
        "applicationKey": "your_application_key",
        "serviceUrl": "https://s3.us-west-000.backblazeb2.com",
        "bucketName": "your-bucket-name"
      }
    }
  }
}
```

**Configuration Parameters:**

- **keyId**: Your Key ID (the B2 `keyID`)
- **applicationKey**: Your Application Key
- **serviceUrl**: The S3 Endpoint URL (must start with `https://`)
- **bucketName**: Your bucket name

Earlier versions of this page listed `accessKeyId` / `secretAccessKey`; ReStore never read those names, so use `keyId` / `applicationKey`.

## Optional: `prefix` option

Add `"prefix": "my-laptop"` to this source's `options` to keep the repository under a sub-folder of the bucket. It is applied to every path (uploads, downloads, listing, sharing) and is empty by default, so existing repositories are unaffected. It must not start with `/` or contain `..`. Changing it later points ReStore at a different (empty) repository; it does not move existing data.

Remote sources do not need a top-level `"path"`. If one is present it is ignored (ReStore logs a warning); use `prefix` instead.
