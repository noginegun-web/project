using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ScumOxygen.Core;

public sealed class PermissionService
{
    private readonly string _usersPath;
    private readonly string _groupsPath;
    private readonly string? _legacyPath;
    private readonly object _sync = new();
    private PermissionUsersFile _users = new();
    private PermissionGroupsFile _groups = new();

    public PermissionService(string legacyOrUsersPath)
        : this(
            Path.Combine(Path.GetDirectoryName(legacyOrUsersPath) ?? ".", "oxygen.users.json"),
            Path.Combine(Path.GetDirectoryName(legacyOrUsersPath) ?? ".", "oxygen.groups.json"),
            legacyOrUsersPath)
    {
    }

    public PermissionService(string usersPath, string groupsPath, string? legacyPath = null)
    {
        _usersPath = usersPath;
        _groupsPath = groupsPath;
        _legacyPath = legacyPath;
        Load();
    }

    public bool HasPermission(string userId, string permission)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        if (string.IsNullOrWhiteSpace(permission))
            return true;

        lock (_sync)
        {
            if (!_users.Users.TryGetValue(userId, out var user))
                return false;

            if (HasPermissionSet(user.Permissions, permission))
                return true;

            foreach (var group in user.Groups)
            {
                if (HasGroupPermission(group, permission, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }
    }

    public void GrantUserPermission(string userId, string permission, string? lastNickName = null)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(permission))
            return;

        lock (_sync)
        {
            var user = GetOrCreateUser(userId, lastNickName);
            AddDistinct(user.Permissions, permission);
            SaveUnsafe();
        }
    }

    public void RevokeUserPermission(string userId, string permission)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(permission))
            return;

        lock (_sync)
        {
            if (_users.Users.TryGetValue(userId, out var user))
            {
                RemoveMatching(user.Permissions, permission);
                SaveUnsafe();
            }
        }
    }

    public void GrantGroupPermission(string groupName, string permission)
    {
        if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(permission))
            return;

        lock (_sync)
        {
            var group = GetOrCreateGroup(groupName);
            AddDistinct(group.Permissions, permission);
            SaveUnsafe();
        }
    }

    public void RevokeGroupPermission(string groupName, string permission)
    {
        if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(permission))
            return;

        lock (_sync)
        {
            if (_groups.Groups.TryGetValue(groupName, out var group))
            {
                RemoveMatching(group.Permissions, permission);
                SaveUnsafe();
            }
        }
    }

    public void AddUserToGroup(string userId, string groupName, string? lastNickName = null)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(groupName))
            return;

        lock (_sync)
        {
            var user = GetOrCreateUser(userId, lastNickName);
            _ = GetOrCreateGroup(groupName);
            AddDistinct(user.Groups, groupName);
            SaveUnsafe();
        }
    }

    public void RemoveUserFromGroup(string userId, string groupName)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(groupName))
            return;

        lock (_sync)
        {
            if (_users.Users.TryGetValue(userId, out var user))
            {
                RemoveMatching(user.Groups, groupName);
                SaveUnsafe();
            }
        }
    }

    public void UpdateUserNickname(string userId, string nickName)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return;

        lock (_sync)
        {
            var user = GetOrCreateUser(userId, nickName);
            if (!string.IsNullOrWhiteSpace(nickName))
                user.LastNickName = nickName;
            SaveUnsafe();
        }
    }

    public void Reload()
    {
        lock (_sync)
        {
            LoadUnsafe();
        }
    }

    private void Load()
    {
        lock (_sync)
        {
            LoadUnsafe();
        }
    }

    private void LoadUnsafe()
    {
        EnsureFolders();

        _users = ReadJson(_usersPath, CreateDefaultUsersFile());
        _groups = ReadJson(_groupsPath, CreateDefaultGroupsFile());

        if (_groups.Groups.Count == 0)
            _groups = CreateDefaultGroupsFile();

        if (!string.IsNullOrWhiteSpace(_legacyPath) && File.Exists(_legacyPath))
        {
            TryImportLegacyPermissions(_legacyPath);
        }

        SaveUnsafe();
    }

    private void SaveUnsafe()
    {
        EnsureFolders();
        WriteJson(_usersPath, _users);
        WriteJson(_groupsPath, _groups);
    }

    private void EnsureFolders()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_usersPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_groupsPath)!);
        if (!string.IsNullOrWhiteSpace(_legacyPath))
            Directory.CreateDirectory(Path.GetDirectoryName(_legacyPath)!);
    }

    private PermissionUserEntry GetOrCreateUser(string userId, string? lastNickName)
    {
        if (!_users.Users.TryGetValue(userId, out var user))
        {
            user = new PermissionUserEntry();
            _users.Users[userId] = user;
        }

        if (!string.IsNullOrWhiteSpace(lastNickName))
            user.LastNickName = lastNickName;

        return user;
    }

    private PermissionGroupEntry GetOrCreateGroup(string groupName)
    {
        if (!_groups.Groups.TryGetValue(groupName, out var group))
        {
            group = new PermissionGroupEntry
            {
                Title = groupName,
                Rank = string.Equals(groupName, "admin", StringComparison.OrdinalIgnoreCase) ? 100 : 0,
                ParentGroup = string.Equals(groupName, "admin", StringComparison.OrdinalIgnoreCase) ? "default" : string.Empty
            };
            _groups.Groups[groupName] = group;
        }

        return group;
    }

    private static PermissionUsersFile ReadJson(string path, PermissionUsersFile fallback)
    {
        try
        {
            if (!File.Exists(path))
                return fallback;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PermissionUsersFile>(json, JsonOptions()) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static PermissionGroupsFile ReadJson(string path, PermissionGroupsFile fallback)
    {
        try
        {
            if (!File.Exists(path))
                return fallback;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PermissionGroupsFile>(json, JsonOptions()) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions(pretty: true));
        File.WriteAllText(path, json);
    }

    private void TryImportLegacyPermissions(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var legacy = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json, JsonOptions()) ?? new();
            foreach (var (userId, perms) in legacy)
            {
                var user = GetOrCreateUser(userId, null);
                foreach (var perm in perms ?? Array.Empty<string>())
                    AddDistinct(user.Permissions, perm);
            }
        }
        catch
        {
        }
    }

    private bool HasGroupPermission(string groupName, string permission, HashSet<string> visited)
    {
        if (!visited.Add(groupName))
            return false;

        if (!_groups.Groups.TryGetValue(groupName, out var group))
            return false;

        if (HasPermissionSet(group.Permissions, permission))
            return true;

        if (!string.IsNullOrWhiteSpace(group.ParentGroup))
            return HasGroupPermission(group.ParentGroup, permission, visited);

        return false;
    }

    private static bool HasPermissionSet(IEnumerable<string> permissions, string requestedPermission)
    {
        foreach (var permission in permissions)
        {
            if (PermissionMatches(permission, requestedPermission))
                return true;
        }

        return false;
    }

    private static bool PermissionMatches(string grantedPermission, string requestedPermission)
    {
        if (string.IsNullOrWhiteSpace(grantedPermission))
            return false;

        if (string.Equals(grantedPermission, "*", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(grantedPermission, requestedPermission, StringComparison.OrdinalIgnoreCase))
            return true;

        if (grantedPermission.EndsWith(".*", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = grantedPermission[..^1];
            return requestedPermission.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void AddDistinct(List<string> items, string value)
    {
        if (items.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
            return;

        items.Add(value);
    }

    private static void RemoveMatching(List<string> items, string value)
    {
        items.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
    }

    private static PermissionUsersFile CreateDefaultUsersFile()
    {
        return new PermissionUsersFile
        {
            Users = new Dictionary<string, PermissionUserEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["YOUR_STEAM_ID_HERE"] = new PermissionUserEntry
                {
                    LastNickName = "Owner",
                    Permissions = new List<string> { "*" },
                    Groups = new List<string> { "admin" }
                }
            }
        };
    }

    private static PermissionGroupsFile CreateDefaultGroupsFile()
    {
        return new PermissionGroupsFile
        {
            Groups = new Dictionary<string, PermissionGroupEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = new PermissionGroupEntry
                {
                    Title = "Player",
                    Rank = 0,
                    Permissions = new List<string>()
                },
                ["admin"] = new PermissionGroupEntry
                {
                    Title = "Admin",
                    Rank = 100,
                    ParentGroup = "default",
                    Permissions = new List<string> { "*" }
                }
            }
        };
    }

    private static JsonSerializerOptions JsonOptions(bool pretty = false)
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = pretty
        };
    }

    public sealed class PermissionUsersFile
    {
        public Dictionary<string, PermissionUserEntry> Users { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PermissionUserEntry
    {
        public string LastNickName { get; set; } = string.Empty;
        public List<string> Permissions { get; set; } = new();
        public List<string> Groups { get; set; } = new();
    }

    public sealed class PermissionGroupsFile
    {
        public Dictionary<string, PermissionGroupEntry> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PermissionGroupEntry
    {
        public string Title { get; set; } = string.Empty;
        public int Rank { get; set; }
        public string ParentGroup { get; set; } = string.Empty;
        public List<string> Permissions { get; set; } = new();
    }
}
