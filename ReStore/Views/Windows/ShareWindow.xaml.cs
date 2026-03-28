using System.Windows;
using ReStore.Core.src.sharing;
using ReStore.Core.src.utils;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace ReStore.Views.Windows;

public partial class ShareWindow : FluentWindow
{
    private readonly ShareService _shareService;
    private readonly IConfigManager _configManager;
    private readonly string _filePath;

    public ShareWindow(string filePath, ShareService shareService, IConfigManager configManager)
    {
        InitializeComponent();
        _filePath = filePath;
        _shareService = shareService;
        _configManager = configManager;

        FilePathText.Text = filePath;
        LoadStorageProviders();
    }

    private void LoadStorageProviders()
    {
        var supportedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "s3", "azure", "gcp", "dropbox", "b2"
        };

        var availableProviders = _configManager.StorageSources
            .Where(kvp => supportedProviders.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        StorageProviderCombo.ItemsSource = availableProviders;
        if (availableProviders.Count > 0)
        {
            StorageProviderCombo.SelectedIndex = 0;
        }
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (StorageProviderCombo.SelectedValue is not string storageType) return;

        try
        {
            SetLoading(true);
            // Default expiration 7 days for now
            string link = await _shareService.ShareFileAsync(_filePath, storageType, TimeSpan.FromDays(7));

            LinkBox.Text = link;
            ResultPanel.Visibility = Visibility.Visible;
            ShareButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error sharing file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void SetLoading(bool isLoading)
    {
        ProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        StorageProviderCombo.IsEnabled = !isLoading;
        ShareButton.IsEnabled = !isLoading;
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(LinkBox.Text);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
