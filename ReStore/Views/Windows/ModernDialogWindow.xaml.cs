using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using UiButton = Wpf.Ui.Controls.Button;

namespace ReStore.Views.Windows;

public partial class ModernDialogWindow : FluentWindow
{
    private MessageBoxResult _defaultResult = MessageBoxResult.OK;
    private MessageBoxResult _escapeResult = MessageBoxResult.OK;

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public ModernDialogWindow(string messageText, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();

        var resolvedCaption = string.IsNullOrWhiteSpace(caption) ? "ReStore" : caption;
        Title = resolvedCaption;
        TitleTextBlock.Text = resolvedCaption;
        MessageTextBlock.Text = messageText ?? string.Empty;

        ConfigureIcon(icon);
        ConfigureButtons(buttons);

        Loaded += ModernDialogWindow_Loaded;
        PreviewKeyDown += ModernDialogWindow_PreviewKeyDown;
    }

    private void ModernDialogWindow_Loaded(object sender, RoutedEventArgs e)
    {
        FocusDefaultButton();
        StartEntranceAnimation();
    }

    private void ConfigureIcon(MessageBoxImage icon)
    {
        var (symbol, badgeColor, textColor, borderColor) = icon switch
        {
            MessageBoxImage.Error or MessageBoxImage.Stop or MessageBoxImage.Hand => ("X", "#FDE7E9", "#B42318", "#F7CDD2"),
            MessageBoxImage.Warning or MessageBoxImage.Exclamation => ("!", "#FFF5E0", "#B54708", "#FFE2B8"),
            MessageBoxImage.Question => ("?", "#E8F1FF", "#175CD3", "#C9DEFF"),
            _ => ("i", "#E6F4EA", "#0E6E3B", "#C8EACE")
        };

        IconTextBlock.Text = symbol;
        IconBadge.Background = CreateBrush(badgeColor);
        IconTextBlock.Foreground = CreateBrush(textColor);
        IconBadge.BorderBrush = CreateBrush(borderColor);
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        HideAndResetButtons();

        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                ConfigureButton(PrimaryButton, "OK", MessageBoxResult.OK, isDefault: true);
                ConfigureButton(SecondaryButton, "Cancel", MessageBoxResult.Cancel, isCancel: true);
                _defaultResult = MessageBoxResult.OK;
                _escapeResult = MessageBoxResult.Cancel;
                break;

            case MessageBoxButton.YesNo:
                ConfigureButton(PrimaryButton, "Yes", MessageBoxResult.Yes, isDefault: true);
                ConfigureButton(SecondaryButton, "No", MessageBoxResult.No, isCancel: true);
                _defaultResult = MessageBoxResult.Yes;
                _escapeResult = MessageBoxResult.No;
                break;

            case MessageBoxButton.YesNoCancel:
                ConfigureButton(PrimaryButton, "Yes", MessageBoxResult.Yes, isDefault: true);
                ConfigureButton(SecondaryButton, "No", MessageBoxResult.No);
                ConfigureButton(TertiaryButton, "Cancel", MessageBoxResult.Cancel, isCancel: true);
                _defaultResult = MessageBoxResult.Yes;
                _escapeResult = MessageBoxResult.Cancel;
                break;

            case MessageBoxButton.OK:
            default:
                ConfigureButton(PrimaryButton, "OK", MessageBoxResult.OK, isDefault: true, isCancel: true);
                _defaultResult = MessageBoxResult.OK;
                _escapeResult = MessageBoxResult.OK;
                break;
        }
    }

    private void ConfigureButton(UiButton button, string text, MessageBoxResult result, bool isDefault = false, bool isCancel = false)
    {
        button.Content = text;
        button.Tag = result;
        button.Visibility = Visibility.Visible;
        button.IsDefault = isDefault;
        button.IsCancel = isCancel;
    }

    private void HideAndResetButtons()
    {
        foreach (var button in new[] { PrimaryButton, SecondaryButton, TertiaryButton })
        {
            button.Visibility = Visibility.Collapsed;
            button.IsDefault = false;
            button.IsCancel = false;
            button.Tag = null;
        }
    }

    private void FocusDefaultButton()
    {
        if (PrimaryButton.IsDefault)
        {
            PrimaryButton.Focus();
            return;
        }

        if (SecondaryButton.IsDefault)
        {
            SecondaryButton.Focus();
            return;
        }

        if (TertiaryButton.IsDefault)
        {
            TertiaryButton.Focus();
        }
    }

    private void ModernDialogWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWithResult(_escapeResult);
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (Keyboard.FocusedElement is not UiButton)
            {
                e.Handled = true;
                CloseWithResult(_defaultResult);
            }
        }
    }

    private void DialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is UiButton button && button.Tag is MessageBoxResult result)
        {
            CloseWithResult(result);
            return;
        }

        CloseWithResult(_defaultResult);
    }

    private void CloseWithResult(MessageBoxResult result)
    {
        Result = result;
        Close();
    }

    private void StartEntranceAnimation()
    {
        if (DialogCard.RenderTransform is not TranslateTransform translateTransform)
        {
            return;
        }

        var easing = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };

        var fadeAnimation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = easing
        };

        var slideAnimation = new DoubleAnimation
        {
            From = 14,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = easing
        };

        DialogCard.BeginAnimation(OpacityProperty, fadeAnimation);
        translateTransform.BeginAnimation(TranslateTransform.YProperty, slideAnimation);
    }

    private static SolidColorBrush CreateBrush(string colorCode)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorCode);
        return new SolidColorBrush(color);
    }
}