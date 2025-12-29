# GitHub Storage Setup

Use GitHub as a storage backend to leverage version control for your backups. This is best suited for configuration files and smaller backups.

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
      "path": "./backups",
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

- **path**: Relative path for organizing backups in the repository (default: `"./backups"`)
- **token**: The personal access token you generated in Step 3
- **repo**: The name of your backup repository
- **owner**: Your GitHub username

**Notes:**

- GitHub has file size limits
- This storage option is best for configuration files, scripts, and smaller backups
- For large file backups, consider using Git LFS or another storage provider
- Keep your token secure - it provides write access to your repository
- You can revoke tokens at any time from GitHub Settings > Developer settings
- Private repositories are free on GitHub for unlimited users

**Storage Limits (Free Plan):**

- **Repository size**: No hard limit, but recommended to keep under 1 GB
- **File size**: 100 MB maximum per file (warning at 50 MB)
- **Free tier**: 2 GB of Git LFS storage and 1 GB of bandwidth per month
- **Best practice**: Use GitHub for configuration backups and smaller files; use Google Drive or S3 for large file backups
