using System.Diagnostics;
using System.Text;

namespace ReStore.E2E;

/// <summary>One launched CLI process: how to read its pipes, wait for it, interrupt it and kill it.</summary>
internal abstract class ChildProcess : IDisposable
{
    public abstract int Id { get; }

    public abstract Stream StandardOutput { get; }

    public abstract Stream StandardError { get; }

    public abstract bool WaitForExit(int milliseconds);

    public abstract int ExitCode { get; }

    public abstract void KillTree();

    /// <summary>Ctrl+C: SIGINT on POSIX. On Windows the child must have been started in its own process group and receives Ctrl+Break.</summary>
    public abstract void Interrupt();

    public abstract void Dispose();
}

/// <summary>A child started through <see cref="Process"/>.</summary>
internal sealed class ManagedChild : ChildProcess
{
    private readonly Process _process;

    private ManagedChild(Process process) => _process = process;

    public static ManagedChild Start(string fileName, IEnumerable<string> arguments, IDictionary<string, string> environment, string workingDirectory, string? stdin, bool keepStdinOpen)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + fileName);
        try
        {
            if (stdin != null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Flush();
            }

            if (!keepStdinOpen)
            {
                process.StandardInput.Close();
            }
        }
        catch (IOException)
        {
            // The child may already be gone (it did not need its input).
        }

        return new ManagedChild(process);
    }

    public override int Id => _process.Id;

    public override Stream StandardOutput => _process.StandardOutput.BaseStream;

    public override Stream StandardError => _process.StandardError.BaseStream;

    public override bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);

    public override int ExitCode => _process.ExitCode;

    public override void KillTree()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    public override void Interrupt()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new NotSupportedException("Start the child with NewProcessGroup to interrupt it on Windows.");
        }

        using var kill = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-INT", Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false })!;
        kill.WaitForExit();
        if (kill.ExitCode != 0)
        {
            throw new InvalidOperationException($"kill -INT {Id} failed ({kill.ExitCode}).");
        }
    }

    public override void Dispose() => _process.Dispose();
}
