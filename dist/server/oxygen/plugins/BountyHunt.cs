using System;
using System.Collections.Generic;
using System.Linq;
using Oxygen.Csharp.API;
using Oxygen.Csharp.Core;

public class BountyHuntConfig
{
    public int BroadcastThreshold { get; set; } = 3;
    public int HeroThreshold { get; set; } = 5;
    public int MaxBoardEntries { get; set; } = 5;
}

public class BountyHuntEntry
{
    public string SteamId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int CurrentStreak { get; set; }
    public int BestStreak { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

[Info("Bounty Hunt", "Codex", "1.0.0")]
[Description("Следит за сериями убийств, показывает активные bounty и добавляет живой PvP-слой серверу.")]
public class BountyHunt : OxygenPlugin
{
    private BountyHuntConfig _config = new();
    private Dictionary<string, BountyHuntEntry> _stats = new(StringComparer.OrdinalIgnoreCase);

    public override void OnLoad()
    {
        _config = LoadConfig<BountyHuntConfig>() ?? new BountyHuntConfig();
        _stats = LoadData<Dictionary<string, BountyHuntEntry>>("BountyHunt_Data")
                 ?? new Dictionary<string, BountyHuntEntry>(StringComparer.OrdinalIgnoreCase);
        SaveConfig(_config);
    }

    public override void OnUnload()
    {
        SaveData("BountyHunt_Data", _stats);
    }

    public override void OnPlayerKill(PlayerBase killer, PlayerBase victim, KillInfo info)
    {
        if (killer == null || victim == null) return;
        if (string.Equals(killer.SteamId, victim.SteamId, StringComparison.OrdinalIgnoreCase)) return;

        var killerEntry = Touch(killer);
        var victimEntry = Touch(victim);
        var victimPreviousStreak = victimEntry.CurrentStreak;

        killerEntry.Kills++;
        killerEntry.CurrentStreak++;
        killerEntry.BestStreak = Math.Max(killerEntry.BestStreak, killerEntry.CurrentStreak);
        killerEntry.UpdatedAtUtc = DateTime.UtcNow;

        victimEntry.Deaths++;
        victimEntry.CurrentStreak = 0;
        victimEntry.UpdatedAtUtc = DateTime.UtcNow;

        if (killerEntry.CurrentStreak >= _config.BroadcastThreshold)
        {
            Server.Broadcast($"[Bounty] {killerEntry.Name} на серии {killerEntry.CurrentStreak} убийств.");
        }

        if (killerEntry.CurrentStreak == _config.HeroThreshold)
        {
            Server.Broadcast($"[Bounty] {killerEntry.Name} стал целью сервера. Остановите его.");
        }

        if (victimPreviousStreak >= _config.BroadcastThreshold)
        {
            Server.Broadcast($"[Bounty] {killerEntry.Name} остановил серию {victimEntry.Name} ({victimPreviousStreak}).");
        }

        SaveData("BountyHunt_Data", _stats);
    }

    public override void OnPlayerDeath(PlayerBase victim, PlayerBase? killer, KillInfo info)
    {
        if (victim == null) return;
        var entry = Touch(victim);
        entry.UpdatedAtUtc = DateTime.UtcNow;
    }

    [Command("streak", "Показывает текущую серию игрока")]
    private void ShowMyStreak(PlayerBase player, string[] args)
    {
        var entry = Touch(player);
        player.Reply($"Текущая серия: {entry.CurrentStreak} | Лучшая серия: {entry.BestStreak} | Убийств: {entry.Kills} | Смертей: {entry.Deaths}", Color.Blue);
    }

    [Command("bounty", "Показывает активные серии убийств")]
    private void ShowBounties(PlayerBase player, string[] args)
    {
        var active = _stats.Values
            .Where(x => x.CurrentStreak >= _config.BroadcastThreshold)
            .OrderByDescending(x => x.CurrentStreak)
            .ThenBy(x => x.Name)
            .Take(Math.Max(1, _config.MaxBoardEntries))
            .ToList();

        if (active.Count == 0)
        {
            player.Reply("Сейчас нет активных bounty.", Color.Yellow);
            return;
        }

        var lines = active.Select((entry, index) => $"{index + 1}. {entry.Name} - серия {entry.CurrentStreak}");
        player.Reply("Активные bounty:\n" + string.Join("\n", lines), Color.Blue);
    }

    [Command("huntertop", "Топ лучших серий")]
    private void ShowTop(PlayerBase player, string[] args)
    {
        var top = _stats.Values
            .OrderByDescending(x => x.BestStreak)
            .ThenByDescending(x => x.Kills)
            .ThenBy(x => x.Name)
            .Take(Math.Max(1, _config.MaxBoardEntries))
            .ToList();

        if (top.Count == 0)
        {
            player.Reply("Статистика пока пуста.", Color.Yellow);
            return;
        }

        var lines = top.Select((entry, index) => $"{index + 1}. {entry.Name} - best {entry.BestStreak}, kills {entry.Kills}, deaths {entry.Deaths}");
        player.Reply("Топ охотников:\n" + string.Join("\n", lines), Color.Blue);
    }

    private BountyHuntEntry Touch(PlayerBase player)
    {
        var key = string.IsNullOrWhiteSpace(player?.SteamId) ? player?.Name ?? "unknown" : player.SteamId;
        if (!_stats.TryGetValue(key, out var entry))
        {
            entry = new BountyHuntEntry
            {
                SteamId = player?.SteamId ?? string.Empty,
                Name = player?.Name ?? "Unknown"
            };
            _stats[key] = entry;
        }

        if (player != null)
        {
            if (!string.IsNullOrWhiteSpace(player.SteamId))
                entry.SteamId = player.SteamId;
            if (!string.IsNullOrWhiteSpace(player.Name))
                entry.Name = player.Name;
        }

        return entry;
    }
}
