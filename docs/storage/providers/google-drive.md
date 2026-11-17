# Google Drive Setup

To use Google Drive as your backup destination, you'll need to create a Google Cloud project and obtain OAuth 2.0 credentials.

## Step 1: Create a Google Cloud Project

1. Go to the [Google Cloud Console](https://console.cloud.google.com/)
2. Click **"New Project"** (or select an existing project)
3. Name your project (e.g., "ReStoreBackups") and click **Create**

## Step 2: Enable the Google Drive API

1. In your project, navigate to **APIs & Services** > **Library**
2. Search for **"Google Drive API"**
3. Click on it and press **Enable**

## Step 3: Create OAuth 2.0 Credentials

1. Go to **APIs & Services** > **Credentials**
2. Click **Create Credentials** > **OAuth client ID**
3. If prompted, configure the OAuth consent screen:
   - User Type: **External** (unless you have a Google Workspace)
   - App name: **ReStore** (or your preferred name)
   - User support email: Your email
   - Developer contact: Your email
   - Add scopes: `https://www.googleapis.com/auth/drive` (full Drive scope, which ReStore requests)
   - Add your email as a test user
4. Back in Credentials, click **Create Credentials** > **OAuth client ID**
5. Application type: **Desktop app**
6. Name: **ReStore Client**
7. Click **Create**
8. Download the JSON file containing your credentials

## Step 4: Configure ReStore

Open `%USERPROFILE%\ReStore\config.json` and configure the Google Drive section:

```json
{
  "storageSources": {
    "gdrive": {
      "options": {
        "client_id": "client_id",
        "client_secret": "client_secret",
        "token_folder": "token_folder",
        "backup_folder_name": "ReStoreBackups"
      }
    }
  }
}
```

**Configuration Parameters:**

- **client_id**: From the downloaded JSON file (`client_id` field)
- **client_secret**: From the downloaded JSON file (`client_secret` field)
- **token_folder**: Where ReStore stores authentication tokens (recommended: `C:\Users\YourName\ReStore\tokens`). If omitted it defaults to the relative folder `Drive.Storage.Token`, which resolves against the process working directory, so set it explicitly
- **client_secret** may be a `dpapi:` or `env:NAME` reference (see [Secrets in config.json](../../configuration.md#secrets-in-configjson))
- **Token cache**: on Windows the sign-in tokens are stored DPAPI-encrypted (`*.dpapi` files) in `token_folder`; a relative `token_folder` now resolves under `%USERPROFILE%\ReStore\tokens`. Tokens left by an older version in the plaintext Google store are migrated and deleted automatically on first use. The migration is one-way: the plaintext copy is deleted, so an older ReStore version would need a fresh sign-in. If the cache was written by another Windows user or machine, interactive runs ask you to sign in again and unattended runs (`--service`) fail with a clear message until you do.
- **backup_folder_name**: The name of the folder in Google Drive where backups will be stored (default: `"ReStoreBackups"`)

## Step 5: First Authentication

When you run your first backup, ReStore will:

1. Open a browser window for Google authentication
2. Ask you to sign in to your Google account
3. Request permission to see, edit, create and delete files in your Google Drive (the full `drive` scope)
4. Save the authentication token for future use

**From the command line (scripts and AI agents):** configure the credentials without editing the file, then sign in once:

```bash
echo %CLIENT_SECRET% | restore config set-storage gdrive --option client_id=YOUR_ID --option-stdin client_secret --protect --default
restore auth gdrive               # opens a browser; --no-browser prints the URL for a person to open on this computer
restore auth status               # configured? token present?
restore probe --storage gdrive
```

`auth gdrive` runs the same sign-in as the first backup and stores the token where the storage finds it. Without a terminal
(non-interactive) it exits `4` unless `--no-browser` is given, because a person has to approve access in a browser.

**Notes:**

- ReStore requests the full Drive scope (`https://www.googleapis.com/auth/drive`). It only reads and writes inside its backup folder, but the permission Google asks you to grant covers your whole Drive
- The sign-in wait times out after 5 minutes. A scheduled/unattended run that needs a new sign-in fails with an "authorization required" error instead of hanging; run ReStore interactively once to re-authenticate
- Transient errors (rate limits, 5xx, network faults) are retried up to 6 times with exponential backoff
- Tokens are stored locally and can be revoked from your [Google Account settings](https://myaccount.google.com/permissions)
- If the app is in "Testing" mode, you may need to re-authenticate periodically

**Storage Limits (Free Plan):**

- **15 GB** of free storage shared across Google Drive, Gmail, and Google Photos
- Files can be up to **5 TB** in size (but will count against your quota)
- To get more storage, consider [Google One](https://one.google.com/) plans starting at 100 GB

## Optional: `prefix` option

Add `"prefix": "my-laptop"` to this source's `options` to keep the repository under a sub-folder of the ReStore backup folder. It is applied to every path (uploads, downloads, listing, sharing) and is empty by default, so existing repositories are unaffected. It must not start with `/` or contain `..`. Changing it later points ReStore at a different (empty) repository; it does not move existing data.

Remote sources do not need a top-level `"path"`. If one is present it is ignored (ReStore logs a warning); use `prefix` instead.
