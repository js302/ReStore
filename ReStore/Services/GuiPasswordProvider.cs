using ReStore.Core.src.utils;
using System;
using System.Windows;

namespace ReStore.Services;

public class GuiPasswordProvider : IPasswordProvider, ILogger
{
    private string? _password;
    private readonly bool _promptUser;
    private bool _isForEncryption = false;

    public GuiPasswordProvider(bool promptUser = true)
    {
        _promptUser = promptUser;
    }

    public void SetPassword(string? password)
    {
        _password = password;
    }

    public void SetEncryptionMode(bool isForEncryption)
    {
        _isForEncryption = isForEncryption;
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        System.Diagnostics.Trace.WriteLine($"[{level}] {message}");
    }

    public async Task<string?> GetPasswordAsync()
    {
        if (!string.IsNullOrEmpty(_password))
        {
            return _password;
        }

        if (_promptUser)
        {
            string? password = null;
            var maxAttempts = 3;
            var attempts = 0;

            while (attempts < maxAttempts)
            {
                password = await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = new Views.Windows.PasswordPromptWindow(_isForEncryption);
                    if (dialog.ShowDialog() == true)
                    {
                        return dialog.Password;
                    }
                    return null;
                });

                if (string.IsNullOrEmpty(password))
                {
                    return null;
                }

                if (_isForEncryption)
                {
                    var configManager = new ConfigManager(this);
                    await configManager.LoadAsync();
                    
                    if (!string.IsNullOrEmpty(configManager.Encryption.Salt) && 
                        !string.IsNullOrEmpty(configManager.Encryption.VerificationToken))
                    {
                        var encryptionService = new EncryptionService(this);
                        var salt = Convert.FromBase64String(configManager.Encryption.Salt);
                        
                        if (encryptionService.VerifyPassword(
                            password, 
                            salt, 
                            configManager.Encryption.VerificationToken,
                            configManager.Encryption.KeyDerivationIterations))
                        {
                            _password = password;
                            return password;
                        }
                        else
                        {
                            attempts++;
                            if (attempts < maxAttempts)
                            {
                                MessageBox.Show(
                                    $"Incorrect password. Please try again.\n\nAttempts remaining: {maxAttempts - attempts}",
                                    "Incorrect Password",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                            }
                            else
                            {
                                MessageBox.Show(
                                    "Maximum password attempts reached. Backup cancelled.",
                                    "Authentication Failed",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                                return null;
                            }
                        }
                    }
                    else
                    {
                        _password = password;
                        return password;
                    }
                }
                else
                {
                    _password = password;
                    return password;
                }
            }
            
            return null;
        }

        return null;
    }

    public bool IsPasswordSet()
    {
        return !string.IsNullOrEmpty(_password);
    }

    public void ClearPassword()
    {
        _password = null;
    }
}
