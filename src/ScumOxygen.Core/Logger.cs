using System;
using System.IO;
using System.Threading;

namespace ScumOxygen.Core;

public sealed class Logger
{
    private readonly string _logPath;
    private readonly object _lock = new();

    public Logger(string logPath)
    {
        _logPath = logPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.UtcNow:O}] [{level}] {message}";
        lock (_lock)
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
    }
}
