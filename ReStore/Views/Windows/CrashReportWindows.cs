using System.Windows;
using System.Windows.Controls;
using ReStore.Core.src.telemetry;

namespace ReStore.Views.Windows
{
    /// <summary>A read-only window with the exact JSON of crash reports (what would be sent). Built in code, no XAML.</summary>
    public sealed class CrashReportViewWindow : Window
    {
        public CrashReportViewWindow(string text)
        {
            Title = "Crash report (exactly what would be sent)";
            Width = 760;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

            var box = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Text = text
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
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(close, 1);
            grid.Children.Add(box);
            grid.Children.Add(close);
            Content = grid;
        }
    }

    /// <summary>
    /// "ReStore ran into a problem. Send an anonymous crash report?" A small non-blocking window (Show, not ShowDialog) with
    /// Send, Always send, Don't send and View report. The decision logic lives in <see cref="CrashReportPrompt"/>.
    /// </summary>
    public sealed class CrashReportPromptWindow : Window
    {
        public CrashReportPromptWindow(int pending, Func<CrashPromptChoice, Task> choose)
        {
            Title = "ReStore";
            SizeToContent = SizeToContent.WidthAndHeight;
            MaxWidth = 460;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

            var message = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Text = "ReStore ran into a problem. Send an anonymous crash report?"
                    + (pending > 1 ? $" ({pending} reports are waiting.)" : "")
                    + "\n\nThe report holds only the error type, a scrubbed message and the names of code methods: never file names, paths, folder or backup names. Use \"View report\" to see exactly what would be sent."
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            void Add(string text, CrashPromptChoice choice, bool closes, bool isDefault = false)
            {
                var button = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault };
                button.Click += async (_, _) =>
                {
                    try
                    {
                        await choose(choice);
                    }
                    finally
                    {
                        if (closes)
                        {
                            Close();
                        }
                    }
                };
                buttons.Children.Add(button);
            }

            Add("Send", CrashPromptChoice.Send, closes: true, isDefault: true);
            Add("Always send", CrashPromptChoice.AlwaysSend, closes: true);
            Add("Don't send", CrashPromptChoice.DontSend, closes: true);
            Add("View report", CrashPromptChoice.View, closes: false);

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(message);
            panel.Children.Add(buttons);
            Content = panel;
        }
    }
}
