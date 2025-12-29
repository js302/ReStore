# AWS S3 Setup

To backup to Amazon S3, you need an AWS account and programmatic access credentials.

## Step 1: Create an AWS Account

1. Go to [AWS Console](https://aws.amazon.com/)
2. Sign up for an AWS account if you don't have one
3. Note: S3 is not free but very affordable for personal backups

## Step 2: Create an S3 Bucket

1. Sign in to the [AWS Management Console](https://console.aws.amazon.com/)
2. Navigate to **S3** service
3. Click **Create bucket**
4. Configure your bucket:
   - **Bucket name**: Choose a unique name (e.g., `restore-backups-yourname`)
   - **Region**: Choose a region close to you
   - **Block Public Access**: Keep all blocks enabled (recommended)
   - **Versioning**: Enable if you want S3-level version history
   - **Encryption**: Enable default encryption (recommended)
5. Click **Create bucket**

## Step 3: Create IAM User with S3 Access

1. Navigate to **IAM** service in AWS Console
2. Click **Users** > **Create user**
3. User name: `restore-backup-user`
4. Click **Next**
5. Select **Attach policies directly**
6. Search and select: **AmazonS3FullAccess** (or create a custom policy for specific bucket access)
7. Click **Next** and **Create user**

## Step 4: Generate Access Keys

1. Click on the newly created user
2. Go to **Security credentials** tab
3. Scroll to **Access keys**
4. Click **Create access key**
5. Use case: **Application running outside AWS**
6. Click **Next** and **Create access key**
7. **Important**: Copy both the **Access Key ID** and **Secret Access Key** immediately (you can't view the secret key again)

## Step 5: Configure ReStore

Open `%USERPROFILE%\ReStore\config.json` and configure the S3 section:

```json
{
  "storageSources": {
    "s3": {
      "path": "./backups",
      "options": {
        "accessKeyId": "your_access_key_id",
        "secretAccessKey": "your_secret_access_key",
        "region": "your_aws_region",
        "bucketName": "your_bucket_name"
      }
    }
  }
}
```

**Configuration Parameters:**

- **path**: Relative path prefix for organizing backups in the bucket (default: `"./backups"`)
- **accessKeyId**: The Access Key ID from Step 4
- **secretAccessKey**: The Secret Access Key from Step 4
- **region**: The AWS region where your bucket is located (e.g., `us-east-1`, `eu-west-1`, `ap-southeast-2`)
- **bucketName**: The name of your S3 bucket from Step 2

## Optional: Create a More Restrictive IAM Policy

For better security, create a custom policy that only grants access to your specific bucket:

1. In IAM, go to **Policies** > **Create policy**
2. Select **JSON** and paste:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "s3:PutObject",
        "s3:GetObject",
        "s3:DeleteObject",
        "s3:ListBucket"
      ],
      "Resource": [
        "arn:aws:s3:::restore-backups-yourname",
        "arn:aws:s3:::restore-backups-yourname/*"
      ]
    }
  ]
}
```

3. Replace `restore-backups-yourname` with your bucket name
4. Name the policy (e.g., `ReStoreBackupPolicy`)
5. Attach this policy to your IAM user instead of `AmazonS3FullAccess`

**Notes:**

- Store your access keys securely - they provide full access to your S3 bucket
- Consider enabling S3 bucket versioning for additional protection
- Monitor your S3 usage in the AWS Console to track costs
- Set up lifecycle policies to automatically delete old backups and reduce costs

**Storage Limits & Pricing:**

- **AWS Free Tier** (first 12 months): 5 GB of S3 Standard storage, 20,000 GET requests, 2,000 PUT requests per month
- **After Free Tier**: Pay-as-you-go pricing (typically $0.023 per GB/month for Standard storage in US East)
- **No file size limit**: Individual objects can be up to 5 TB
- **Cost Example**: 100 GB of backups ≈ $2.30/month
- Consider using **S3 Glacier** for long-term archival at ~$0.004 per GB/month (much cheaper but slower retrieval)
