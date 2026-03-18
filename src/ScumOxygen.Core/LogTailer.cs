using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ScumOxygen.Core;

public sealed class LogTailer
{
    private readonly string _dir;
    private readonly string _pattern;
    private readonly Encoding _encoding;
    private string? _currentFile;
    private long _position;
    private bool _initialized;

    public LogTailer(string directory, string pattern, Encoding encoding)
    {
        _dir = directory;
        _pattern = pattern;
        _encoding = encoding;
    }

    public IEnumerable<string> ReadNewLines()
    {
        var file = PickLatestFile();
        if (file == null) return Array.Empty<string>();

        if (!string.Equals(_currentFile, file, StringComparison.OrdinalIgnoreCase))
        {
            _currentFile = file;
            _position = _initialized && File.Exists(file) ? new FileInfo(file).Length : 0;
            _initialized = true;
        }

        if (_currentFile == null) return Array.Empty<string>();
        var info = new FileInfo(_currentFile);
        if (!info.Exists) return Array.Empty<string>();
        if (info.Length < _position) _position = 0;

        var lines = new List<string>();
        using var fs = new FileStream(_currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(_position, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, _encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        while (!sr.EndOfStream)
        {
            var line = sr.ReadLine();
            if (line != null)
                lines.Add(line);
        }
        _position = fs.Position;
        return lines;
    }

    private string? PickLatestFile()
    {
        if (!Directory.Exists(_dir)) return null;
        return Directory.GetFiles(_dir, _pattern)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }
}
