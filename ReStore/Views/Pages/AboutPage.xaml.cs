using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using ReStore.Core.src.utils;
using ReStore.Interop;
using ReStore.Services;

namespace ReStore.Views.Pages
{
    /// <summary>One crash report row: name plus time and size.</summary>
    public sealed class CrashReportRow(CrashReportFile file)
    {
        public CrashReportFile File { get; } = file;
        public string Name => File.Name;
        public string Detail => $"{DisplayTime.Format(File.LastWriteUtc)}, {ByteFormatter.Format(File.Length)}";
    }

    /// <summary>One pending anonymous crash report row (waiting for the user to send or delete it).</summary>
    public sealed class PendingCrashRow(ReStore.Core.src.telemetry.CrashReportSummary summary)
    {
        public string Id { get; } = summary.Id;
        public string Name => $"{Id}  {summary.ExceptionType ?? "(unknown)"}";
        public string Detail => $"{DisplayTime.Format(summary.CreatedUtc)}, {ByteFormatter.Format(summary.Bytes)}";
    }

    /// <summary>Version, runtime/OS, packaging, data locations, crash reports and diagnostics.</summary>
    public partial class AboutPage : Page
    {
        internal const string DocsUrl = "https://github.com/js302/ReStore/tree/main/docs";

        public AboutPage()
        {
            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            BuildFacts();
            PathsList.ItemsSource = AboutInfo.DataPaths();
            LoadCrashReports();
        }

        private void BuildFacts()
        {
            FactsGrid.Children.Clear();
            FactsGrid.RowDefinitions.Clear();

            var packaged = PackageInfo.IsPackaged;
            var facts = new List<(string, string)>
            {
                ("Version", AboutInfo.VersionOf(typeof(AboutPage).Assembly)),
                (".NET runtime", RuntimeInformation.FrameworkDescription),
                ("Operating system", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"),
                ("Installation", packaged ? "Packaged (MSIX)" : "Unpackaged"),
            };
            facts.Add(("Updates", AppUpdateManager.Instance.DescribeStatus()));
            if (packaged && PackageInfo.FamilyName is { } family)
            {
                facts.Add(("Package family", family));
            }

            foreach (var (label, value) in facts)
            {
                var row = FactsGrid.RowDefinitions.Count;
                FactsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var labelBlock = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) };
                labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                Grid.SetRow(labelBlock, row);

                var valueBlock = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                valueBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                Grid.SetRow(valueBlock, row);
                Grid.SetColumn(valueBlock, 1);

                FactsGrid.Children.Add(labelBlock);
                FactsGrid.Children.Add(valueBlock);
            }
        }

        private void LoadCrashReports()
        {
            var rows = AboutInfo.FindCrashReports().Select(f => new CrashReportRow(f)).ToList();
            CrashList.ItemsSource = rows;
            var pending = ReStore.Core.src.telemetry.Telemetry.Client.CrashStore.List().Select(s => new PendingCrashRow(s)).ToList();
            PendingCrashList.ItemsSource = pending;
            PendingCrashesHeader.Visibility = pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            NoCrashesText.Visibility = rows.Count == 0 && pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void SendPendingCrash_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Sending the crash report", async () =>
            {
                if (((FrameworkElement)sender).Tag is not PendingCrashRow row)
                {
                    return;
                }

                var client = ReStore.Core.src.telemetry.Telemetry.Client;
                if (!client.QueueCrash(row.Id))
                {
                    MessageBox.Show(client.Decision.Enabled
                        ? "The crash report could not be queued."
                        : $"Usage statistics are off ({client.Decision.Description}), so crash reports cannot be sent.", "Send crash report", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    _ = Task.Run(() => client.FlushAsync(TimeSpan.FromSeconds(5)));
                    MessageBox.Show(client.Endpoint == null
                        ? "The crash report is queued. No collection server is configured in this version, so it stays on this PC."
                        : "The crash report is queued and will be sent in the background.", "Send crash report", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                LoadCrashReports();
                await Task.CompletedTask;
            });
        }

        private async void ViewPendingCrash_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Viewing the crash report", () =>
            {
                if (((FrameworkElement)sender).Tag is PendingCrashRow row)
                {
                    var text = ReStore.Core.src.telemetry.CrashReportPrompt.Indent(ReStore.Core.src.telemetry.Telemetry.Client.CrashStore.Read(row.Id));
                    new ReStore.Views.Windows.CrashReportViewWindow(text ?? "This report no longer exists.") { Owner = Window.GetWindow(this) }.Show();
                }

                return Task.CompletedTask;
            });
        }

        private async void DeletePendingCrash_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Deleting the crash report", () =>
            {
                if (((FrameworkElement)sender).Tag is PendingCrashRow row)
                {
                    ReStore.Core.src.telemetry.Telemetry.Client.CrashStore.Delete(row.Id);
                    LoadCrashReports();
                }

                return Task.CompletedTask;
            });
        }

        private void RefreshCrashes_Click(object sender, RoutedEventArgs e) => LoadCrashReports();

        private async void OpenPath_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Opening the location", () =>
            {
                if (((FrameworkElement)sender).Tag is not DataPathInfo info)
                {
                    return Task.CompletedTask;
                }

                var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                if (info.IsDirectory)
                {
                    Directory.CreateDirectory(info.Path);
                    startInfo.ArgumentList.Add(info.Path);
                }
                else if (File.Exists(info.Path))
                {
                    startInfo.ArgumentList.Add("/select," + info.Path);
                }
                else
                {
                    var parent = Path.GetDirectoryName(info.Path) ?? info.Path;
                    Directory.CreateDirectory(parent);
                    startInfo.ArgumentList.Add(parent);
                }

                Process.Start(startInfo);
                return Task.CompletedTask;
            });
        }

        private async void OpenCrash_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Opening the crash report", () =>
            {
                if (((FrameworkElement)sender).Tag is CrashReportRow row)
                {
                    Process.Start(new ProcessStartInfo(row.File.Path) { UseShellExecute = true });
                }

                return Task.CompletedTask;
            });
        }

        private async void CopyCrash_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Copying the crash report", async () =>
            {
                if (((FrameworkElement)sender).Tag is CrashReportRow row)
                {
                    // Crash text can contain paths, tokens in messages, etc.: redact like the diagnostics report does.
                    var text = await File.ReadAllTextAsync(row.File.Path);
                    Clipboard.SetText(DiagnosticsReport.RedactText(text));
                    MessageBox.Show("Crash report copied to the clipboard. Secrets have been removed.", "Copy crash report", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            });
        }

        private async void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Copying diagnostics", async () =>
            {
                AppServices.Logger.Flush();
                var config = await AppServices.GetConfigManagerAsync();
                var report = await DiagnosticsReport.BuildAsync(config, AboutInfo.VersionOf(typeof(AboutPage).Assembly));
                Clipboard.SetText(report);
                MessageBox.Show("Diagnostics copied to the clipboard. Secrets have been removed.", "Copy diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private async void Docs_Click(object sender, RoutedEventArgs e)
        {
            await ErrorReporter.GuardAsync("Opening the documentation", () =>
            {
                Process.Start(new ProcessStartInfo(DocsUrl) { UseShellExecute = true });
                return Task.CompletedTask;
            });
        }
    }
}
