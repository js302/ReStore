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
   - Add scopes: `https://www.googleapis.com/auth/drive.file`
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
      "path": "./backups",
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

- **path**: Relative path for organizing backups (default: `"./backups"`)
- **client_id**: From the downloaded JSON file (`client_id` field)
- **client_secret**: From the downloaded JSON file (`client_secret` field)
- **token_folder**: Where ReStore stores authentication tokens (recommended: `C:\Users\YourName\ReStore\tokens`)
- **backup_folder_name**: The name of the folder in Google Drive where backups will be stored (default: `"ReStoreBackups"`)

## Step 5: First Authentication

When you run your first backup, ReStore will:

1. Open a browser window for Google authentication
2. Ask you to sign in to your Google account
3. Request permission to access files it creates in Google Drive
4. Save the authentication token for future use

**Notes:**

- The app will only access files it creates, not your existing Drive files
- Tokens are stored locally and can be revoked from your [Google Account settings](https://myaccount.google.com/permissions)
- If the app is in "Testing" mode, you may need to re-authenticate periodically

**Storage Limits (Free Plan):**

- **15 GB** of free storage shared across Google Drive, Gmail, and Google Photos
- Files can be up to **5 TB** in size (but will count against your quota)
- To get more storage, consider [Google One](https://one.google.com/) plans starting at 100 GB
