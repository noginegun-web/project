using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScumOxygen.Core;

public sealed class PermissionService
{
    private readonly string _path;
    private readonly Dictionary<string, HashSet<string>> _userPerms = new(StringComparer.OrdinalIgnoreCase);

    public PermissionService(string path)
    {
        _path = path;
        Load();
    }

    public bool HasPermission(string userId, string permission)
    {
        return _userPerms.TryGetValue(userId, out var set) && set.Contains(permission);
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, "{}");
            return;
        }

        var json = File.ReadAllText(_path);
        var data = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json) ?? new();
        _userPerms.Clear();
        foreach (var kv in data)
        {
            _userPerms[kv.Key] = new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase);
        }
    }
}
