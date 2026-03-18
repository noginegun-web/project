using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Oxygen.Csharp.API;

namespace ScumOxygen.Core;

public sealed class PlayerRegistry
{
    private readonly Dictionary<string, PlayerBase> _bySteam = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlayerBase> _byName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LiveIdentityTtl = TimeSpan.FromMinutes(15);

    public IReadOnlyList<PlayerBase> List()
    {
        return _bySteam.Values
            .GroupBy(p => RuntimeHelpers.GetHashCode(p))
            .Select(g => g.First())
            .ToList()
            .AsReadOnly();
    }

    public PlayerBase? Find(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (_bySteam.TryGetValue(query, out var bySteam)) return bySteam;
        if (_byName.TryGetValue(query, out var byName)) return byName;
        return _byName.Values
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name) &&
                                 p.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    public PlayerBase UpsertFromLogin(string steamId, string name, int databaseId = 0, string ipAddress = "", Vector3? location = null)
    {
        var hasSteam = !string.IsNullOrWhiteSpace(steamId);
        var key = hasSteam ? steamId : $"name:{name}";

        if (_bySteam.TryGetValue(key, out var existing))
        {
            if (hasSteam) existing.SteamId = steamId;
            if (!string.IsNullOrWhiteSpace(name) && !ShouldPreserveLiveIdentity(existing, name)) existing.Name = name;
            if (databaseId > 0) existing.DatabaseId = databaseId;
            if (!string.IsNullOrWhiteSpace(ipAddress)) existing.IpAddress = ipAddress;
            if (location.HasValue && (HasKnownLocation(location.Value) || !HasKnownLocation(existing.Location))) existing.Location = location.Value;
            if (!string.IsNullOrWhiteSpace(existing.Name)) _byName[existing.Name] = existing;
            return existing;
        }

        // If we previously tracked by name only and now have SteamId, migrate.
        if (hasSteam && !string.IsNullOrWhiteSpace(name))
        {
            var nameKey = $"name:{name}";
            if (_bySteam.TryGetValue(nameKey, out var temp))
            {
                _bySteam.Remove(nameKey);
                temp.SteamId = steamId;
                if (!ShouldPreserveLiveIdentity(temp, name))
                    temp.Name = name;
                if (databaseId > 0) temp.DatabaseId = databaseId;
                if (!string.IsNullOrWhiteSpace(ipAddress)) temp.IpAddress = ipAddress;
                if (location.HasValue && (HasKnownLocation(location.Value) || !HasKnownLocation(temp.Location))) temp.Location = location.Value;
                _bySteam[steamId] = temp;
                _byName[name] = temp;
                return temp;
            }
        }

        var p = new PlayerBase
        {
            SteamId = hasSteam ? steamId : string.Empty,
            Name = name,
            DatabaseId = databaseId,
            IpAddress = ipAddress,
            Location = location ?? default
        };
        _bySteam[key] = p;
        if (!string.IsNullOrWhiteSpace(name)) _byName[name] = p;
        return p;
    }

    public PlayerBase UpsertFromNative(string name, Vector3 location, string steamId = "")
    {
        PlayerBase? player = null;

        if (!string.IsNullOrWhiteSpace(steamId) && _bySteam.TryGetValue(steamId, out var bySteam))
        {
            player = bySteam;
        }

        if (!string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name, out var byName))
        {
            player = byName;
        }

        if (player == null)
        {
            var key = !string.IsNullOrWhiteSpace(steamId)
                ? steamId
                : (!string.IsNullOrWhiteSpace(name) ? $"name:{name}" : $"native:{Guid.NewGuid():N}");
            if (!_bySteam.TryGetValue(key, out player))
            {
                player = new PlayerBase
                {
                    SteamId = steamId,
                    Name = name
                };
                _bySteam[key] = player;
            }
        }

        if (!string.IsNullOrWhiteSpace(steamId))
        {
            player.SteamId = steamId;
            _bySteam[steamId] = player;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            player.Name = name;
            _byName[name] = player;
        }

        player.Location = location;
        return player;
    }

    public void UpdateNativePosition(string name, Vector3 location)
    {
        var player = UpsertFromNative(name, location);
        player.Location = location;
    }

    public void RemoveFromLogin(string steamId, string name)
    {
        var hasSteam = !string.IsNullOrWhiteSpace(steamId);
        if (hasSteam)
        {
            if (_bySteam.TryGetValue(steamId, out var p))
            {
                _bySteam.Remove(steamId);
                if (!string.IsNullOrWhiteSpace(p.Name)) _byName.Remove(p.Name);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var key = $"name:{name}";
            _bySteam.Remove(key);
            _byName.Remove(name);
        }
    }

    public (List<PlayerBase> joined, List<PlayerBase> left) UpdateFromSnapshot(IEnumerable<PlayerSnapshot> snapshot)
    {
        var joined = new List<PlayerBase>();
        var left = new List<PlayerBase>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in snapshot)
        {
            if (string.IsNullOrWhiteSpace(s.SteamId)) continue;
            seen.Add(s.SteamId);

            if (!_bySteam.TryGetValue(s.SteamId, out var p))
            {
                if (!string.IsNullOrWhiteSpace(s.Name) && _byName.TryGetValue(s.Name, out var byName))
                {
                    p = byName;
                    var oldNameKey = $"name:{s.Name}";
                    if (_bySteam.ContainsKey(oldNameKey))
                        _bySteam.Remove(oldNameKey);
                    _bySteam[s.SteamId] = p;
                }
                else
                {
                    p = new PlayerBase
                    {
                        SteamId = s.SteamId,
                        Name = s.Name,
                        IpAddress = s.IpAddress,
                        DatabaseId = s.Id,
                        Money = s.Money,
                        Location = s.Location
                    };
                    _bySteam[s.SteamId] = p;
                    if (!string.IsNullOrWhiteSpace(p.Name))
                        _byName[p.Name] = p;
                    joined.Add(p);
                }
            }

            p.SteamId = s.SteamId;
            if (!string.IsNullOrWhiteSpace(s.Name) && !ShouldPreserveLiveIdentity(p, s.Name))
                p.Name = s.Name;
            if (!string.IsNullOrWhiteSpace(s.IpAddress))
                p.IpAddress = s.IpAddress;
            p.DatabaseId = s.Id;
            p.Money = s.Money;
            if (HasKnownLocation(s.Location) || !HasKnownLocation(p.Location))
                p.Location = s.Location;
            if (!string.IsNullOrWhiteSpace(p.Name))
                _byName[p.Name] = p;
        }

        var toRemove = _bySteam.Keys.Where(k => !seen.Contains(k)).ToList();
        foreach (var steamId in toRemove)
        {
            if (_bySteam.TryGetValue(steamId, out var p))
            {
                left.Add(p);
                _bySteam.Remove(steamId);
                if (!string.IsNullOrWhiteSpace(p.Name))
                    _byName.Remove(p.Name);
            }
        }

        return (joined, left);
    }

    private static bool ShouldPreserveLiveIdentity(PlayerBase player, string incomingName)
    {
        if (string.IsNullOrWhiteSpace(player.Name) || string.IsNullOrWhiteSpace(incomingName))
            return false;

        if (string.Equals(player.Name, incomingName, StringComparison.OrdinalIgnoreCase))
            return false;

        return player.LastNativeUpdate > DateTimeOffset.UtcNow.Subtract(LiveIdentityTtl);
    }

    private static bool HasKnownLocation(Vector3 location)
    {
        return Math.Abs(location.X) > 0.001 || Math.Abs(location.Y) > 0.001 || Math.Abs(location.Z) > 0.001;
    }
}

public readonly struct PlayerSnapshot
{
    public int Id { get; init; }
    public string Name { get; init; }
    public string SteamId { get; init; }
    public string IpAddress { get; init; }
    public int Money { get; init; }
    public Vector3 Location { get; init; }
}
