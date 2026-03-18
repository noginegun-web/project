using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ScumOxygen.Core.Models;

namespace Oxygen.Csharp.API;

public static class Server
{
    public static Action<string>? BroadcastImpl;
    public static Action<string>? AnnounceImpl;
    public static Func<string, CommandResult>? CommandImpl;
    public static Func<string, Task<CommandResult>>? CommandAsyncImpl;

    public static IReadOnlyList<PlayerBase> AllPlayers => Oxygen.ListPlayers();

    public static void Broadcast(string message) => BroadcastImpl?.Invoke(message);
    public static void Announce(string message) => (AnnounceImpl ?? BroadcastImpl)?.Invoke(message);

    public static CommandResult ProcessCommand(string command)
    {
        return CommandImpl?.Invoke(command) ?? CommandResult.Fail("command executor not configured", TimeSpan.Zero);
    }

    public static Task<CommandResult> ProcessCommandAsync(string command)
    {
        return CommandAsyncImpl?.Invoke(command) ?? Task.FromResult(CommandResult.Fail("command executor not configured", TimeSpan.Zero));
    }
}

public static class OxyConsole
{
    public static Action<string>? PrintImpl;

    public static void Print(string message)
    {
        PrintImpl?.Invoke(message);
    }
}

public readonly struct Vector3
{
    public readonly double X;
    public readonly double Y;
    public readonly double Z;

    public Vector3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public override string ToString() => $"{X:0.##},{Y:0.##},{Z:0.##}";
}

public readonly struct Color
{
    public readonly byte R;
    public readonly byte G;
    public readonly byte B;

    public Color(byte r, byte g, byte b)
    {
        R = r; G = g; B = b;
    }

    public static readonly Color Red = new(255, 60, 60);
    public static readonly Color White = new(255, 255, 255);
    public static readonly Color Green = new(80, 255, 120);
    public static readonly Color Yellow = new(255, 220, 80);
    public static readonly Color Blue = new(80, 160, 255);
    public static readonly Color Orange = new(255, 165, 0);

    public override string ToString() => $"{R},{G},{B}";
}

public sealed class CommandResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Raw { get; init; } = string.Empty;

    public static CommandResponse From(CommandResult result)
    {
        return new CommandResponse
        {
            Success = result.Success,
            Message = result.Response ?? string.Empty,
            Raw = result.Response ?? string.Empty
        };
    }
}

public sealed class KillInfo
{
    public DateTime Timestamp { get; init; }
    public string Weapon { get; init; } = string.Empty;
    public string WeaponDamage { get; init; } = string.Empty;
    public double Distance { get; init; }
    public bool IsSuicide { get; init; }
}

public sealed class DeathData
{
    public string KillerId { get; init; } = string.Empty;
    public string KillerName { get; init; } = string.Empty;
    public string DeadType { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public bool Event { get; init; }
    public float Distance { get; init; }
}

public sealed class PlayerRespawnData
{
    public int SpawnLocationType { get; init; }
    public int SectorX { get; init; }
    public int SectorY { get; init; }
}

public sealed class PlayerLockPickData
{
    public string Target { get; init; } = string.Empty;
    public string TargetId { get; init; } = string.Empty;
    public string OwnerSteamId { get; init; } = string.Empty;
    public string OwnerName { get; init; } = string.Empty;
    public int Result { get; init; }
    public int FailCount { get; init; }
}

public sealed class Item
{
    private readonly PlayerBase? _player;

    internal Item(string name, PlayerBase? player = null, IEnumerable<Item>? contents = null)
    {
        Name = name;
        _player = player;
        Contents = (contents ?? Array.Empty<Item>()).ToList().AsReadOnly();
    }

    public string Name { get; }
    public IReadOnlyList<Item> Contents { get; }

    public void Destroy()
    {
        // Full per-item destroy needs native entity bindings; keep the API shape ready.
        _player?.ProcessCommand($"DestroyItem {Name}");
    }

    public Item SetDurability(float percent)
    {
        _player?.ProcessCommand($"SetItemDurability {Name} {percent:0.##}");
        return this;
    }

    public Item SetAmmo(int count, string? ammoName = null)
    {
        var suffix = string.IsNullOrWhiteSpace(ammoName) ? string.Empty : $" {ammoName}";
        _player?.ProcessCommand($"SetItemAmmo {Name} {count}{suffix}");
        return this;
    }
}

public sealed class PlayerInventory
{
    private readonly PlayerBase _player;
    private IReadOnlyList<Item> _all = Array.Empty<Item>();

    internal PlayerInventory(PlayerBase player) => _player = player;

    public IReadOnlyList<Item> All => _all;
    public int Count => _all.Count;

    public int Clear()
    {
        var removed = _all.Count;
        _all = Array.Empty<Item>();
        _player.ProcessCommand("ClearInventory");
        return removed;
    }

    internal void SetSnapshot(IReadOnlyList<Item> items)
    {
        _all = items ?? Array.Empty<Item>();
    }
}

public sealed class PlayerBase
{
    public string SteamId { get; internal set; } = string.Empty;
    public string Steamid => SteamId;
    public string Name { get; internal set; } = string.Empty;
    public string CharacterName => Name;
    public string FakeName { get; internal set; } = string.Empty;
    public string IpAddress { get; internal set; } = string.Empty;
    public int DatabaseId { get; internal set; }
    public int NativePlayerId { get; internal set; }
    public int Ping { get; internal set; }
    public int FamePoints { get; internal set; }
    public int Money { get; internal set; }
    public int Gold { get; internal set; }
    public Vector3 Location { get; internal set; }
    public string ItemInHands { get; internal set; } = string.Empty;
    public IReadOnlyList<string> QuickAccessItems { get; internal set; } = Array.Empty<string>();
    public DateTimeOffset LastNativeUpdate { get; internal set; }

    public PlayerInventory Inventory { get; }

    public PlayerBase()
    {
        Inventory = new PlayerInventory(this);
    }

    public PlayerBase(string id)
    {
        SteamId = id;
        Inventory = new PlayerInventory(this);
    }

    public bool HasPermission(string permission)
    {
        if (string.IsNullOrWhiteSpace(permission)) return true;
        if (string.IsNullOrWhiteSpace(SteamId)) return false;

        try
        {
            return Oxygen.Permissions.HasPermission(SteamId, permission);
        }
        catch
        {
            return false;
        }
    }

    public void Reply(string message) => Reply(message, Color.Blue);

    public void Reply(string message, Color color)
    {
        var safe = Escape(message);
        if (DatabaseId > 0)
        {
            var response = Server.ProcessCommand($"SendNotification 1 {DatabaseId} \"{safe}\"");
            if (response.Success)
            {
                return;
            }
        }

        Server.Broadcast($"[{Name}] {safe}");
    }

    public void ProcessCommand(string command)
    {
        _ = Server.ProcessCommand(command);
    }

    public async Task<CommandResponse> ProcessCommandAsync(string command)
    {
        var res = await Server.ProcessCommandAsync(command);
        return CommandResponse.From(res);
    }

    public Item? GiveItem(string item)
    {
        if (string.IsNullOrWhiteSpace(item)) return null;
        ProcessCommand($"GiveItem {item}");
        var created = new Item(item, this);
        Inventory.SetSnapshot(Inventory.All.Concat(new[] { created }).ToList().AsReadOnly());
        return created;
    }

    public Item? EquipItem(string item)
    {
        if (string.IsNullOrWhiteSpace(item)) return null;
        ProcessCommand($"EquipItem {item}");
        ItemInHands = item;
        var equipped = new Item(item, this);
        var items = new List<Item> { equipped };
        items.AddRange(Inventory.All.Where(x => !string.Equals(x.Name, item, StringComparison.OrdinalIgnoreCase)));
        Inventory.SetSnapshot(items.AsReadOnly());
        return equipped;
    }

    private static string Escape(string msg)
    {
        return msg.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
