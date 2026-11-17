using System.Windows;
using System.Windows.Input;

namespace ReStore.Views.Windows
{
    /// <summary>Single-line text prompt, for cases MessageBox cannot cover.</summary>
    public partial class TextInputWindow : Window
    {
        public TextInputWindow(string title, string prompt, string? initialValue = null, string? hint = null)
        {
            InitializeComponent();

            Title = title;
            TitleText.Text = title;
            PromptText.Text = prompt;
            System.Windows.Automation.AutomationProperties.SetName(InputBox, prompt);
            InputBox.Text = initialValue ?? string.Empty;

            if (string.IsNullOrWhiteSpace(hint))
            {
                HintText.Visibility = Visibility.Collapsed;
            }
            else
            {
                HintText.Text = hint;
            }

            Loaded += (_, __) =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            };
        }

        public string? Value { get; private set; }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            Value = InputBox.Text;
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OkBtn_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                CancelBtn_Click(sender, e);
            }
        }
    }
}
