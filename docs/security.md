# Security & Best Practices

## Security

- **Encryption**: Enable AES-256-GCM encryption to protect sensitive backups with password protection
- **Password Storage**: Your encryption password is never stored - only the salt is saved in `config.json`
- **Credentials**: Store configuration files securely, especially if they contain cloud storage credentials
- **Environment Variables**: May include sensitive information like API keys - consider enabling encryption
- **Windows Settings**: Backups contain registry exports that can modify system behavior
- **Administrator Privileges**: May be required for some restore operations, particularly for system-level settings
- **Script Review**: Always review restore scripts before executing them, especially when restoring Windows settings
- **Lost Passwords**: There is no password recovery mechanism - encrypted backups are unrecoverable without the password

## Best Practices

- **Enable Encryption**: Protect sensitive data with AES-256-GCM encryption and a strong password
- **Secure Your Password**: Store your encryption password in a password manager and write it down in a secure location
- **Test Restores**: Verify you can restore and decrypt backups before you need them in an emergency
- **System Backups**: Run before major system changes
- **Multiple Locations**: Keep backups in multiple locations for redundancy (local + cloud)
- **System Restore Points**: Create a Windows system restore point before restoring Windows settings
- **Review Scripts**: Review Windows settings restore scripts before running them
- **Selective Restoration**: When restoring to a different computer, selectively restore settings rather than restoring everything
