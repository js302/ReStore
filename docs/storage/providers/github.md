# GitHub Storage Setup

Use a GitHub repository as a storage backend, which gets you version history over your backups for free. Suited to config files and small backups only — see the size limits below.

## Step 1: Create a GitHub Account

1. Go to [GitHub](https://github.com/)
2. Sign up for a free account if you don't have one

## Step 2: Create a Private Repository

1. Click the **+** icon in the top-right corner
2. Select **New repository**
3. Configure your repository:
   - **Repository name**: `restore-backups` (or your preferred name)
   - **Visibility**: **Private** (recommended for backups)
   - **Initialize**: You can add a README if desired
4. Click **Create repository**

## Step 3: Generate a Personal Access Token (Classic)

1. Click your profile picture > **Settings**
2. Scroll down to **Developer settings** (in the left sidebar)
3. Click **Personal access tokens** > **Tokens (classic)**
4. Click **Generate new token** > **Generate new token (classic)**
5. Configure the token:
   - **Note**: `ReStore Backup Access`
   - **Expiration**: Choose an expiration period (or No expiration)
   - **Scopes**: Select **repo** (Full control of private repositories)
6. Click **Generate token**
7. **Important**: Copy the token immediately (you won't be able to see it again)

## Alternative: Fine-grained Personal Access Token

For better security, use a fine-grained token:

1. Go to **Personal access tokens** > **Fine-grained tokens**
2. Click **Generate new token**
3. Configure:
   - **Token name**: `ReStore Backup`
   - **Expiration**: Choose duration
   - **Repository access**: Only select repositories > Choose your backup repository
   - **Permissions** > **Repository permissions**:
     - **Contents**: Read and write
     - **Metadata**: Read-only (automatically granted)
4. Click **Generate token** and copy it

## Step 4: Configure ReStore

Open `%USERPROFILE%\ReStore\config.json` and configure the GitHub section:

```json
{
  "storageSources": {
    "github": {
      "options": {
        "token": "your_token",
        "repo": "your_repo",
        "owner": "your_github_username"
      }
    }
  }
}
```

**Configuration Parameters:**

- **token**: The personal access token you generated in Step 3
- **repo**: The name of your backup repository
- **owner**: Your GitHub username

**Notes:**

- Uploads are base64 encoded in memory before being sent, so a file near the size limit needs
  roughly 2.3× its on-disk size in RAM
- Re-uploading an existing file updates it in place (the existing blob sha is looked up first)
- Existence checks use a cached repository tree rather than downloading file contents
- When GitHub rate-limits requests (primary or secondary), ReStore waits for the advertised retry time (up to 5 minutes) and retries up to 3 times
- For large file backups, consider using Git LFS or another storage provider
- Keep your token secure - it provides write access to your repository
- You can revoke tokens at any time from GitHub Settings > Developer settings
- Private repositories are free on GitHub for unlimited users

**Storage Limits (Free Plan):**

- **Repository size**: Recommended to keep under 5 GB
- **File size**: 100 MB maximum per file (warning at 50 MB)
- **Free tier**: 10 GB of Git LFS storage and bandwidth per month
- **Best practice**: Use GitHub for configuration backups and smaller files; use Google Drive or S3 for large file backups

## Optional: `prefix` option

Add `"prefix": "my-laptop"` to this source's `options` to keep the repository under a sub-folder of the repository. It is applied to every path (uploads, downloads, listing, sharing) and is empty by default, so existing repositories are unaffected. It must not start with `/` or contain `..`. Changing it later points ReStore at a different (empty) repository; it does not move existing data.

Remote sources do not need a top-level `"path"`. If one is present it is ignored (ReStore logs a warning); use `prefix` instead.
