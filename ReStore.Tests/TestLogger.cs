using ReStore.Core.src.utils;
using System.Collections.Concurrent;

namespace ReStore.Tests;

public sealed class TestLogger : ILogger
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => [.. _messages];

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var line = $"[{DateTime.UtcNow:O}] [{level}] {message}";
        _messages.Enqueue(line);
        Console.WriteLine(line);
    }
}
