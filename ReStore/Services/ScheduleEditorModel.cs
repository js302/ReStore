using System.Globalization;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Services
{
    /// <summary>The choices of the per-folder schedule selector in Settings.</summary>
    public enum ScheduleMode
    {
        /// <summary>No schedule of its own: the global backup interval applies.</summary>
        Default,

        /// <summary>Every N hours (the text is the number of hours).</summary>
        EveryHours,

        /// <summary>Daily at HH:mm.</summary>
        Daily,

        /// <summary>Weekdays (Monday to Friday) at HH:mm.</summary>
        Weekdays,

        /// <summary>Any schedule expression (days lists, weekly, monthly, cron, off).</summary>
        Custom
    }

    /// <param name="Text">Hours, HH:mm or the expression, depending on <paramref name="Mode"/>; empty for <see cref="ScheduleMode.Default"/>.</param>
    public sealed record ScheduleEditorValue(ScheduleMode Mode, string Text);

    /// <summary>
    /// What the Settings schedule selector does, without any UI: maps a stored expression to a selector choice and back, validates
    /// what was typed and writes the "Next runs" preview. A schedule the simple choices cannot express is shown as Custom.
    /// </summary>
    public static class ScheduleEditorModel
    {
        public static ScheduleEditorValue FromExpression(string? expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return new ScheduleEditorValue(ScheduleMode.Default, string.Empty);
            }

            if (!BackupSchedule.TryParse(expression, out var schedule, out _))
            {
                // Keep what is in the file so the user sees (and can fix) it.
                return new ScheduleEditorValue(ScheduleMode.Custom, expression.Trim());
            }

            if (schedule.IsInterval && schedule.Interval is { } interval && interval.Ticks % TimeSpan.TicksPerHour == 0)
            {
                return new ScheduleEditorValue(ScheduleMode.EveryHours, ((long)interval.TotalHours).ToString(CultureInfo.InvariantCulture));
            }

            var parts = schedule.Expression.Split(' ');
            if (schedule.Kind == ScheduleKind.Calendar && parts.Length == 2 && !parts[1].Contains(',', StringComparison.Ordinal))
            {
                if (parts[0] == "daily")
                {
                    return new ScheduleEditorValue(ScheduleMode.Daily, parts[1]);
                }

                if (parts[0] == "weekdays")
                {
                    return new ScheduleEditorValue(ScheduleMode.Weekdays, parts[1]);
                }
            }

            return new ScheduleEditorValue(ScheduleMode.Custom, schedule.Expression);
        }

        /// <summary>
        /// The expression to store for the selection: null for <see cref="ScheduleMode.Default"/> (no schedule), otherwise the normalized
        /// expression. False with a message for the user when what was typed is not valid.
        /// </summary>
        public static bool TryBuild(ScheduleMode mode, string? text, out string? expression, out string error)
        {
            expression = null;
            error = string.Empty;
            var value = text?.Trim() ?? string.Empty;

            string raw;
            switch (mode)
            {
                case ScheduleMode.Default:
                    return true;
                case ScheduleMode.EveryHours:
                    if (!NumberInput.TryParseDouble(value, out var hours) || hours <= 0)
                    {
                        error = "Enter the number of hours between backups, for example 4.";
                        return false;
                    }

                    var minutes = (long)Math.Round(hours * 60);
                    raw = minutes % 60 == 0 ? $"every {minutes / 60}h" : $"every {minutes}m";
                    break;
                case ScheduleMode.Daily:
                    raw = $"daily {value}";
                    break;
                case ScheduleMode.Weekdays:
                    raw = $"weekdays {value}";
                    break;
                default:
                    if (value.Length == 0)
                    {
                        error = "Enter a schedule such as \"daily 02:00\", \"mon,wed,fri 07:15\" or \"cron 0 */6 * * *\".";
                        return false;
                    }

                    raw = value;
                    break;
            }

            if (!BackupSchedule.TryParse(raw, out var schedule, out var parseError))
            {
                error = char.ToUpperInvariant(parseError[0]) + parseError[1..];
                return false;
            }

            expression = schedule.Expression;
            return true;
        }

        /// <summary>One line under the selector: what the schedule means and its next runs, or why it is not valid.</summary>
        public static string Preview(ScheduleMode mode, string? text, string defaultDescription, DateTime nowUtc, TimeZoneInfo zone, int count = 3)
        {
            if (!TryBuild(mode, text, out var expression, out var error))
            {
                return error;
            }

            if (expression == null)
            {
                return defaultDescription;
            }

            var schedule = BackupSchedule.Parse(expression);
            if (schedule.IsOff)
            {
                return "Off: this folder is only backed up when its files change (or by hand).";
            }

            if (schedule.IsInterval)
            {
                return $"{schedule.Description}, counted from the last backup.";
            }

            var runs = schedule.NextOccurrences(nowUtc, count, zone)
                .Select(run => FormatRun(run, nowUtc, zone));
            var list = string.Join(", ", runs);
            return list.Length == 0 ? $"{schedule.Description}. No run in the next eight years." : $"{schedule.Description}. Next runs: {list}";
        }

        private static string FormatRun(DateTime runUtc, DateTime nowUtc, TimeZoneInfo zone)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(runUtc, zone);
            var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
            var day = local.Date == today ? "today" : local.Date == today.AddDays(1) ? "tomorrow" : local.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            return $"{day} {local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
        }
    }

    /// <summary>The days selector of the backup window.</summary>
    public enum WindowDaysChoice
    {
        EveryDay,
        Weekdays,
        Weekends,
        Custom
    }

    /// <summary>The backup window ("quiet hours") controls of Settings, without any UI.</summary>
    public static class BackupWindowEditorModel
    {
        public static (bool Enabled, string Start, string End, WindowDaysChoice Days, string CustomDays) FromConfig(BackupWindowConfig? config)
        {
            if (config == null)
            {
                return (false, "22:00", "06:00", WindowDaysChoice.EveryDay, string.Empty);
            }

            var days = (config.Days ?? "all").Trim();
            if (BackupWindow.TryCreate(config.Start, config.End, days, out var window, out _))
            {
                var (choice, custom) = window.DaysText switch
                {
                    "all" => (WindowDaysChoice.EveryDay, string.Empty),
                    "weekdays" => (WindowDaysChoice.Weekdays, string.Empty),
                    "weekends" => (WindowDaysChoice.Weekends, string.Empty),
                    var other => (WindowDaysChoice.Custom, other)
                };
                return (config.Enabled, window.StartText, window.EndText, choice, custom);
            }

            // Unparsable (hand-edited): show it as it is so it can be corrected.
            return (config.Enabled, config.Start, config.End, WindowDaysChoice.Custom, days);
        }

        /// <summary>
        /// The window to store. Null when it is switched off and there was none before (nothing to write); otherwise a config whose
        /// unknown members are those of <paramref name="existing"/>. False with a message when the entries are not valid.
        /// </summary>
        public static bool TryBuild(
            bool enabled, string? start, string? end, WindowDaysChoice days, string? customDays, BackupWindowConfig? existing,
            out BackupWindowConfig? config, out string error)
        {
            config = null;
            error = string.Empty;
            if (!enabled && existing == null)
            {
                return true;
            }

            var daysText = days switch
            {
                WindowDaysChoice.EveryDay => "all",
                WindowDaysChoice.Weekdays => "weekdays",
                WindowDaysChoice.Weekends => "weekends",
                _ => customDays?.Trim() ?? string.Empty
            };

            if (days == WindowDaysChoice.Custom && daysText.Length == 0)
            {
                error = "Enter the days the window starts on, for example mon,wed,fri or mon-fri.";
                return false;
            }

            if (!BackupWindow.TryCreate(start, end, daysText, out var window, out var parseError))
            {
                error = char.ToUpperInvariant(parseError[0]) + parseError[1..];
                return false;
            }

            config = new BackupWindowConfig
            {
                Enabled = enabled,
                Start = window.StartText,
                End = window.EndText,
                Days = window.DaysText,
                ExtensionData = existing?.ExtensionData
            };
            return true;
        }

        public static string Preview(bool enabled, string? start, string? end, WindowDaysChoice days, string? customDays)
        {
            if (!TryBuild(true, start, end, days, customDays, null, out var config, out var error))
            {
                return error;
            }

            var window = BackupWindow.FromConfig(config, out _)!;
            var text = window.IsAlwaysOpen
                ? "Open all day, every day: this restricts nothing."
                : window.Describe() + ". Manual backups always run; a backup that has started may finish after the window closes.";
            return enabled ? text : $"Switched off. {text}";
        }
    }
}
