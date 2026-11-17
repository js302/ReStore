using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace ReStore.Views.Windows;

/// <summary>Asks for the current and the new password of an encrypted v3 repository; the Settings page performs the change.</summary>
public partial class ChangePasswordWindow : FluentWindow
{
    public string? CurrentPassword { get; private set; }
    public string? NewPassword { get; private set; }
    public bool CreateRecoveryKey { get; private set; }

    public ChangePasswordWindow()
    {
        InitializeComponent();
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        var next = NewInput.Password;
        string? message = null;
        if (next.Length > 0 && next.Length < 8)
        {
            message = "The new password must be at least 8 characters long.";
        }
        else if (ConfirmInput.Password.Length > 0 && next != ConfirmInput.Password)
        {
            message = "The new passwords do not match.";
        }

        ValidationMessage.Text = message ?? string.Empty;
        ValidationMessage.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        ChangeButton.IsEnabled = message == null
            && CurrentInput.Password.Length > 0
            && next.Length >= 8
            && next == ConfirmInput.Password;
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        CurrentPassword = CurrentInput.Password;
        NewPassword = NewInput.Password;
        CreateRecoveryKey = RecoveryKeyCheck.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ConfirmInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ChangeButton.IsEnabled)
        {
            Change_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }
}
