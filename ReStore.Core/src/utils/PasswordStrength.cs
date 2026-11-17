using System.Text.RegularExpressions;

namespace ReStore.Core.src.utils;

/// <summary>The password rules of the encryption setup, shared by the GUI wizard and the CLI: at least 8 characters, and a strength meter.</summary>
public sealed record PasswordStrength(string Label, int Length, int Score)
{
    public const int MinimumLength = 8;

    public bool IsWeak => Label == "Weak";

    /// <summary>The same scoring as the encryption setup window: length 8 and 12, upper, lower, digit and symbol each count one.</summary>
    public static PasswordStrength Evaluate(string password)
    {
        var score = 0;
        if (password.Length >= 8) score++;
        if (password.Length >= 12) score++;
        if (Regex.IsMatch(password, "[A-Z]")) score++;
        if (Regex.IsMatch(password, "[a-z]")) score++;
        if (Regex.IsMatch(password, @"\d")) score++;
        if (Regex.IsMatch(password, @"[!@#$%^&*(),.?""':{}|<>]")) score++;

        return new PasswordStrength(score <= 2 ? "Weak" : score <= 4 ? "Medium" : "Strong", password.Length, score);
    }
}
