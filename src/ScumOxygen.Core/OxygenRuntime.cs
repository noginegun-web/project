using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Data.Sqlite;
using Oxygen.Csharp.API;
using Oxygen.Csharp.Core;

namespace ScumOxygen.Core;

[SupportedOSPlatform("windows")]
public sealed class OxygenRuntime
{
    private static int _resolverInstalled;
    private readonly Logger _log;
    private readonly PluginCompiler _compiler;
    private readonly CommandRegistry _commands;
    private readonly PermissionService _permissions;
    private readonly TimerService _timers;
    private readonly HookService _hooks;
    private readonly WebApiService _web;
    private readonly RuntimeConfig _runtimeConfig;
    private readonly CommandService _commandsSvc;
    private readonly PlayerRegistry _players;
    private readonly ChatHistory _chat = new(400);
    private readonly KillHistory _kills = new(400);
    private NativeBridgeService? _nativeBridge;
    private readonly object _econLock = new();
    private DateTime _econLastRead = DateTime.MinValue;
    private Dictionary<string, EconomyEntry> _econBySteam = new(StringComparer.OrdinalIgnoreCase);
    private EconomySummary? _econSummary;
    private ServerEventPump? _eventPump;
    private CommandQueue? _cmdQueue;

    private sealed record PluginEntry(AssemblyLoadContext? Ctx, object Instance, Type Type);
    private readonly Dictionary<string, PluginEntry> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private readonly object _pluginLock = new();

    public OxygenRuntime(Logger log)
    {
        _log = log;
        if (Interlocked.Exchange(ref _resolverInstalled, 1) == 0)
        {
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                if (string.Equals(name.Name, "ScumOxygen.Core", StringComparison.OrdinalIgnoreCase))
                {
                    return typeof(OxygenRuntime).Assembly;
                }
                return null;
            };
        }
        var errorLog = System.IO.Path.Combine(OxygenPaths.LogsDir, "OxygenError.log");
        _compiler = new PluginCompiler(log, errorLog);
        _commands = new CommandRegistry();
        _permissions = new PermissionService(Path.Combine(OxygenPaths.ConfigsDir, "permissions.json"));
        _timers = new TimerService();
        _hooks = new HookService();
        _web = new WebApiService();
        _runtimeConfig = RuntimeConfig.Load(Path.Combine(OxygenPaths.ConfigsDir, "runtime.json"));
        _commandsSvc = new CommandService(log);
        _players = new PlayerRegistry();
    }

    public CommandRegistry Commands => _commands;
    public PermissionService Permissions => _permissions;
    public TimerService Timers => _timers;
    public HookService Hooks => _hooks;
    public WebApiService Web => _web;
    public PlayerRegistry Players => _players;
    public RuntimeConfig Config => _runtimeConfig;
    public string Version => "1.0.0";

    public void Start()
    {
        OxygenPaths.Ensure();
        Oxygen.Csharp.API.Oxygen.Timers = _timers;
        Oxygen.Csharp.API.Oxygen.Permissions = _permissions;
        Oxygen.Csharp.API.Oxygen.Hooks = _hooks;
        Oxygen.Csharp.API.Oxygen.Web = _web;
        Oxygen.Csharp.API.Oxygen.FindPlayerImpl = _players.Find;
        Oxygen.Csharp.API.Oxygen.ListPlayersImpl = _players.List;

        Oxygen.Csharp.API.Server.CommandImpl = _commandsSvc.Execute;
        Oxygen.Csharp.API.Server.CommandAsyncImpl = cmd => _commandsSvc.ExecuteAsync(cmd);
        Oxygen.Csharp.API.Server.BroadcastImpl = msg => _commandsSvc.Execute($"broadcast {msg}");
        Oxygen.Csharp.API.Server.AnnounceImpl = msg => _commandsSvc.Execute($"announce {msg}");

        _web.SetWebRoot(OxygenPaths.WebDir);
        _web.ConfigureSecurity(_runtimeConfig.ApiKey, _runtimeConfig.AllowedIps);
        _web.ConfigureCors(_runtimeConfig.EnableCors);
        if (_runtimeConfig.EnableLocalWeb)
        {
            try
            {
                _web.Start(_runtimeConfig.LocalWebPrefix);
                _log.Info($"Web API started: {_runtimeConfig.LocalWebPrefix}");
            }
            catch (Exception ex)
            {
                _log.Error($"Web API failed to стартовать: {ex.GetBaseException().Message}");
            }
        }
        if (string.IsNullOrWhiteSpace(_runtimeConfig.ApiKey))
        {
            _log.Info("Web API: API key пустой — доступ разрешен только с localhost.");
        }
        _cmdQueue = new CommandQueue(Path.Combine(OxygenPaths.OxygenDir, "commands.txt"));
        _timers.Every(TimeSpan.FromSeconds(1), PollCommands);
        _nativeBridge = new NativeBridgeService(_log, _players, this);
        _nativeBridge.Start();
        _eventPump = new ServerEventPump(_log, _timers, _commandsSvc, _players, this);
        _eventPump.Start();
        LoadAll();
        StartWatcher();
    }

    public object ListPlugins()
    {
        var files = Directory.GetFiles(OxygenPaths.PluginsDir, "*.cs");
        var list = new List<string>();
        foreach (var f in files) list.Add(Path.GetFileName(f));
        return new { plugins = list };
    }

    public object GetPlugin(string name)
    {
        var path = Path.Combine(OxygenPaths.PluginsDir, name);
        if (!File.Exists(path)) return new { ok = false, code = "" };
        return new { ok = true, code = File.ReadAllText(path) };
    }

    public object SavePlugin(string name, string code)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { ok = false, error = "empty name" };
        var safeName = Path.GetFileName(name);
        if (!safeName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            safeName += ".cs";
        var path = Path.Combine(OxygenPaths.PluginsDir, safeName);
        try
        {
            var watcher = _watcher;
            if (watcher != null) watcher.EnableRaisingEvents = false;
            WritePluginSafe(path, code);
            ReloadPluginFile(path);
            if (watcher != null) watcher.EnableRaisingEvents = true;
            return new { ok = true };
        }
        catch (Exception ex)
        {
            _log.Error($"[Plugin] Save failed for {safeName}: {ex.GetBaseException().Message}");
            return new { ok = false, error = ex.GetBaseException().Message };
        }
    }

    private static void WritePluginSafe(string path, string code)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, code ?? string.Empty);
        File.Copy(tmp, path, true);
        File.Delete(tmp);
    }

    public object DeletePlugin(string name)
    {
        var safeName = Path.GetFileName(name);
        var path = Path.Combine(OxygenPaths.PluginsDir, safeName);
        if (File.Exists(path)) File.Delete(path);
        return new { ok = true };
    }

    public object ReloadPlugin(string name)
    {
        var safeName = Path.GetFileName(name);
        var path = Path.Combine(OxygenPaths.PluginsDir, safeName);
        if (File.Exists(path))
        {
            // Trigger immediate reload to avoid relying solely on FileSystemWatcher.
            ReloadPluginFile(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        return new { ok = true };
    }

    public object GetLogs()
    {
        var logPath = Path.Combine(OxygenPaths.LogsDir, "Oxygen.log");
        if (!File.Exists(logPath)) return new { text = "" };
        var text = File.ReadAllText(logPath);
        return new { text };
    }

    public object GetPlayers()
    {
        if (!_commandsSvc.Enabled && _players.List().Count == 0)
        {
            _players.UpdateFromSnapshot(ReadPlayerSnapshotFromDb());
        }

        var econ = GetEconomySnapshot();
        var players = _players.List()
            .Select(p => new
            {
                p.Name,
                p.SteamId,
                IpAddress = econ.Map.TryGetValue(p.SteamId, out var ipEntry) && !string.IsNullOrWhiteSpace(ipEntry.AuthorityIp) ? ipEntry.AuthorityIp : p.IpAddress,
                p.DatabaseId,
                Money = econ.Map.TryGetValue(p.SteamId, out var e) ? e.MoneyBalance : p.Money,
                FamePoints = econ.Map.TryGetValue(p.SteamId, out var e2) ? e2.FamePoints : 0,
                PlayTime = econ.Map.TryGetValue(p.SteamId, out var e3) ? e3.PlayTime : 0,
                LastLogin = econ.Map.TryGetValue(p.SteamId, out var e4) ? e4.LastLogin : "",
                LastLogout = econ.Map.TryGetValue(p.SteamId, out var e5) ? e5.LastLogout : "",
                PrisonerId = econ.Map.TryGetValue(p.SteamId, out var e6) ? e6.PrisonerId : 0,
                FakeName = econ.Map.TryGetValue(p.SteamId, out var e7) ? e7.FakeName : "",
                AuthorityName = econ.Map.TryGetValue(p.SteamId, out var e8) ? e8.AuthorityName : "",
                AuthorityIp = econ.Map.TryGetValue(p.SteamId, out var e9) ? e9.AuthorityIp : "",
                CreatedAt = econ.Map.TryGetValue(p.SteamId, out var e10) ? e10.CreationTime : "",
                HasUsedNewPlayerProtection = econ.Map.TryGetValue(p.SteamId, out var e11) && e11.HasUsedNewPlayerProtection,
                Gold = 0,
                Online = true,
                Source = _commandsSvc.Enabled ? "rcon" : "db",
                Location = new { p.Location.X, p.Location.Y, p.Location.Z }
            })
            .ToList();
        return new { players };
    }

    public object ExecuteCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return new { ok = false, error = "empty command" };
        var res = _commandsSvc.Execute(command);
        return new { ok = res.Success, response = res.Response, error = res.Error };
    }

    public object BroadcastMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return new { ok = false, error = "empty message" };
        return ExecuteCommand($"broadcast {message}");
    }

    public object GetStatus()
    {
        return new
        {
            rcon = _commandsSvc.Enabled,
            players = _players.List().Count,
            playerSource = _commandsSvc.Enabled ? "rcon" : "db"
        };
    }

    public object GetChat()
    {
        var items = _chat.Snapshot(200);
        return new { messages = items };
    }

    public object GetKills()
    {
        var items = _kills.Snapshot(200);
        return new { kills = items };
    }

    public object GetEconomy()
    {
        var econ = GetEconomySnapshot();
        if (econ.Summary == null)
            return new { topMoney = Array.Empty<object>(), topFame = Array.Empty<object>() };
        return new { topMoney = econ.Summary.TopMoney, topFame = econ.Summary.TopFame };
    }

    public object GetMapData()
    {
        List<object> players;
        var vehicles = new List<object>();
        var chests = new List<object>();
        var flags = new List<object>();

        try
        {
            var dbPath = ResolveDatabasePath();
            if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();

                players = BuildPlayerMapMarkers(conn);
                vehicles = ReadVehicleMarkers(conn);
                chests = ReadChestMarkers(conn);
                flags = ReadFlagMarkers(conn);
            }
            else
            {
                players = BuildPlayerMapMarkers();
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Map] Failed to read map entities: {ex.GetBaseException().Message}");
            players = BuildPlayerMapMarkers();
        }

        return new
        {
            bounds = new
            {
                minX = -760000,
                maxX = 760000,
                minY = -760000,
                maxY = 760000
            },
            background = new
            {
                url = _runtimeConfig.MapImageUrl,
                sourceUrl = _runtimeConfig.MapSourceUrl,
                name = "SCUM Island"
            },
            updatedAt = DateTimeOffset.UtcNow.ToString("u"),
            counts = new
            {
                players = players.Count,
                vehicles = vehicles.Count,
                chests = chests.Count,
                flags = flags.Count
            },
            layers = new
            {
                players,
                vehicles,
                chests,
                flags
            }
        };
    }

    public object SendChat(string channel, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return new { ok = false, error = "empty message" };
        // Best effort: use broadcast since direct chat channels may not be available server-side.
        return ExecuteCommand($"broadcast {message}");
    }

    public object GetSquads()
    {
        try
        {
            var dbPath = ResolveDatabasePath();
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                return new { squads = Array.Empty<object>() };

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT s.id,
       s.name,
       s.member_limit,
       s.score,
       sm.user_profile_id,
       sm.rank,
       up.name AS member_name
FROM squad s
LEFT JOIN squad_member sm ON sm.squad_id = s.id
LEFT JOIN user_profile up ON up.id = sm.user_profile_id
ORDER BY s.id, sm.rank";

            using var reader = cmd.ExecuteReader();
            var map = new Dictionary<int, SquadInfo>();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                var name = reader.IsDBNull(1) ? $"Squad {id}" : reader.GetString(1);
                var memberLimit = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                var score = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);

                if (!map.TryGetValue(id, out var squad))
                {
                    squad = new SquadInfo
                    {
                        Id = id,
                        Name = name,
                        MemberLimit = memberLimit,
                        Score = score
                    };
                    map[id] = squad;
                }

                if (!reader.IsDBNull(4))
                {
                    var profileId = reader.GetInt32(4);
                    var rank = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                    var memberName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                    squad.Members.Add(new { id = profileId, name = memberName, rank });
                }
            }

            var squads = map.Values
                .Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    memberLimit = s.MemberLimit,
                    score = s.Score,
                    members = s.Members
                })
                .ToList();

            return new { squads };
        }
        catch (Exception ex)
        {
            _log.Error($"[Squads] Failed to read DB: {ex.GetBaseException().Message}");
            return new { squads = Array.Empty<object>(), error = ex.GetBaseException().Message };
        }
    }

    private string ResolveDatabasePath()
    {
        if (!string.IsNullOrWhiteSpace(_runtimeConfig.DatabasePath))
            return _runtimeConfig.DatabasePath;
        var candidates = new[]
        {
            Path.Combine(OxygenPaths.BaseDir, "..", "..", "..", "Saved", "SaveFiles", "SCUM.db"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Saved", "SaveFiles", "SCUM.db"),
            Path.Combine(OxygenPaths.BaseDir, "..", "..", "Saved", "SaveFiles", "SCUM.db")
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
                return full;
        }

        return Path.GetFullPath(candidates[0]);
    }

    internal List<PlayerSnapshot> ReadPlayerSnapshotFromDb()
    {
        var snapshot = new List<PlayerSnapshot>();

        try
        {
            var dbPath = ResolveDatabasePath();
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                return snapshot;

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id,
       user_id,
       name,
       authority_ip,
       money_balance,
       last_login_time,
       last_logout_time
FROM user_profile";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var lastLogin = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                var lastLogout = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                if (!LooksOnline(lastLogin, lastLogout))
                    continue;

                snapshot.Add(new PlayerSnapshot
                {
                    Id = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    SteamId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    IpAddress = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Money = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    Location = new Vector3(0, 0, 0)
                });
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Players/DB] Failed to read live player snapshot: {ex.GetBaseException().Message}");
        }

        return snapshot;
    }

    private EconomySnapshot GetEconomySnapshot()
    {
        lock (_econLock)
        {
            if ((DateTime.UtcNow - _econLastRead) < TimeSpan.FromSeconds(20) && _econSummary != null)
            {
                return new EconomySnapshot(_econBySteam, _econSummary);
            }

            var dbPath = ResolveDatabasePath();
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                _econBySteam = new Dictionary<string, EconomyEntry>(StringComparer.OrdinalIgnoreCase);
                _econSummary = new EconomySummary();
                _econLastRead = DateTime.UtcNow;
                return new EconomySnapshot(_econBySteam, _econSummary);
            }

            try
            {
                var map = new Dictionary<string, EconomyEntry>(StringComparer.OrdinalIgnoreCase);
                var topMoney = new List<object>();
                var topFame = new List<object>();

                using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT user_id, name, fame_points, money_balance, play_time, last_login_time, last_logout_time, prisoner_id, authority_ip, authority_name, fake_name, creation_time, has_used_new_player_protection FROM user_profile";
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            var steamId = r.IsDBNull(0) ? string.Empty : r.GetString(0);
                            if (string.IsNullOrWhiteSpace(steamId)) continue;
                            var entry = new EconomyEntry
                            {
                                SteamId = steamId,
                                Name = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                                FamePoints = r.IsDBNull(2) ? 0 : r.GetDouble(2),
                                MoneyBalance = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                                PlayTime = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                                LastLogin = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                                LastLogout = r.IsDBNull(6) ? string.Empty : r.GetString(6),
                                PrisonerId = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                                AuthorityIp = r.IsDBNull(8) ? string.Empty : r.GetString(8),
                                AuthorityName = r.IsDBNull(9) ? string.Empty : r.GetString(9),
                                FakeName = r.IsDBNull(10) ? string.Empty : r.GetString(10),
                                CreationTime = r.IsDBNull(11) ? string.Empty : r.GetString(11),
                                HasUsedNewPlayerProtection = !r.IsDBNull(12) && r.GetBoolean(12)
                            };
                            map[steamId] = entry;
                        }
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT user_id, name, fame_points, money_balance, play_time, last_login_time FROM user_profile ORDER BY money_balance DESC LIMIT 50";
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            topMoney.Add(new
                            {
                                steamId = r.IsDBNull(0) ? "" : r.GetString(0),
                                name = r.IsDBNull(1) ? "" : r.GetString(1),
                                famePoints = r.IsDBNull(2) ? 0 : r.GetDouble(2),
                                moneyBalance = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                                playTime = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                                lastLogin = r.IsDBNull(5) ? "" : r.GetString(5)
                            });
                        }
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT user_id, name, fame_points, money_balance, play_time, last_login_time FROM user_profile ORDER BY fame_points DESC LIMIT 50";
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            topFame.Add(new
                            {
                                steamId = r.IsDBNull(0) ? "" : r.GetString(0),
                                name = r.IsDBNull(1) ? "" : r.GetString(1),
                                famePoints = r.IsDBNull(2) ? 0 : r.GetDouble(2),
                                moneyBalance = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                                playTime = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                                lastLogin = r.IsDBNull(5) ? "" : r.GetString(5)
                            });
                        }
                    }
                }

                _econBySteam = map;
                _econSummary = new EconomySummary
                {
                    TopMoney = topMoney,
                    TopFame = topFame
                };
                _econLastRead = DateTime.UtcNow;
                return new EconomySnapshot(_econBySteam, _econSummary);
            }
            catch (Exception ex)
            {
                _log.Error($"[Economy] Failed to read DB: {ex.GetBaseException().Message}");
                _econBySteam = new Dictionary<string, EconomyEntry>(StringComparer.OrdinalIgnoreCase);
                _econSummary = new EconomySummary();
                _econLastRead = DateTime.UtcNow;
                return new EconomySnapshot(_econBySteam, _econSummary);
            }
        }
    }

    private sealed class SquadInfo
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public int MemberLimit { get; init; }
        public double Score { get; init; }
        public List<object> Members { get; } = new();
    }

    private sealed class EconomyEntry
    {
        public string SteamId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public double FamePoints { get; init; }
        public int MoneyBalance { get; init; }
        public long PlayTime { get; init; }
        public string LastLogin { get; init; } = string.Empty;
        public string LastLogout { get; init; } = string.Empty;
        public int PrisonerId { get; init; }
        public string AuthorityIp { get; init; } = string.Empty;
        public string AuthorityName { get; init; } = string.Empty;
        public string FakeName { get; init; } = string.Empty;
        public string CreationTime { get; init; } = string.Empty;
        public bool HasUsedNewPlayerProtection { get; init; }
    }

    private sealed class EconomySummary
    {
        public List<object> TopMoney { get; init; } = new();
        public List<object> TopFame { get; init; } = new();
    }

    private List<object> BuildPlayerMapMarkers(SqliteConnection? conn = null)
    {
        var econ = GetEconomySnapshot();
        var rows = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var player in _players.List().Where(p => HasKnownLocation(p.Location)))
        {
            var key = !string.IsNullOrWhiteSpace(player.SteamId) ? player.SteamId : player.Name;
            rows[key] = new
            {
                id = player.SteamId,
                type = "player",
                name = player.Name,
                steamId = player.SteamId,
                x = player.Location.X,
                y = player.Location.Y,
                z = player.Location.Z,
                money = econ.Map.TryGetValue(player.SteamId, out var moneyEntry) ? moneyEntry.MoneyBalance : player.Money,
                famePoints = econ.Map.TryGetValue(player.SteamId, out var fameEntry) ? fameEntry.FamePoints : 0,
                source = "runtime",
                label = string.IsNullOrWhiteSpace(player.Name) ? player.SteamId : player.Name
            };
        }

        if (conn != null)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT up.user_id,
       up.name,
       up.fame_points,
       up.money_balance,
       up.last_login_time,
       up.last_logout_time,
       up.prisoner_id,
       e.location_x,
       e.location_y,
       e.location_z
FROM user_profile up
LEFT JOIN prisoner_entity pe ON pe.prisoner_id = up.prisoner_id
LEFT JOIN entity e ON e.id = pe.entity_id
WHERE up.user_id IS NOT NULL
  AND (e.location_x <> 0 OR e.location_y <> 0 OR e.location_z <> 0)";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var steamId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (string.IsNullOrWhiteSpace(steamId))
                    continue;

                var lastLogin = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                var lastLogout = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                if (!LooksOnline(lastLogin, lastLogout))
                    continue;

                if (rows.ContainsKey(steamId))
                    continue;

                var name = reader.IsDBNull(1) ? steamId : reader.GetString(1);
                rows[steamId] = new
                {
                    id = steamId,
                    type = "player",
                    name,
                    steamId,
                    x = reader.IsDBNull(7) ? 0.0 : reader.GetDouble(7),
                    y = reader.IsDBNull(8) ? 0.0 : reader.GetDouble(8),
                    z = reader.IsDBNull(9) ? 0.0 : reader.GetDouble(9),
                    money = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    famePoints = reader.IsDBNull(2) ? 0.0 : reader.GetDouble(2),
                    source = "db",
                    prisonerId = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    label = name
                };
            }
        }

        return rows.Values.ToList();
    }

    private static List<object> ReadVehicleMarkers(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT v.entity_id,
       e.class,
       e.location_x,
       e.location_y,
       e.location_z
FROM vehicle_entity v
JOIN entity e ON e.id = v.entity_id
WHERE e.location_x <> 0 OR e.location_y <> 0";

        using var reader = cmd.ExecuteReader();
        var rows = new List<object>();
        while (reader.Read())
        {
            rows.Add(new
            {
                id = reader.GetInt32(0),
                type = "vehicle",
                className = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                name = CleanEntityName(reader.IsDBNull(1) ? string.Empty : reader.GetString(1)),
                x = reader.IsDBNull(2) ? 0.0 : reader.GetDouble(2),
                y = reader.IsDBNull(3) ? 0.0 : reader.GetDouble(3),
                z = reader.IsDBNull(4) ? 0.0 : reader.GetDouble(4),
                label = CleanEntityName(reader.IsDBNull(1) ? string.Empty : reader.GetString(1))
            });
        }
        return rows;
    }

    private static List<object> ReadChestMarkers(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT e.id,
       e.class,
       e.location_x,
       e.location_y,
       e.location_z
FROM entity e
WHERE (
        lower(e.class) LIKE '%chest%'
     OR lower(e.class) LIKE '%crate%'
     OR lower(e.class) LIKE '%locker%'
     OR lower(e.class) LIKE '%wardrobe%'
     OR lower(e.class) LIKE '%_item_container_%'
      )
  AND (e.location_x <> 0 OR e.location_y <> 0)
ORDER BY e.id";

        using var reader = cmd.ExecuteReader();
        var rows = new List<object>();
        while (reader.Read())
        {
            var rawClass = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            rows.Add(new
            {
                id = reader.GetInt32(0),
                type = "chest",
                className = rawClass,
                name = CleanEntityName(rawClass),
                x = reader.IsDBNull(2) ? 0.0 : reader.GetDouble(2),
                y = reader.IsDBNull(3) ? 0.0 : reader.GetDouble(3),
                z = reader.IsDBNull(4) ? 0.0 : reader.GetDouble(4),
                label = CleanEntityName(rawClass)
            });
        }
        return rows;
    }

    private static List<object> ReadFlagMarkers(SqliteConnection conn)
    {
        var rows = new List<object>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT be.element_id,
       COALESCE(b.name, be.asset, 'Flag') AS name,
       be.asset,
       be.location_x,
       be.location_y,
       be.location_z
FROM base_element_flag f
JOIN base_element be ON be.element_id = f.element_id
LEFT JOIN base b ON b.id = be.base_id";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rawName = reader.IsDBNull(1) ? "Flag" : reader.GetString(1);
                rows.Add(new
                {
                    id = reader.GetInt32(0),
                    type = "flag",
                    name = CleanEntityName(rawName),
                    asset = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    x = reader.IsDBNull(3) ? 0.0 : reader.GetDouble(3),
                    y = reader.IsDBNull(4) ? 0.0 : reader.GetDouble(4),
                    z = reader.IsDBNull(5) ? 0.0 : reader.GetDouble(5),
                    label = CleanEntityName(rawName)
                });
            }
        }

        if (rows.Count > 0)
            return rows;

        using var fallback = conn.CreateCommand();
        fallback.CommandText = @"
SELECT be.element_id,
       COALESCE(b.name, be.asset, 'Flag') AS name,
       be.asset,
       be.location_x,
       be.location_y,
       be.location_z
FROM base_element be
LEFT JOIN base b ON b.id = be.base_id
WHERE lower(be.asset) LIKE '%flag%'";

        using var fallbackReader = fallback.ExecuteReader();
        while (fallbackReader.Read())
        {
            var rawName = fallbackReader.IsDBNull(1) ? "Flag" : fallbackReader.GetString(1);
            rows.Add(new
            {
                id = fallbackReader.GetInt32(0),
                type = "flag",
                name = CleanEntityName(rawName),
                asset = fallbackReader.IsDBNull(2) ? string.Empty : fallbackReader.GetString(2),
                x = fallbackReader.IsDBNull(3) ? 0.0 : fallbackReader.GetDouble(3),
                y = fallbackReader.IsDBNull(4) ? 0.0 : fallbackReader.GetDouble(4),
                z = fallbackReader.IsDBNull(5) ? 0.0 : fallbackReader.GetDouble(5),
                label = CleanEntityName(rawName)
            });
        }

        return rows;
    }

    private static bool HasKnownLocation(Vector3 location)
    {
        return Math.Abs(location.X) > 0.001 || Math.Abs(location.Y) > 0.001 || Math.Abs(location.Z) > 0.001;
    }

    private static string CleanEntityName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown";

        return raw
            .Replace("_ES", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_Item_Container", " Container", StringComparison.OrdinalIgnoreCase)
            .Replace("_", " ", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private readonly record struct EconomySnapshot(
        Dictionary<string, EconomyEntry> Map,
        EconomySummary? Summary);

    public void Stop()
    {
        _nativeBridge?.Stop();
        _eventPump?.Stop();
        foreach (var entry in _loaded.Values)
        {
            InvokePlugin(entry.Instance, "OnPluginUnload");
            InvokePlugin(entry.Instance, "OnUnload");
            if (entry.Ctx != null && entry.Ctx.IsCollectible)
                entry.Ctx.Unload();
        }
        _loaded.Clear();
    }

    private void LoadAll()
    {
        foreach (var file in Directory.GetFiles(OxygenPaths.PluginsDir, "*.cs"))
        {
            LoadPlugin(file);
        }
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(OxygenPaths.PluginsDir, "*.cs");
        _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime;
        _watcher.Changed += (_, e) => ReloadPluginFile(e.FullPath);
        _watcher.Created += (_, e) => LoadPlugin(e.FullPath);
        _watcher.Deleted += (_, e) => UnloadPlugin(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    private void ReloadPluginFile(string sourceFile)
    {
        lock (_pluginLock)
        {
            try
            {
                UnloadPlugin(sourceFile);
                LoadPlugin(sourceFile);
            }
            catch (Exception ex)
            {
                _log.Error($"[Plugin] Reload failed for {sourceFile}: {ex.GetBaseException().Message}");
            }
        }
    }

    private void UnloadPlugin(string sourceFile)
    {
        lock (_pluginLock)
        {
        var key = Path.GetFileNameWithoutExtension(sourceFile);
        if (_loaded.TryGetValue(key, out var entry))
        {
            InvokePlugin(entry.Instance, "OnPluginUnload");
            InvokePlugin(entry.Instance, "OnUnload");
            if (entry.Ctx != null && entry.Ctx.IsCollectible)
                entry.Ctx.Unload();
            _loaded.Remove(key);
            _log.Info($"Unloaded plugin: {key}");
        }
        }
    }

    private void LoadPlugin(string sourceFile)
    {
        lock (_pluginLock)
        {
        var outputPath = _compiler.Compile(sourceFile, OxygenPaths.CacheDir);
        if (outputPath == null) return;

        try
        {
            var ctx = new PluginLoadContext(outputPath, typeof(OxygenRuntime).Assembly.Location);
            var asm = ctx.LoadFromAssemblyPath(outputPath);

            var pluginType = asm.GetTypes().FirstOrDefault(IsOxygenPluginType);
            if (pluginType == null)
            {
                _log.Error($"No OxygenPlugin found in {sourceFile}");
                ctx.Unload();
                return;
            }

            var plugin = Activator.CreateInstance(pluginType)!;
            // Ensure API singletons are set even if load context quirks occur
            Oxygen.Csharp.API.Oxygen.Timers = _timers;
            Oxygen.Csharp.API.Oxygen.Permissions = _permissions;
            Oxygen.Csharp.API.Oxygen.Hooks = _hooks;
            Oxygen.Csharp.API.Oxygen.Web = _web;
            _log.Info($"API Timers null? {Oxygen.Csharp.API.Oxygen.Timers is null}");
            RegisterCommands(plugin, pluginType);
            RegisterHooks(plugin, pluginType);
            InvokePlugin(plugin, "OnLoad");
            InvokePlugin(plugin, "OnPluginInit");

            var key = Path.GetFileNameWithoutExtension(sourceFile);
            _loaded[key] = new PluginEntry(ctx, plugin, pluginType);
            _log.Info($"Loaded plugin: {pluginType.FullName}");
        }
        catch (Exception ex)
        {
            _log.Error($"[Plugin] Load failed for {sourceFile}: {ex.GetBaseException().Message}");
        }
        }
    }

    private void RegisterCommands(object plugin, Type pluginType)
    {
        var methods = pluginType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var m in methods)
        {
            var cmd = GetAttributeByFullName(m, "Oxygen.Csharp.Core.CommandAttribute");
            if (cmd == null) continue;

            var permAttr = GetAttributeByFullName(m, "Oxygen.Csharp.Core.PermissionAttribute");
            var perm = GetAttrValue<string>(permAttr, "Permission") ?? string.Empty;
            var cmdName = GetAttrValue<string>(cmd, "Name") ?? string.Empty;
            var cmdDesc = GetAttrValue<string>(cmd, "Description") ?? string.Empty;
            var handler = new CommandRegistry.CommandHandler(
                cmdName,
                cmdDesc,
                perm,
                (player, args) =>
                {
                    try
                    {
                        var parameters = m.GetParameters();
                        if (parameters.Length == 1)
                        {
                            InvokeMethod(plugin, m, args);
                        }
                        else if (parameters.Length == 2 &&
                                 parameters[0].ParameterType.FullName == "Oxygen.Csharp.API.PlayerBase")
                        {
                            InvokeMethod(plugin, m, player ?? CreateConsolePlayer(), args);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[Plugin] Command {cmdName} failed: {ex.GetBaseException().Message}");
                    }
                });

            _commands.Register(handler);
        }
    }

    private void RegisterHooks(object plugin, Type pluginType)
    {
        var methods = pluginType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var m in methods)
        {
            var hook = GetAttributeByFullName(m, "Oxygen.Csharp.Core.HookAttribute");
            if (hook == null) continue;

            var eventName = GetAttrValue<string>(hook, "EventName") ?? string.Empty;
            Action<object[]> wrapper = args =>
            {
                try { InvokeMethod(plugin, m, args); }
                catch (Exception ex)
                {
                    _log.Error($"[Plugin] Hook {eventName} failed: {ex.GetBaseException().Message}");
                }
            };
            if (!string.IsNullOrWhiteSpace(eventName))
            {
                _hooks.Register(eventName, wrapper);
            }
        }
    }

    private static object? GetAttributeByFullName(MemberInfo member, string fullName)
    {
        return member.GetCustomAttributes(inherit: true)
            .FirstOrDefault(a => a.GetType().FullName == fullName);
    }

    private static T? GetAttrValue<T>(object? attr, string property)
    {
        if (attr == null) return default;
        var prop = attr.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null) return default;
        return prop.GetValue(attr) is T value ? value : default;
    }

    private static bool IsOxygenPluginType(Type type)
    {
        if (type.IsAbstract) return false;
        var cur = type;
        while (cur != null)
        {
            var baseType = cur.BaseType;
            if (baseType == null) break;
            if (baseType.FullName == "Oxygen.Csharp.Core.OxygenPlugin") return true;
            cur = baseType;
        }
        return false;
    }

    internal void DispatchPlayerConnected(Oxygen.Csharp.API.PlayerBase player)
    {
        foreach (var entry in _loaded.Values)
            InvokePlugin(entry.Instance, "OnPlayerConnected", player);
    }

    internal void DispatchPlayerDisconnected(Oxygen.Csharp.API.PlayerBase player)
    {
        foreach (var entry in _loaded.Values)
            InvokePlugin(entry.Instance, "OnPlayerDisconnected", player);
    }

    internal void DispatchPlayerRespawned(Oxygen.Csharp.API.PlayerBase player)
    {
        foreach (var entry in _loaded.Values)
            InvokePlugin(entry.Instance, "OnPlayerRespawned", player);
    }

    internal void DispatchPlayerDeath(Oxygen.Csharp.API.PlayerBase victim, Oxygen.Csharp.API.PlayerBase? killer, Oxygen.Csharp.API.KillInfo info)
    {
        _kills.Add(new KillMessage(
            info.Timestamp == default ? DateTimeOffset.UtcNow : new DateTimeOffset(info.Timestamp, TimeSpan.Zero),
            killer?.Name ?? "Unknown",
            killer?.SteamId ?? string.Empty,
            victim.Name ?? "Unknown",
            victim.SteamId ?? string.Empty,
            info.Weapon,
            info.WeaponDamage,
            info.Distance,
            info.IsSuicide));
        foreach (var entry in _loaded.Values)
            InvokePlugin(entry.Instance, "OnPlayerDeath", victim, killer, info);
        _hooks.Emit("OnPlayerDeath", victim, killer, info);
        _hooks.Emit("player_death", victim, killer, info);
    }

    internal void DispatchPlayerKill(Oxygen.Csharp.API.PlayerBase killer, Oxygen.Csharp.API.PlayerBase victim, Oxygen.Csharp.API.KillInfo info)
    {
        foreach (var entry in _loaded.Values)
            InvokePlugin(entry.Instance, "OnPlayerKill", killer, victim, info);
        _hooks.Emit("OnPlayerKill", killer, victim, info);
        _hooks.Emit("player_kill", killer, victim, info);
    }

    internal bool DispatchPlayerChat(Oxygen.Csharp.API.PlayerBase player, string message, int chatType)
    {
        _chat.Add(new ChatMessage(
            DateTimeOffset.UtcNow,
            ChatTypeName(chatType),
            player.Name ?? "Unknown",
            message));
        var allow = true;
        foreach (var entry in _loaded.Values)
        {
            var res = InvokePlugin(entry.Instance, "OnPlayerChat", player, message, chatType);
            if (res is bool b && !b) allow = false;
        }
        return allow;
    }

    internal bool DispatchPlayerTakeItemInHands(Oxygen.Csharp.API.PlayerBase player, string itemName)
    {
        var allow = true;
        foreach (var entry in _loaded.Values)
        {
            var res = InvokePlugin(entry.Instance, "OnPlayerTakeItemInHands", player, itemName);
            if (res is bool b && !b) allow = false;
        }
        return allow;
    }

    private object? InvokePlugin(object instance, string methodName, params object?[] args)
    {
        try
        {
            var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) return null;
            return InvokeMethod(instance, method, args);
        }
        catch (Exception ex)
        {
            _log.Error($"[Plugin] {instance.GetType().FullName}.{methodName} failed: {ex}");
            return null;
        }
    }

    private static object? InvokeMethod(object instance, MethodInfo method, params object?[] args)
    {
        var adaptedArgs = AdaptArguments(method, args);
        return method.Invoke(instance, adaptedArgs);
    }

    private static object?[] AdaptArguments(MethodInfo method, object?[] args)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0) return Array.Empty<object?>();

        var adapted = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var arg = i < args.Length ? args[i] : null;
            adapted[i] = AdaptArgument(parameters[i].ParameterType, arg);
        }

        return adapted;
    }

    private static object? AdaptArgument(Type expectedType, object? arg)
    {
        if (arg == null) return null;
        if (expectedType.IsInstanceOfType(arg)) return arg;

        var targetType = Nullable.GetUnderlyingType(expectedType) ?? expectedType;

        if (targetType.FullName == "Oxygen.Csharp.API.PlayerBase")
            return ClonePlayerBase(targetType, arg);

        if (targetType.FullName == "Oxygen.Csharp.API.KillInfo")
            return CloneKillInfo(targetType, arg);

        if (targetType.FullName == "Oxygen.Csharp.API.Vector3")
            return CloneVector3(targetType, arg);

        if (targetType.IsEnum)
        {
            if (arg is string enumText)
                return Enum.Parse(targetType, enumText, ignoreCase: true);
            return Enum.ToObject(targetType, Convert.ToInt32(arg, CultureInfo.InvariantCulture));
        }

        if (targetType.IsPrimitive || targetType == typeof(decimal) || targetType == typeof(string))
        {
            return Convert.ChangeType(arg, targetType, CultureInfo.InvariantCulture);
        }

        return arg;
    }

    private static object ClonePlayerBase(Type expectedType, object source)
    {
        var clone = Activator.CreateInstance(expectedType)!;
        CopyMemberValue(source, clone, "SteamId");
        CopyMemberValue(source, clone, "Name");
        CopyMemberValue(source, clone, "IpAddress");
        CopyMemberValue(source, clone, "DatabaseId");
        CopyMemberValue(source, clone, "Money");

        var sourceLocation = GetMemberValue(source, "Location");
        if (sourceLocation != null)
        {
            var targetLocationType = expectedType.Assembly.GetType("Oxygen.Csharp.API.Vector3");
            if (targetLocationType != null)
            {
                SetMemberValue(clone, "Location", CloneVector3(targetLocationType, sourceLocation));
            }
        }

        return clone;
    }

    private static object CloneKillInfo(Type expectedType, object source)
    {
        var clone = Activator.CreateInstance(expectedType)!;
        CopyMemberValue(source, clone, "Timestamp");
        CopyMemberValue(source, clone, "Weapon");
        CopyMemberValue(source, clone, "WeaponDamage");
        CopyMemberValue(source, clone, "Distance");
        CopyMemberValue(source, clone, "IsSuicide");
        return clone;
    }

    private static object CloneVector3(Type expectedType, object source)
    {
        var x = Convert.ToDouble(GetMemberValue(source, "X") ?? 0d, CultureInfo.InvariantCulture);
        var y = Convert.ToDouble(GetMemberValue(source, "Y") ?? 0d, CultureInfo.InvariantCulture);
        var z = Convert.ToDouble(GetMemberValue(source, "Z") ?? 0d, CultureInfo.InvariantCulture);
        return Activator.CreateInstance(expectedType, x, y, z)!;
    }

    private static void CopyMemberValue(object source, object target, string memberName)
    {
        var value = GetMemberValue(source, memberName);
        if (value != null)
        {
            SetMemberValue(target, memberName, value);
        }
    }

    private static object? GetMemberValue(object instance, string memberName)
    {
        var type = instance.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property != null)
            return property.GetValue(instance);

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? type.GetField($"<{memberName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(instance);
    }

    private static void SetMemberValue(object instance, string memberName, object? value)
    {
        var type = instance.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var setter = property?.SetMethod ?? property?.GetSetMethod(true);
        if (setter != null)
        {
            setter.Invoke(instance, new[] { value });
            return;
        }

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? type.GetField($"<{memberName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(instance, value);
    }

    private static string ChatTypeName(int chatType)
    {
        return chatType switch
        {
            1 => "Local",
            2 => "Global",
            3 => "Squad",
            4 => "Admin",
            _ => "Global"
        };
    }

    private sealed record ChatMessage(DateTimeOffset Time, string Channel, string Name, string Message);
    private sealed record KillMessage(DateTimeOffset Time, string Killer, string KillerSteamId, string Victim, string VictimSteamId, string Weapon, string WeaponDamage, double Distance, bool IsSuicide);

    private sealed class ChatHistory
    {
        private readonly int _capacity;
        private readonly LinkedList<ChatMessage> _messages = new();
        private readonly object _lock = new();

        public ChatHistory(int capacity)
        {
            _capacity = Math.Max(50, capacity);
        }

        public void Add(ChatMessage msg)
        {
            lock (_lock)
            {
                _messages.AddFirst(msg);
                while (_messages.Count > _capacity)
                {
                    _messages.RemoveLast();
                }
            }
        }

        public List<object> Snapshot(int limit)
        {
            var take = Math.Max(1, limit);
            var list = new List<object>();
            lock (_lock)
            {
                foreach (var msg in _messages)
                {
                    list.Add(new
                    {
                        time = msg.Time.ToString("u"),
                        channel = msg.Channel,
                        name = msg.Name,
                        message = msg.Message
                    });
                    if (list.Count >= take) break;
                }
            }
            list.Reverse();
            return list;
        }
    }

    private sealed class KillHistory
    {
        private readonly int _capacity;
        private readonly LinkedList<KillMessage> _messages = new();
        private readonly object _lock = new();

        public KillHistory(int capacity)
        {
            _capacity = Math.Max(50, capacity);
        }

        public void Add(KillMessage msg)
        {
            lock (_lock)
            {
                _messages.AddFirst(msg);
                while (_messages.Count > _capacity)
                {
                    _messages.RemoveLast();
                }
            }
        }

        public List<object> Snapshot(int limit)
        {
            var take = Math.Max(1, limit);
            var list = new List<object>();
            lock (_lock)
            {
                foreach (var msg in _messages)
                {
                    list.Add(new
                    {
                        time = msg.Time.ToString("u"),
                        killer = msg.Killer,
                        killerSteamId = msg.KillerSteamId,
                        victim = msg.Victim,
                        victimSteamId = msg.VictimSteamId,
                        weapon = msg.Weapon,
                        weaponDamage = msg.WeaponDamage,
                        distance = msg.Distance,
                        isSuicide = msg.IsSuicide
                    });
                    if (list.Count >= take) break;
                }
            }
            list.Reverse();
            return list;
        }
    }

    private void PollCommands()
    {
        if (_cmdQueue == null) return;
        var lines = _cmdQueue.ReadNewLines();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            // Format: userId:command arg1 arg2
            var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmdPart = split[0];
            var argsPart = split.Length > 1 ? split[1] : string.Empty;

            var userSplit = cmdPart.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
            var userId = userSplit.Length == 2 ? userSplit[0] : "console";
            var cmd = userSplit.Length == 2 ? userSplit[1] : cmdPart;
            var args = string.IsNullOrWhiteSpace(argsPart) ? Array.Empty<string>() : argsPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            _commands.TryExecute(cmd, args, _permissions, ResolveCommandPlayer(userId), userId, _log);
        }
    }

    private PlayerBase ResolveCommandPlayer(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Equals("console", StringComparison.OrdinalIgnoreCase))
            return CreateConsolePlayer();

        return _players.Find(userId) ?? new PlayerBase
        {
            SteamId = userId,
            Name = userId
        };
    }

    private static PlayerBase CreateConsolePlayer()
    {
        return new PlayerBase
        {
            SteamId = "console",
            Name = "Console"
        };
    }

    private static bool LooksOnline(string loginRaw, string logoutRaw)
    {
        var hasLogin = TryParseUtc(loginRaw, out var login);
        var hasLogout = TryParseUtc(logoutRaw, out var logout);

        if (!hasLogin)
            return false;
        if (!hasLogout)
            return true;

        return login > logout;
    }

    private static bool TryParseUtc(string raw, out DateTime value)
    {
        if (DateTime.TryParse(raw, out value))
        {
            value = value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();
            return true;
        }

        value = default;
        return false;
    }
}
