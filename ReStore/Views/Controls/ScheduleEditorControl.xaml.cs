using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReStore.Services;

namespace ReStore.Views.Controls
{
    /// <summary>
    /// The schedule selector of Settings (use default interval / every N hours / daily at / weekdays at / custom expression) with a live
    /// "Next runs" preview and a validation message. The logic is <see cref="ScheduleEditorModel"/> (unit-tested); this is the thin UI.
    /// <see cref="Edited"/> fires on every change (for dirty tracking), <see cref="Committed"/> when the user has finished entering a valid,
    /// changed schedule (choice made, focus left the box, Enter pressed). An invalid entry is never committed.
    /// </summary>
    public partial class ScheduleEditorControl : UserControl
    {
        private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xEF, 0x44, 0x44));
        private bool _loading;
        private string? _lastExpression;

        public ScheduleEditorControl()
        {
            InitializeComponent();
            ModeCombo.SelectionChanged += (_, _) => OnModeChanged();
            ValueBox.TextChanged += (_, _) => OnEdited(commit: false);
            ValueBox.LostFocus += (_, _) => Commit();
            ValueBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Commit();
                }
            };
            Load(null);
        }

        /// <summary>What the preview says for "Use default interval".</summary>
        public string DefaultDescription { get; set; } = "Follows the global backup interval.";

        public event EventHandler? Edited;

        public event EventHandler? Committed;

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private ScheduleMode Mode => (ModeCombo.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse<ScheduleMode>(tag, out var mode) ? mode : ScheduleMode.Default;

        /// <summary>Shows a stored expression without raising any event.</summary>
        public void Load(string? expression)
        {
            _loading = true;
            try
            {
                var value = ScheduleEditorModel.FromExpression(expression);
                foreach (ComboBoxItem item in ModeCombo.Items)
                {
                    if (string.Equals(item.Tag as string, value.Mode.ToString(), StringComparison.Ordinal))
                    {
                        ModeCombo.SelectedItem = item;
                        break;
                    }
                }

                ValueBox.Text = value.Text;
                ApplyModeLayout();
                _lastExpression = ScheduleEditorModel.TryBuild(value.Mode, value.Text, out var built, out _) ? built : expression;
                RefreshPreview();
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>The expression the controls describe now (null = use the default interval), or false with a message when it is not valid.</summary>
        public bool TryGetExpression(out string? expression, out string error)
            => ScheduleEditorModel.TryBuild(Mode, ValueBox.Text, out expression, out error);

        private void ApplyModeLayout()
        {
            var mode = Mode;
            ValueBox.Visibility = mode == ScheduleMode.Default ? Visibility.Collapsed : Visibility.Visible;
            ValueBox.MinWidth = mode == ScheduleMode.Custom ? 240 : 60;
            UnitText.Text = mode switch
            {
                ScheduleMode.EveryHours => "hours",
                ScheduleMode.Daily or ScheduleMode.Weekdays => "(24-hour HH:mm, local time)",
                ScheduleMode.Custom => "e.g. mon,wed,fri 07:15 | weekly sun 03:00 | monthly last 04:00 | cron 0 */6 * * * | off",
                _ => string.Empty
            };
        }

        private void OnModeChanged()
        {
            if (_loading)
            {
                return;
            }

            ApplyModeLayout();
            if (Mode != ScheduleMode.Default && !LooksValidFor(Mode, ValueBox.Text))
            {
                // A sensible starting value, so choosing "Daily at" is already a valid schedule; "Custom" starts from the current one.
                var suggestion = Mode switch
                {
                    ScheduleMode.EveryHours => "4",
                    ScheduleMode.Daily => "02:00",
                    ScheduleMode.Weekdays => "18:30",
                    _ => _lastExpression ?? string.Empty
                };
                _loading = true;
                ValueBox.Text = suggestion;
                _loading = false;
            }

            OnEdited(commit: true);
        }

        private static bool LooksValidFor(ScheduleMode mode, string text)
            => mode == ScheduleMode.Default || ScheduleEditorModel.TryBuild(mode, text, out _, out _);

        private void OnEdited(bool commit)
        {
            if (_loading)
            {
                return;
            }

            RefreshPreview();
            Edited?.Invoke(this, EventArgs.Empty);
            if (commit)
            {
                Commit();
            }
        }

        private void RefreshPreview()
        {
            var valid = ScheduleEditorModel.TryBuild(Mode, ValueBox.Text, out _, out var error);
            PreviewText.Text = valid
                ? ScheduleEditorModel.Preview(Mode, ValueBox.Text, DefaultDescription, DateTime.UtcNow, TimeZoneInfo.Local)
                : $"Not saved: {error}";
            PreviewText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            if (!valid)
            {
                PreviewText.Foreground = ErrorBrush;
            }
        }

        private void Commit()
        {
            if (_loading || !TryGetExpression(out var expression, out _))
            {
                return;
            }

            if (string.Equals(expression, _lastExpression, StringComparison.Ordinal))
            {
                return;
            }

            _lastExpression = expression;
            Committed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Tells the control the expression that is now stored (after a save, or a rollback).</summary>
        public void MarkStored(string? expression) => _lastExpression = expression;
    }
}
