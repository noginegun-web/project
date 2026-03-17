using System;
using System.IO;

namespace ScumOxygen.Core;

public sealed class CommandQueue
{
    private readonly string _path;
    private long _lastSize = 0;

    public CommandQueue(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path)) File.WriteAllText(_path, string.Empty);
    }

    public string[] ReadNewLines()
    {
        var info = new FileInfo(_path);
        if (info.Length == _lastSize) return Array.Empty<string>();

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(_lastSize, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        var text = sr.ReadToEnd();
        _lastSize = info.Length;
        return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
