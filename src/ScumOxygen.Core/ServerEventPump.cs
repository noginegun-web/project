using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Oxygen.Csharp.API;

namespace ScumOxygen.Core;

public sealed class ServerEventPump
{
    private readonly Logger _log;
    private readonly TimerService _timers;
    private readonly CommandService _commands;
    private readonly PlayerRegistry _players;
    private readonly OxygenRuntime _runtime;

    private LogTailer? _chatTailer;
    private LogTailer? _loginTailer;
    private LogTailer? _killTailer;
    private LogTailer? _eventKillTailer;
    private Guid _logTimer;
    private Guid _playerTimer;

    public ServerEventPump(Logger log, TimerService timers, CommandService commands, PlayerRegistry players, OxygenRuntime runtime)
    {
        _log = log;
        _timers = timers;
        _commands = commands;
        _players = players;
        _runtime = runtime;
    }

    public void Start()
    {
        var logsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Saved", "SaveFiles", "Logs");
        logsDir = System.IO.Path.GetFullPath(logsDir);

        _chatTailer = new LogTailer(logsDir, "chat_*.log", Encoding.Unicode);
        _loginTailer = new LogTailer(logsDir, "login_*.log", Encoding.Unicode);
        _killTailer = new LogTailer(logsDir, "kill_*.log", Encoding.Unicode);
        _eventKillTailer = new LogTailer(logsDir, "event_kill_*.log", Encoding.Unicode);

        _logTimer = _timers.Every(TimeSpan.FromSeconds(2), PollLogs);
        _playerTimer = _timers.Every(TimeSpan.FromSeconds(10), PollPlayers);
        _log.Info(_commands.Enabled
            ? "[EventPump] Player snapshot source: RCON listplayers."
            : "[EventPump] Player snapshot source: SCUM.db last_login/last_logout heuristic.");
    }

    public void Stop()
    {
        if (_logTimer != Guid.Empty) _timers.Cancel(_logTimer);
        if (_playerTimer != Guid.Empty) _timers.Cancel(_playerTimer);
    }

    private async void PollPlayers()
    {
        List<PlayerSnapshot> snapshot;
        if (_commands.Enabled)
        {
            var res = await _commands.ExecuteAsync("listplayers");
            if (!res.Success) return;
            snapshot = ParsePlayers(res.Response ?? string.Empty);
        }
        else
        {
            snapshot = _runtime.ReadPlayerSnapshotFromDb();
        }

        var (joined, left) = _players.UpdateFromSnapshot(snapshot);
        foreach (var p in joined)
        {
            _runtime.DispatchPlayerConnected(p);
        }
        foreach (var p in left)
        {
            _runtime.DispatchPlayerDisconnected(p);
        }
    }

    private void PollLogs()
    {
        if (_killTailer != null)
        {
            foreach (var line in _killTailer.ReadNewLines())
            {
                if (TryParseKillLine(line, out var kill))
                {
                    var killer = !string.IsNullOrWhiteSpace(kill.KillerSteamId)
                        ? _players.UpsertFromLogin(kill.KillerSteamId, kill.KillerName)
                        : null;
                    var victim = _players.UpsertFromLogin(kill.VictimSteamId, kill.VictimName);

                    _runtime.DispatchPlayerDeath(victim, killer, kill.Info);
                    if (killer != null)
                    {
                        _runtime.DispatchPlayerKill(killer, victim, kill.Info);
                    }
                }
            }
        }

        if (_eventKillTailer != null)
        {
            foreach (var line in _eventKillTailer.ReadNewLines())
            {
                if (TryParseKillLine(line, out var kill))
                {
                    var killer = !string.IsNullOrWhiteSpace(kill.KillerSteamId)
                        ? _players.UpsertFromLogin(kill.KillerSteamId, kill.KillerName)
                        : null;
                    var victim = _players.UpsertFromLogin(kill.VictimSteamId, kill.VictimName);

                    _runtime.DispatchPlayerDeath(victim, killer, kill.Info);
                    if (killer != null)
                    {
                        _runtime.DispatchPlayerKill(killer, victim, kill.Info);
                    }
                }
            }
        }

        if (_chatTailer != null)
        {
            foreach (var line in _chatTailer.ReadNewLines())
            {
                if (TryParseChatLine(line, out var channel, out var name, out var message))
                {
                    var player = _players.Find(name) ?? new PlayerBase { Name = name };
                    var chatType = MapChatType(channel);

                    // Command handling: /command arg1 arg2
                    if (message.StartsWith("/") || message.StartsWith("!"))
                    {
                        var trimmed = message.TrimStart('/', '!');
                        var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        var cmd = split.Length > 0 ? split[0] : string.Empty;
                        var argsPart = split.Length > 1 ? split[1] : string.Empty;
                        var args = string.IsNullOrWhiteSpace(argsPart) ? Array.Empty<string>() : argsPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (!string.IsNullOrWhiteSpace(cmd))
                        {
                            var userId = !string.IsNullOrWhiteSpace(player.SteamId) ? player.SteamId : $"name:{player.Name}";
                            _runtime.Commands.TryExecute(cmd, args, _runtime.Permissions, player, userId, _log);
                        }
                    }

                    var allow = _runtime.DispatchPlayerChat(player, message, chatType);
                    if (!allow)
                    {
                        // Best-effort: no direct cancel from logs
                    }
                }
            }
        }

        if (_loginTailer != null)
        {
            foreach (var line in _loginTailer.ReadNewLines())
            {
                if (TryParseLoginLine(line, out var steamId, out var name, out var isJoin))
                {
                    var player = _players.UpsertFromLogin(steamId, name);
                    if (isJoin)
                    {
                        _runtime.DispatchPlayerConnected(player);
                    }
                    else
                    {
                        _players.RemoveFromLogin(steamId, name);
                        _runtime.DispatchPlayerDisconnected(player);
                    }
                }
            }
        }
    }

    private static int MapChatType(string channel)
    {
        if (channel.Contains("global", StringComparison.OrdinalIgnoreCase)) return 2;
        if (channel.Contains("local", StringComparison.OrdinalIgnoreCase)) return 1;
        if (channel.Contains("squad", StringComparison.OrdinalIgnoreCase)) return 3;
        if (channel.Contains("admin", StringComparison.OrdinalIgnoreCase)) return 4;
        return 0;
    }

    private static bool TryParseChatLine(string line, out string channel, out string name, out string message)
    {
        channel = string.Empty;
        name = string.Empty;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(line)) return false;

        // Examples:
        // 2026.03.17-12.00.00: [Global] PlayerName: Hello
        // [Global] PlayerName: Hello
        var m = Regex.Match(line, @"\[(?<chan>[^\]]+)\]\s*(?<name>[^:]+):\s*(?<msg>.+)$");
        if (!m.Success) return false;

        channel = m.Groups["chan"].Value.Trim();
        name = m.Groups["name"].Value.Trim();
        message = m.Groups["msg"].Value.Trim();
        return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(message);
    }

    private static bool TryParseLoginLine(string line, out string steamId, out string name, out bool isJoin)
    {
        steamId = string.Empty;
        name = string.Empty;
        isJoin = false;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var lower = line.ToLowerInvariant();
        if (!(lower.Contains("logged in") || lower.Contains("logged out") || lower.Contains("connected") || lower.Contains("disconnected")))
            return false;

        isJoin = lower.Contains("logged in") || lower.Contains("connected");

        // Try extract SteamID (17-digit)
        var m = Regex.Match(line, @"(\d{17})");
        if (m.Success) steamId = m.Groups[1].Value;

        // Try extract name between quotes
        var n = Regex.Match(line, "\"([^\"]+)\"");
        if (n.Success) name = n.Groups[1].Value;

        return !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(steamId);
    }

    private sealed record KillParsed(string KillerSteamId, string KillerName, string VictimSteamId, string VictimName, KillInfo Info);

    private static bool TryParseKillLine(string line, out KillParsed kill)
    {
        kill = new KillParsed(string.Empty, string.Empty, string.Empty, string.Empty, new KillInfo());
        if (string.IsNullOrWhiteSpace(line)) return false;

        var idx = line.IndexOf(": ", StringComparison.Ordinal);
        if (idx <= 0) return false;

        var tsRaw = line[..idx].Trim();
        var jsonRaw = line[(idx + 2)..].Trim();
        if (!jsonRaw.StartsWith("{", StringComparison.Ordinal)) return false;

        if (!DateTime.TryParseExact(tsRaw, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            timestamp = DateTime.UtcNow;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonRaw);
            var root = doc.RootElement;

            if (!root.TryGetProperty("Victim", out var victimEl)) return false;
            if (!root.TryGetProperty("Killer", out var killerEl)) return false;

            var victimSteam = GetString(victimEl, "UserId");
            var victimName = GetString(victimEl, "ProfileName");
            var killerSteam = GetString(killerEl, "UserId");
            var killerName = GetString(killerEl, "ProfileName");

            var weaponRaw = root.TryGetProperty("Weapon", out var wepEl) ? wepEl.GetString() ?? string.Empty : string.Empty;
            var weaponDamage = "";
            var weaponName = weaponRaw;
            var bracketIdx = weaponRaw.LastIndexOf('[');
            if (bracketIdx >= 0)
            {
                weaponName = weaponRaw[..bracketIdx].Trim();
                var end = weaponRaw.LastIndexOf(']');
                if (end > bracketIdx)
                {
                    weaponDamage = weaponRaw[(bracketIdx + 1)..end].Trim();
                }
            }

            var dist = ComputeDistance(killerEl, victimEl);
            var isSuicide = !string.IsNullOrWhiteSpace(killerSteam) && killerSteam == victimSteam;

            var info = new KillInfo
            {
                Timestamp = timestamp,
                Weapon = weaponName,
                WeaponDamage = weaponDamage,
                Distance = dist,
                IsSuicide = isSuicide
            };

            kill = new KillParsed(killerSteam, killerName, victimSteam, victimName, info);
            return !string.IsNullOrWhiteSpace(victimSteam) || !string.IsNullOrWhiteSpace(victimName);
        }
        catch
        {
            return false;
        }
    }

    private static string GetString(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var p) ? p.GetString() ?? string.Empty : string.Empty;
    }

    private static double GetDouble(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
            _ => 0
        };
    }

    private static double ComputeDistance(JsonElement killerEl, JsonElement victimEl)
    {
        if (!killerEl.TryGetProperty("ServerLocation", out var kLoc)) return 0;
        if (!victimEl.TryGetProperty("ServerLocation", out var vLoc)) return 0;
        if (!kLoc.TryGetProperty("X", out var kx) || !kLoc.TryGetProperty("Y", out var ky)) return 0;
        if (!vLoc.TryGetProperty("X", out var vx) || !vLoc.TryGetProperty("Y", out var vy)) return 0;

        var x1 = GetDouble(kx);
        var y1 = GetDouble(ky);
        var x2 = GetDouble(vx);
        var y2 = GetDouble(vy);

        var dx = x2 - x1;
        var dy = y2 - y1;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        return Math.Round(dist / 100.0, 2);
    }

    private static List<PlayerSnapshot> ParsePlayers(string response)
    {
        var list = new List<PlayerSnapshot>();
        if (string.IsNullOrWhiteSpace(response)) return list;

        var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var p = TryParseCsv(line) ?? TryParseColon(line);
            if (p.HasValue) list.Add(p.Value);
        }

        return list;
    }

    private static PlayerSnapshot? TryParseCsv(string line)
    {
        var parts = line.Split(',').Select(p => p.Trim()).ToArray();
        if (parts.Length < 4) return null;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;

        var name = parts.Length > 1 ? parts[1] : "";
        var steamId = parts.Length > 2 ? parts[2] : "";
        var ip = parts.Length > 3 ? parts[3] : "";

        return new PlayerSnapshot
        {
            Id = id,
            Name = name,
            SteamId = steamId,
            IpAddress = ip,
            Money = 0,
            Location = new Vector3(0, 0, 0)
        };
    }

    private static PlayerSnapshot? TryParseColon(string line)
    {
        if (!line.Contains("Name:", StringComparison.OrdinalIgnoreCase)) return null;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pairs = line.Split(',');
        foreach (var pair in pairs)
        {
            var kv = pair.Split(':', 2);
            if (kv.Length == 2)
                dict[kv[0].Trim()] = kv[1].Trim();
        }

        if (!int.TryParse(dict.GetValueOrDefault("ID") ?? "0", out var id))
            id = 0;

        return new PlayerSnapshot
        {
            Id = id,
            Name = dict.GetValueOrDefault("Name") ?? "",
            SteamId = dict.GetValueOrDefault("SteamID") ?? "",
            IpAddress = dict.GetValueOrDefault("IP") ?? "",
            Money = 0,
            Location = new Vector3(0, 0, 0)
        };
    }
}
