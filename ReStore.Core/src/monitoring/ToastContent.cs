using System.Xml.Linq;

namespace ReStore.Core.src.monitoring;

/// <summary>What a toast button (or the toast body) asks the app to do when activated.</summary>
public enum ToastAction { Open, RetryNow, OpenLog, Unlock, BackUpNow, ViewReport }

/// <summary>A parsed toast activation: the action plus the page a plain click should open.</summary>
public sealed record ToastActivation(ToastAction Action, NotificationTarget Target, string? Token = null);

/// <summary>
/// Activation arguments carried by toasts as a protocol URI (<c>restore-toast:action=retry&amp;target=dashboard</c>).
/// Windows launches the packaged app with the URI as its command-line argument; a running instance receives it through
/// the single-instance pipe. Pure and WPF-free.
/// </summary>
public static class ToastActivationArgs
{
    public const string Scheme = "restore-toast";

    private static readonly (ToastAction Action, string Token)[] Tokens =
    [
        (ToastAction.Open, "open"),
        (ToastAction.RetryNow, "retry"),
        (ToastAction.OpenLog, "log"),
        (ToastAction.Unlock, "unlock"),
        (ToastAction.BackUpNow, "backup"),
        (ToastAction.ViewReport, "report"),
    ];

    // In memory only: a toast from a previous run, or a link from a web page, can never carry it.
    private static readonly string ProcessToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    /// <summary>The per-process secret that toast URIs of this run carry.</summary>
    public static string CurrentToken => ProcessToken;

    public static string Build(ToastAction action, NotificationTarget target, string? token = null) =>
        $"{Scheme}:action={Tokens.First(t => t.Action == action).Token}&target={target.ToString().ToLowerInvariant()}"
        + (string.IsNullOrEmpty(token) ? string.Empty : $"&t={token}");

    /// <summary>True only when the activation carries exactly <paramref name="expectedToken"/> (constant-time compare).</summary>
    public static bool IsAuthorized(ToastActivation activation, string expectedToken)
    {
        if (string.IsNullOrEmpty(activation.Token) || string.IsNullOrEmpty(expectedToken))
        {
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(activation.Token),
            System.Text.Encoding.UTF8.GetBytes(expectedToken));
    }

    /// <summary>True for anything that looks like a toast URI (even a malformed one), so callers can swallow it.</summary>
    public static bool IsToastArgument(string? raw) =>
        raw != null && raw.Trim().Trim('"').StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? raw, out ToastActivation activation)
    {
        activation = new ToastActivation(ToastAction.Open, NotificationTarget.None);
        if (!IsToastArgument(raw))
        {
            return false;
        }

        var text = raw!.Trim().Trim('"');
        text = text[(Scheme.Length + 1)..].TrimStart('/');
        if (text.StartsWith('?'))
        {
            text = text[1..];
        }

        string? actionToken = null;
        string? targetToken = null;
        string? token = null;
        foreach (var pair in text.TrimEnd('/').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(kv[0]).Trim();
            var value = Uri.UnescapeDataString(kv[1]).Trim();
            if (key.Equals("action", StringComparison.OrdinalIgnoreCase))
            {
                actionToken = value;
            }
            else if (key.Equals("t", StringComparison.OrdinalIgnoreCase))
            {
                token = value;
            }
            else if (key.Equals("target", StringComparison.OrdinalIgnoreCase))
            {
                targetToken = value;
            }
        }

        var match = Tokens.FirstOrDefault(t => t.Token.Equals(actionToken, StringComparison.OrdinalIgnoreCase));
        if (match.Token == null)
        {
            return false;
        }

        var target = Enum.TryParse<NotificationTarget>(targetToken, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : NotificationTarget.None;
        activation = new ToastActivation(match.Action, target, token);
        return true;
    }
}

/// <summary>Builds the Windows toast XML (ToastGeneric) for a notification. Pure: no WinRT types.</summary>
public static class ToastXmlBuilder
{
    public static string ButtonLabel(ToastAction action) => action switch
    {
        ToastAction.RetryNow => "Retry now",
        ToastAction.OpenLog => "Open log",
        ToastAction.Unlock => "Unlock",
        ToastAction.BackUpNow => "Back up now",
        ToastAction.ViewReport => "View report",
        _ => "Open ReStore"
    };

    public static string Build(string title, string message, NotificationTarget target, IReadOnlyList<ToastAction>? actions, string? token = null)
    {
        var toast = new XElement("toast",
            new XAttribute("launch", ToastActivationArgs.Build(ToastAction.Open, target, token)),
            new XAttribute("activationType", "protocol"),
            new XElement("visual",
                new XElement("binding",
                    new XAttribute("template", "ToastGeneric"),
                    new XElement("text", title),
                    new XElement("text", message))));

        var buttons = (actions ?? []).Where(a => a != ToastAction.Open).Distinct().Take(5).ToList();
        if (buttons.Count > 0)
        {
            toast.Add(new XElement("actions", buttons.Select(a => new XElement("action",
                new XAttribute("content", ButtonLabel(a)),
                new XAttribute("arguments", ToastActivationArgs.Build(a, target, token)),
                new XAttribute("activationType", "protocol")))));
        }

        return toast.ToString(SaveOptions.DisableFormatting);
    }
}
