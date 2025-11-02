using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace ReStore.Views.Windows;

public partial class EncryptionSetupWindow : FluentWindow
{
    public string? Password { get; private set; }
    public byte[]? Salt { get; private set; }

    public EncryptionSetupWindow()
    {
        InitializeComponent();
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        UpdatePasswordStrength();
        ValidatePasswords();
    }

    private void UpdatePasswordStrength()
    {
        var password = PasswordInput.Password;
        
        if (string.IsNullOrEmpty(password))
        {
            StrengthIndicator.Text = "Enter a password to see strength";
            StrengthIndicator.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
            return;
        }

        var length = password.Length;
        var hasUpper = Regex.IsMatch(password, @"[A-Z]");
        var hasLower = Regex.IsMatch(password, @"[a-z]");
        var hasDigit = Regex.IsMatch(password, @"\d");
        var hasSymbol = Regex.IsMatch(password, @"[!@#$%^&*(),.?""':{}|<>]");

        var score = 0;
        if (length >= 8) score++;
        if (length >= 12) score++;
        if (hasUpper) score++;
        if (hasLower) score++;
        if (hasDigit) score++;
        if (hasSymbol) score++;

        string strength;
        string color;
        if (score <= 2)
        {
            strength = "Weak";
            color = "#D32F2F";
        }
        else if (score <= 4)
        {
            strength = "Medium";
            color = "#F57C00";
        }
        else
        {
            strength = "Strong";
            color = "#388E3C";
        }

        var features = new List<string>();
        if (hasUpper) features.Add("uppercase");
        if (hasLower) features.Add("lowercase");
        if (hasDigit) features.Add("numbers");
        if (hasSymbol) features.Add("symbols");

        var featureText = features.Any() ? $" ({string.Join(", ", features)})" : "";
        
        StrengthIndicator.Text = $"{strength} - {length} characters{featureText}";
        StrengthIndicator.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!);
    }

    private void ValidatePasswords()
    {
        var password = PasswordInput.Password;
        var confirm = ConfirmPasswordInput.Password;

        if (string.IsNullOrEmpty(password))
        {
            EnableButton.IsEnabled = false;
            ValidationMessage.Visibility = Visibility.Collapsed;
            return;
        }

        if (password.Length < 8)
        {
            ValidationMessage.Text = "Password must be at least 8 characters long.";
            ValidationMessage.Visibility = Visibility.Visible;
            EnableButton.IsEnabled = false;
            return;
        }

        if (string.IsNullOrEmpty(confirm))
        {
            EnableButton.IsEnabled = false;
            ValidationMessage.Visibility = Visibility.Collapsed;
            return;
        }

        if (password != confirm)
        {
            ValidationMessage.Text = "Passwords do not match.";
            ValidationMessage.Visibility = Visibility.Visible;
            EnableButton.IsEnabled = false;
            return;
        }

        ValidationMessage.Visibility = Visibility.Collapsed;
        EnableButton.IsEnabled = true;
    }

    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordInput.Password;
        
        Salt = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(Salt);
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ConfirmPasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && EnableButton.IsEnabled)
        {
            Enable_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }
}
