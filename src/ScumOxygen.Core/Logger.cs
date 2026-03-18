using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ScumOxygen.Core;

public sealed class Logger
{
    private readonly string _logPath;
    private readonly object _lock = new();
    private readonly Queue<string> _tail = new();
    private const int MaxTailLines = 2000;

    public Logger(string logPath)
    {
        _logPath = logPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);
    public void Warn(string message) => Write("WARN", message);

    public IReadOnlyList<string> Snapshot(int limit = 200)
    {
        lock (_lock)
        {
            if (_tail.Count == 0)
                return Array.Empty<string>();

            var lines = _tail.ToArray();
            if (limit <= 0 || lines.Length <= limit)
                return lines;

            var slice = new string[limit];
            Array.Copy(lines, lines.Length - limit, slice, 0, limit);
            return slice;
        }
    }

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.UtcNow:O}] [{level}] {message}";
        lock (_lock)
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
            _tail.Enqueue(line);
            while (_tail.Count > MaxTailLines)
            {
                _tail.Dequeue();
            }
        }
    }
}
