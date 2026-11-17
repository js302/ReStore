using System.Text;

namespace ReStore.Core.src.utils;

/// <summary>Plain-text rendering of a restore plan (CLI <c>--plan</c>).</summary>
public static class SystemRestorePlanFormatter
{
    private const int MaxValueLength = 100;

    /// <param name="reveal">Show environment-variable values as they are; by default they are masked (see <see cref="SystemRestoreValueView"/>).</param>
    public static string Format(SystemRestorePlan plan, bool reveal = false)
    {
        var text = new StringBuilder();
        text.AppendLine($"System restore plan ({plan.Component}): {plan.ChangeCount} change(s), {plan.UnchangedCount} unchanged, {plan.NeedsAdminCount} need administrator rights");

        foreach (var action in plan.Actions)
        {
            var admin = action.NeedsAdmin ? " [needs admin]" : "";
            text.AppendLine($"  {Label(action.Change),-9} {action.Scope,-8} {action.Name}{admin}");
            if (action.Change is PlannedChange.Add or PlannedChange.Change)
            {
                var view = SystemRestoreValueView.Describe(action, reveal);
                if (view.Masked)
                {
                    if (action.OldValue != null)
                    {
                        text.AppendLine($"      old: {view.OldValue}");
                    }

                    text.AppendLine($"      new: {view.NewValue}");
                    foreach (var entry in view.PathAdded)
                    {
                        text.AppendLine($"      + {entry}");
                    }

                    foreach (var entry in view.PathRemoved)
                    {
                        text.AppendLine($"      - {entry}");
                    }

                    text.AppendLine("      (values hidden; pass --reveal to show them)");
                }
                else
                {
                    if (action.OldValue != null)
                    {
                        text.AppendLine($"      old: {Shorten(action.OldValue)}");
                    }

                    text.AppendLine($"      new: {Shorten(action.NewValue ?? "")}");
                }
            }

            if (action.Change == PlannedChange.Import)
            {
                foreach (var key in action.Keys)
                {
                    text.AppendLine($"      key: {key}");
                }
            }

            if (!string.IsNullOrEmpty(action.Detail))
            {
                text.AppendLine($"      ({action.Detail})");
            }
        }

        foreach (var rejected in plan.Rejected)
        {
            text.AppendLine($"  rejected  {rejected}");
        }

        return text.ToString().TrimEnd();
    }

    private static string Label(PlannedChange change) => change switch
    {
        PlannedChange.Add => "add",
        PlannedChange.Change => "change",
        PlannedChange.Unchanged => "same",
        PlannedChange.Install => "install",
        PlannedChange.AlreadyInstalled => "present",
        PlannedChange.Unavailable => "manual",
        PlannedChange.Import => "import",
        PlannedChange.Create => "create",
        PlannedChange.Replace => "replace",
        _ => "skip"
    };

    private static string Shorten(string value)
        => value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "...";
}

/// <summary>
/// What a restore plan may show of an environment variable. A plan is printed into terminals, logs and agent transcripts, so
/// by default values are masked: the name, whether it changes and the value length are shown. Names that look like secrets
/// (TOKEN, SECRET, PASSWORD, PASSWD, KEY, CREDENTIAL, AUTH, CONNECTION) get no length either. PATH-like variables list the
/// entries a restore would add or remove instead: those are folder names, not secrets. <c>--reveal</c> shows everything.
/// </summary>
public static partial class SystemRestoreValueView
{
    public const string MaskedValue = "***";

    public sealed record View(
        bool Masked, string? OldValue, string? NewValue, int? OldLength, int? NewLength, bool Changed,
        IReadOnlyList<string> PathAdded, IReadOnlyList<string> PathRemoved);

    public static bool IsSecretName(string name) => SecretNamePattern().IsMatch(name);

    public static bool IsPathLike(string name) =>
        name.EndsWith("PATH", StringComparison.OrdinalIgnoreCase) || name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase);

    public static View Describe(PlannedAction action, bool reveal)
    {
        var changed = !string.Equals(action.OldValue, action.NewValue, StringComparison.Ordinal);
        if (reveal || !action.Component.Equals("environment", StringComparison.OrdinalIgnoreCase))
        {
            return new View(false, action.OldValue, action.NewValue, action.OldValue?.Length, action.NewValue?.Length, changed, [], []);
        }

        var secret = IsSecretName(action.Name);
        var oldValue = action.OldValue == null ? null : secret ? MaskedValue : $"{MaskedValue} ({action.OldValue.Length} chars)";
        var newValue = action.NewValue == null ? null : secret ? MaskedValue : $"{MaskedValue} ({action.NewValue.Length} chars)";
        IReadOnlyList<string> added = [];
        IReadOnlyList<string> removed = [];
        if (!secret && IsPathLike(action.Name))
        {
            var before = Split(action.OldValue);
            var after = Split(action.NewValue);
            added = [.. after.Where(e => !before.Contains(e, StringComparer.OrdinalIgnoreCase))];
            removed = [.. before.Where(e => !after.Contains(e, StringComparer.OrdinalIgnoreCase))];
        }

        return new View(true, oldValue, newValue, secret ? null : action.OldValue?.Length, secret ? null : action.NewValue?.Length, changed, added, removed);
    }

    private static List<string> Split(string? value) =>
        value == null ? [] : [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    [System.Text.RegularExpressions.GeneratedRegex("TOKEN|SECRET|PASSWORD|PASSWD|KEY|CREDENTIAL|AUTH|CONNECTION", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SecretNamePattern();
}
