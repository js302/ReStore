using System.Windows;
using System.Windows.Controls;
using ReStore.Core.src.telemetry;

namespace ReStore.Views.Windows
{
    /// <summary>
    /// "Show collected data": the anonymous usage events waiting on this PC, exactly as they would be sent
    /// (the same output as <c>restore telemetry preview</c>). Built in code, no XAML.
    /// </summary>
    public sealed class TelemetryPreviewWindow : Window
    {
        public TelemetryPreviewWindow()
        {
            Title = "Collected usage statistics";
            Width = 760;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

            var client = Telemetry.Client;
            var events = client.Preview();
            var decision = client.Decision;

            var header = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Text = (decision.Enabled ? "Usage statistics are on." : $"Usage statistics are off: {decision.Description}")
                    + (client.Endpoint == null
                        ? " No collection server is configured in this version, so nothing leaves this PC; the events below only wait here."
                        : " Queued events are sent in the background.")
            };

            var box = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Text = events.Count == 0 ? "No events are queued." : string.Join(Environment.NewLine, events)
            };

            var close = new Button
            {
                Content = "Close",
                IsCancel = true,
                IsDefault = true,
                Padding = new Thickness(16, 4, 16, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            close.Click += (_, _) => Close();

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(header, 0);
            Grid.SetRow(box, 1);
            Grid.SetRow(close, 2);
            grid.Children.Add(header);
            grid.Children.Add(box);
            grid.Children.Add(close);
            Content = grid;
        }
    }
}
