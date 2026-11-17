using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ReStore.E2E;

/// <summary>
/// Windows only: a child created with CreateProcess and CREATE_NEW_PROCESS_GROUP so that it can be sent Ctrl+Break
/// (<c>GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid)</c>; .NET raises Console.CancelKeyPress for it). <see cref="System.Diagnostics.Process"/>
/// cannot set creation flags, hence the P/Invoke. stdin is an empty closed pipe; stdout and stderr are pipes.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeChild : ChildProcess
{
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint CtrlBreakEvent = 1;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;

    private readonly IntPtr _process;
    private readonly FileStream _stdout;
    private readonly FileStream _stderr;
    private readonly int _pid;

    private NativeChild(IntPtr process, int pid, FileStream stdout, FileStream stderr)
    {
        _process = process;
        _pid = pid;
        _stdout = stdout;
        _stderr = stderr;
    }

    public static NativeChild Start(string fileName, IEnumerable<string> arguments, IDictionary<string, string> environment, string workingDirectory)
    {
        EnsureConsole();

        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        CreatePipeChecked(out var stdinRead, out var stdinWrite, ref security);
        CreatePipeChecked(out var stdoutRead, out var stdoutWrite, ref security);
        CreatePipeChecked(out var stderrRead, out var stderrWrite, ref security);

        // Our ends must not be inherited by the child.
        foreach (var handle in new[] { stdinWrite, stdoutRead, stderrRead })
        {
            if (!SetHandleInformation(handle, HandleFlagInherit, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = StartfUseStdHandles,
            StdInput = stdinRead,
            StdOutput = stdoutWrite,
            StdError = stderrWrite
        };

        var commandLine = new StringBuilder(Quote(fileName));
        foreach (var argument in arguments)
        {
            commandLine.Append(' ').Append(Quote(argument));
        }

        var environmentBlock = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            environmentBlock.Append(key).Append('=').Append(value).Append('\0');
        }

        environmentBlock.Append('\0');

        var created = CreateProcessW(
            null, commandLine, IntPtr.Zero, IntPtr.Zero, bInheritHandles: true, CreateNewProcessGroup | CreateUnicodeEnvironment,
            environmentBlock.ToString(), workingDirectory, ref startup, out var information);
        var error = Marshal.GetLastWin32Error();

        // The child holds its own copies now.
        CloseHandle(stdinRead);
        CloseHandle(stdoutWrite);
        CloseHandle(stderrWrite);
        CloseHandle(stdinWrite);
        if (!created)
        {
            CloseHandle(stdoutRead);
            CloseHandle(stderrRead);
            throw new Win32Exception(error, "CreateProcess failed for " + fileName);
        }

        CloseHandle(information.Thread);
        return new NativeChild(
            information.Process,
            information.ProcessId,
            new FileStream(new SafeFileHandle(stdoutRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false),
            new FileStream(new SafeFileHandle(stderrRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false));
    }

    public override int Id => _pid;

    public override Stream StandardOutput => _stdout;

    public override Stream StandardError => _stderr;

    public override bool WaitForExit(int milliseconds) =>
        WaitForSingleObject(_process, milliseconds < 0 ? Infinite : (uint)milliseconds) == WaitObject0;

    public override int ExitCode => GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : throw new Win32Exception(Marshal.GetLastWin32Error());

    public override void KillTree()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(_pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already exited.
        }
    }

    public override void Interrupt()
    {
        if (!GenerateConsoleCtrlEvent(CtrlBreakEvent, (uint)_pid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GenerateConsoleCtrlEvent(CTRL_BREAK) failed");
        }
    }

    public override void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
        CloseHandle(_process);
    }

    /// <summary>The test host normally has a console. If it has none (started detached), create one so children can share it.</summary>
    private static void EnsureConsole()
    {
        var processes = new uint[1];
        if (GetConsoleProcessList(processes, 1) == 0)
        {
            AllocConsole();
        }
    }

    private static void CreatePipeChecked(out IntPtr read, out IntPtr write, ref SecurityAttributes security)
    {
        if (!CreatePipe(out read, out write, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>The CommandLineToArgvW quoting rules.</summary>
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                builder.Append('\\', backslashes * 2);
            }
            else if (argument[i] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[i]);
            }
        }

        return builder.Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
        public uint Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved3;
        public IntPtr StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint creationFlags, string environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}
