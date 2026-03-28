using System.Windows;
using ReStore.Views.Windows;

namespace ReStore.Services;

public static class ModernDialogService
{
    public static MessageBoxResult Show(string messageBoxText)
    {
        return Show(messageBoxText, "ReStore", MessageBoxButton.OK, MessageBoxImage.None);
    }

    public static MessageBoxResult Show(string messageBoxText, string caption)
    {
        return Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);
    }

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button)
    {
        return Show(messageBoxText, caption, button, MessageBoxImage.None);
    }

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        if (Application.Current?.Dispatcher == null)
        {
            return System.Windows.MessageBox.Show(messageBoxText, caption, button, icon);
        }

        if (Application.Current.Dispatcher.CheckAccess())
        {
            return ShowInternal(messageBoxText, caption, button, icon);
        }

        return Application.Current.Dispatcher.Invoke(() => ShowInternal(messageBoxText, caption, button, icon));
    }

    private static MessageBoxResult ShowInternal(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        try
        {
            var dialog = new ModernDialogWindow(messageBoxText, caption, button, icon)
            {
                Owner = ResolveOwnerWindow()
            };

            dialog.ShowDialog();
            return dialog.Result == MessageBoxResult.None
                ? GetCloseFallbackResult(button)
                : dialog.Result;
        }
        catch
        {
            return System.Windows.MessageBox.Show(messageBoxText, caption, button, icon);
        }
    }

    private static Window? ResolveOwnerWindow()
    {
        if (Application.Current == null)
        {
            return null;
        }

        var activeWindow = Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive && window.IsVisible);

        return activeWindow ?? Application.Current.MainWindow;
    }

    private static MessageBoxResult GetCloseFallbackResult(MessageBoxButton button)
    {
        return button switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };
    }
}