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
    private static readonly Regex ScumChatRegex = new(
        @"^(?:\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}:\s*)?'(?<player>[^']+)'\s*'(?<channel>[^:']+):\s*(?<msg>.*)'$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ScumLoginRegex = new(
        @"^(?:\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}:\s*)?'(?<player>[^']+)'\s+(?<state>logged in|logged out|connected|disconnected)\s+at:(?:\s+X=(?<x>-?\d+(?:\.\d+)?))?(?:\s+Y=(?<y>-?\d+(?:\.\d+)?))?(?:\s+Z=(?<z>-?\d+(?:\.\d+)?))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

    private sealed record ChatParsed(string SteamId, string Name, int DatabaseId, string IpAddress, string Channel, string Message);
    private sealed record LoginParsed(string SteamId, string Name, int DatabaseId, string IpAddress, bool IsJoin, Vector3 Location);

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
        var logsDir = System.IO.Path.Combine(OxygenPaths.BaseDir, "..", "..", "..", "Saved", "SaveFiles", "Logs");
        logsDir = System.IO.Path.GetFullPath(logsDir);
        _log.Info($"[EventPump] Logs dir: {logsDir}");

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
        try
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
        catch (Exception ex)
        {
            _log.Error($"[EventPump] PollPlayers failed: {ex}");
        }
    }

    private void PollLogs()
    {
        try
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
                    if (TryParseChatLine(line, out var chat))
                    {
                        var player = _players.UpsertFromLogin(chat.SteamId, chat.Name, chat.DatabaseId, chat.IpAddress);
                        player.LastNativeUpdate = DateTimeOffset.UtcNow;
                        var chatType = MapChatType(chat.Channel);

                        if (chat.Message.StartsWith("/") || chat.Message.StartsWith("!"))
                        {
                            _log.Info($"[EventPump] Chat parsed: steamId={chat.SteamId}, dbId={chat.DatabaseId}, name={chat.Name}, channel={chat.Channel}, message={chat.Message}");
                        }

                        _runtime.TryHandlePlayerCommand(player, chat.Message);

                        var allow = _runtime.DispatchPlayerChat(player, chat.Message, chatType);
                        if (!allow)
                        {
                            // Best-effort: no direct cancel from logs
                        }
                    }
                    else if (line.Contains("/:") || line.Contains("/"))
                    {
                        _log.Info($"[EventPump] Chat line ignored: {line}");
                    }
                }
            }

            if (_loginTailer != null)
            {
                foreach (var line in _loginTailer.ReadNewLines())
                {
                    if (TryParseLoginLine(line, out var login))
                    {
                        var player = _players.UpsertFromLogin(login.SteamId, login.Name, login.DatabaseId, login.IpAddress, login.Location);
                        player.LastNativeUpdate = DateTimeOffset.UtcNow;
                        _log.Info($"[EventPump] Login parsed: steamId={login.SteamId}, dbId={login.DatabaseId}, name={login.Name}, join={login.IsJoin}, ip={login.IpAddress}, loc={login.Location}");

                        if (login.IsJoin)
                        {
                            _runtime.DispatchPlayerConnected(player);
                        }
                        else
                        {
                            _players.RemoveFromLogin(login.SteamId, login.Name);
                            _runtime.DispatchPlayerDisconnected(player);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[EventPump] PollLogs failed: {ex}");
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

    private static bool TryParseChatLine(string line, out ChatParsed chat)
    {
        chat = new ChatParsed(string.Empty, string.Empty, 0, string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(line)) return false;

        var m = ScumChatRegex.Match(line);
        if (!m.Success) return false;

        if (!TryParsePlayerToken(
                m.Groups["player"].Value.Trim(),
                out var steamId,
                out var name,
                out var databaseId,
                out var ipAddress))
        {
            return false;
        }

        var channel = m.Groups["channel"].Value.Trim();
        var message = m.Groups["msg"].Value.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(message))
            return false;

        chat = new ChatParsed(steamId, name, databaseId, ipAddress, channel, message);
        return true;
    }

    private static bool TryParseLoginLine(string line, out LoginParsed login)
    {
        login = new LoginParsed(string.Empty, string.Empty, 0, string.Empty, false, default);
        if (string.IsNullOrWhiteSpace(line)) return false;

        var match = ScumLoginRegex.Match(line);
        if (!match.Success)
            return false;

        if (!TryParsePlayerToken(
                match.Groups["player"].Value.Trim(),
                out var steamId,
                out var name,
                out var databaseId,
                out var ipAddress))
        {
            return false;
        }

        var state = match.Groups["state"].Value.Trim();
        var isJoin = state.Contains("logged in", StringComparison.OrdinalIgnoreCase) ||
                     state.Contains("connected", StringComparison.OrdinalIgnoreCase);
        var location = new Vector3(
            ParseCoordinate(match.Groups["x"].Value),
            ParseCoordinate(match.Groups["y"].Value),
            ParseCoordinate(match.Groups["z"].Value));

        login = new LoginParsed(steamId, name, databaseId, ipAddress, isJoin, location);
        return true;
    }

    private static bool TryParsePlayerToken(string token, out string steamId, out string name, out int databaseId, out string ipAddress)
    {
        steamId = string.Empty;
        name = string.Empty;
        databaseId = 0;
        ipAddress = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        var work = token.Trim();
        var firstSpace = work.IndexOf(' ');
        if (firstSpace > 0)
        {
            var candidateIp = work[..firstSpace].Trim();
            if (System.Net.IPAddress.TryParse(candidateIp, out _))
            {
                ipAddress = candidateIp;
                work = work[(firstSpace + 1)..].Trim();
            }
        }

        var colonIndex = work.IndexOf(':');
        if (colonIndex <= 0)
            return false;

        steamId = work[..colonIndex].Trim();
        if (!Regex.IsMatch(steamId, @"^\d{17}$"))
            return false;

        var remainder = work[(colonIndex + 1)..].Trim();
        var tail = Regex.Match(remainder, @"^(?<name>.*?)(?:\((?<db>\d+)\))?$", RegexOptions.CultureInvariant);
        if (!tail.Success)
            return false;

        name = tail.Groups["name"].Value.Trim();
        if (tail.Groups["db"].Success)
        {
            _ = int.TryParse(tail.Groups["db"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out databaseId);
        }

        return !string.IsNullOrWhiteSpace(name);
    }

    private static double ParseCoordinate(string raw)
    {
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
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
