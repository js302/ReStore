using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace ReStore.Views.Windows;

public partial class PasswordPromptWindow : FluentWindow
{
    public string? Password { get; private set; }

    public PasswordPromptWindow(bool isForEncryption = false)
    {
        InitializeComponent();
        
        if (isForEncryption)
        {
            TitleText.Text = "Encryption Password Required";
            MessageText.Text = "Encryption is enabled. Please enter your encryption password to create encrypted backups.\n\nThis password will be cached for this session only.";
        }
        else
        {
            TitleText.Text = "Decrypt Backup";
            MessageText.Text = "This backup is encrypted. Please enter the password to decrypt and restore.";
        }
        
        Loaded += (s, e) => PasswordInput.Focus();
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        OkButton.IsEnabled = !string.IsNullOrEmpty(PasswordInput.Password);
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OkButton.IsEnabled)
        {
            Ok_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordInput.Password;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Password = null;
        DialogResult = false;
        Close();
    }
}
