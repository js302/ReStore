using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ReStore.Core.src.utils;

/// <summary>A stored variable value together with its registry kind ("String" or "ExpandString").</summary>
public sealed record StoredEnvironmentValue(string Value, string Kind);

/// <summary>Abstraction over where environment variables live, so merge/restore logic is testable off Windows.</summary>
public interface IEnvironmentVariableStore
{
    StoredEnvironmentValue? Get(string name, EnvironmentVariableTarget target);

    /// <summary>Writes a value. Throws when the target cannot be written (for example Machine without elevation).</summary>
    void Set(string name, string value, EnvironmentVariableTarget target, string kind);

    /// <summary>Deletes a variable (used by undo for variables the restore created). A missing variable is not an error.</summary>
    void Remove(string name, EnvironmentVariableTarget target)
        => throw new NotSupportedException("This environment store cannot remove variables.");

    /// <summary>Tells running applications that the environment changed. Called once after a batch of writes.</summary>
    void NotifyChanged();
}

public static class EnvironmentVariableKinds
{
    public const string String = "String";
    public const string ExpandString = "ExpandString";

    public static string Normalize(string? kind)
        => string.Equals(kind, ExpandString, StringComparison.OrdinalIgnoreCase) ? ExpandString : String;
}

/// <summary>
/// Default store: the registry for User/Machine on Windows (so REG_EXPAND_SZ is preserved and PATH is read
/// unexpanded), plain <see cref="Environment"/> calls otherwise.
/// </summary>
public sealed class DefaultEnvironmentVariableStore : IEnvironmentVariableStore
{
    private const string UserKeyPath = "Environment";
    private const string MachineKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public StoredEnvironmentValue? Get(string name, EnvironmentVariableTarget target)
    {
        if (OperatingSystem.IsWindows() && target != EnvironmentVariableTarget.Process)
        {
            return GetFromRegistry(name, target);
        }

        var value = Environment.GetEnvironmentVariable(name, target);
        return value == null ? null : new StoredEnvironmentValue(value, EnvironmentVariableKinds.String);
    }

    public void Set(string name, string value, EnvironmentVariableTarget target, string kind)
    {
        if (OperatingSystem.IsWindows() && target != EnvironmentVariableTarget.Process)
        {
            SetInRegistry(name, value, target, kind);
            return;
        }

        Environment.SetEnvironmentVariable(name, value, target);
    }

    public void Remove(string name, EnvironmentVariableTarget target)
    {
        if (OperatingSystem.IsWindows() && target != EnvironmentVariableTarget.Process)
        {
            RemoveFromRegistry(name, target);
            return;
        }

        Environment.SetEnvironmentVariable(name, null, target);
    }

    public void NotifyChanged()
    {
        if (OperatingSystem.IsWindows())
        {
            BroadcastSettingChange();
        }
    }

    /// <summary>Registry kind of an existing variable, or null when unavailable (non-Windows, Process target, missing).</summary>
    public static string? TryGetKind(string name, EnvironmentVariableTarget target)
    {
        if (!OperatingSystem.IsWindows() || target == EnvironmentVariableTarget.Process)
        {
            return null;
        }

        try
        {
            return GetFromRegistry(name, target)?.Kind;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static StoredEnvironmentValue? GetFromRegistry(string name, EnvironmentVariableTarget target)
    {
        using var key = OpenKey(target, writable: false);
        if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var kind = key.GetValueKind(name);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            return null;
        }

        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return value == null
            ? null
            : new StoredEnvironmentValue(
                value,
                kind == RegistryValueKind.ExpandString ? EnvironmentVariableKinds.ExpandString : EnvironmentVariableKinds.String);
    }

    [SupportedOSPlatform("windows")]
    private static void SetInRegistry(string name, string value, EnvironmentVariableTarget target, string kind)
    {
        using var key = OpenKey(target, writable: true)
            ?? throw new InvalidOperationException($"Could not open the {target} environment registry key for writing.");
        var registryKind = EnvironmentVariableKinds.Normalize(kind) == EnvironmentVariableKinds.ExpandString
            ? RegistryValueKind.ExpandString
            : RegistryValueKind.String;
        key.SetValue(name, value, registryKind);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveFromRegistry(string name, EnvironmentVariableTarget target)
    {
        using var key = OpenKey(target, writable: true)
            ?? throw new InvalidOperationException($"Could not open the {target} environment registry key for writing.");
        key.DeleteValue(name, throwOnMissingValue: false);
    }

    [SupportedOSPlatform("windows")]
    private static RegistryKey? OpenKey(EnvironmentVariableTarget target, bool writable)
    {
        return target == EnvironmentVariableTarget.Machine
            ? Registry.LocalMachine.OpenSubKey(MachineKeyPath, writable)
            : Registry.CurrentUser.OpenSubKey(UserKeyPath, writable);
    }

    [SupportedOSPlatform("windows")]
    private static void BroadcastSettingChange()
    {
        try
        {
            _ = SendMessageTimeout(
                new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);
        }
        catch (Exception)
        {
            // Best effort; new processes read the registry anyway.
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
