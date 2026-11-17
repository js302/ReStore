using ReStore.Core.src.monitoring;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ReStore.Services
{
    /// <summary>
    /// Shows Windows toasts through the WinRT APIs of the 19041 TFM (no Windows App SDK). Only usable with MSIX package
    /// identity: an unpackaged process would need an AUMID plus a Start-menu shortcut, which ReStore does not create, so
    /// callers fall back to tray balloons there. Buttons and the toast body use protocol activation
    /// (<c>restore-toast:</c>, declared in Package.appxmanifest); Windows launches ReStore.exe with the URI, and the
    /// single-instance machinery in App forwards it to the running instance.
    /// </summary>
    internal static class ToastNotifier
    {
        public static bool IsAvailable => ReStore.Interop.PackageInfo.IsPackaged && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

        public static void Show(PendingNotification notification)
        {
            var xml = ToastXmlBuilder.Build(notification.Title, notification.Message, notification.Target, notification.Actions, ToastActivationArgs.CurrentToken);
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(doc));
        }
    }
}
