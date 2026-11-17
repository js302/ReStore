using System.Windows;
using ReStore.Core.src.core;

namespace ReStore.Views.Windows
{
    /// <summary>What the user asked for in <see cref="VerifyOptionsWindow"/>.</summary>
    public enum VerifyChoice
    {
        Quick,
        Sample,
        Full,
        TestRestore
    }

    /// <summary>Picks how thoroughly to check a snapshot, or to test-restore a few of its files instead.</summary>
    public partial class VerifyOptionsWindow : Window
    {
        public VerifyOptionsWindow(string description, int samplePercent, VerifyChoice initial = VerifyChoice.Sample)
        {
            InitializeComponent();

            PromptText.Text = description;
            SampleText.Inlines.Add(new System.Windows.Documents.Run("Sample") { FontWeight = FontWeights.SemiBold });
            SampleText.Inlines.Add(new System.Windows.Documents.Run($" - quick checks, plus a full check of a random {samplePercent}% of the chunks."));

            QuickOption.IsChecked = initial == VerifyChoice.Quick;
            SampleOption.IsChecked = initial == VerifyChoice.Sample;
            FullOption.IsChecked = initial == VerifyChoice.Full;
            TestRestoreOption.IsChecked = initial == VerifyChoice.TestRestore;
        }

        public VerifyChoice Choice { get; private set; } = VerifyChoice.Sample;

        /// <summary>Shows the dialog; null when the user cancelled.</summary>
        public static VerifyChoice? Ask(Window? owner, string description, int samplePercent = VerificationModes.DefaultSamplePercent)
        {
            var dialog = new VerifyOptionsWindow(description, samplePercent);
            if (owner != null)
            {
                dialog.Owner = owner;
            }

            return dialog.ShowDialog() == true ? dialog.Choice : null;
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            Choice = QuickOption.IsChecked == true ? VerifyChoice.Quick
                : FullOption.IsChecked == true ? VerifyChoice.Full
                : TestRestoreOption.IsChecked == true ? VerifyChoice.TestRestore
                : VerifyChoice.Sample;
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
