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
using System.Text;

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
    private readonly object _liveEventDedupLock = new();
    private readonly Dictionary<string, DateTimeOffset> _recentChatEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _recentCommandEvents = new(StringComparer.Ordinal);
    private NativeBridgeService? _nativeBridge;
    private string? _resolvedServerName;
    private string? _resolvedServerId;
    private readonly object _econLock = new();
    private DateTime _econLastRead = DateTime.MinValue;
    private Dictionary<string, EconomyEntry> _econBySteam = new(StringComparer.OrdinalIgnoreCase);
    private EconomySummary? _econSummary;
    private ServerEventPump? _eventPump;
    private CommandQueue? _cmdQueue;

    private sealed record PluginEntry(AssemblyLoadContext? Ctx, object Instance, Type Type, List<(string Method, string Path)> WebRoutes);
    private readonly Dictionary<string, PluginEntry> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private readonly object _pluginLock = new();

    private static readonly TimeSpan ChatDedupWindow = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan CommandDedupWindow = TimeSpan.FromMilliseconds(1200);

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
        _permissions = new PermissionService(
            Path.Combine(OxygenPaths.DataDir, "oxygen.users.json"),
            Path.Combine(OxygenPaths.DataDir, "oxygen.groups.json"),
            Path.Combine(OxygenPaths.ConfigsDir, "permissions.json"));
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
        _cmdQueue = new CommandQueue(Path.Combine(OxygenPaths.OxygenDir, "plugin-commands.txt"));
        _timers.Every(TimeSpan.FromSeconds(1), PollCommands);
        _nativeBridge = new NativeBridgeService(_log, _players, this);
        _nativeBridge.Start();
        if (!_nativeBridge.WaitUntilConnected(TimeSpan.FromSeconds(5)))
        {
            _log.Warn("[NativeBridge] Pipe not connected within startup window; continuing with deferred bridge.");
        }
        _commandsSvc.SetNativeCommandSender(command => _nativeBridge?.TrySendServerCommand(command) == true);
        ResolveServerIdentity();
        LoadAll();
        StartWatcher();
        _eventPump = new ServerEventPump(_log, _timers, _commandsSvc, _players, this);
        _eventPump.Start();
    }

    public object GetServerIdentity()
    {
        var (serverId, serverName, source) = ResolveServerIdentity();
        return new
        {
            serverId,
            serverName,
            source
        };
    }

    public (string ServerId, string ServerName, string Source) GetResolvedServerIdentity()
    {
        return ResolveServerIdentity();
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

    public object GetRuntimeLog()
    {
        return new
        {
            lines = _log.Snapshot(500)
        };
    }

    public object GetPlayers()
    {
        if (!_commandsSvc.Enabled && _players.List().Count == 0)
        {
            _players.UpdateFromSnapshot(ReadPlayerSnapshotFromDb());
        }

        var econ = GetEconomySnapshot();
        var quickAccessBySteam = ReadQuickAccessBySteam();
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
                FakeName = econ.Map.TryGetValue(p.SteamId, out var e7) && !string.IsNullOrWhiteSpace(e7.FakeName) ? e7.FakeName : p.FakeName,
                AuthorityName = econ.Map.TryGetValue(p.SteamId, out var e8) ? e8.AuthorityName : "",
                AuthorityIp = econ.Map.TryGetValue(p.SteamId, out var e9) ? e9.AuthorityIp : "",
                CreatedAt = econ.Map.TryGetValue(p.SteamId, out var e10) ? e10.CreationTime : "",
                HasUsedNewPlayerProtection = econ.Map.TryGetValue(p.SteamId, out var e11) && e11.HasUsedNewPlayerProtection,
                Gold = 0,
                Online = true,
                Source = p.LastNativeUpdate > DateTimeOffset.UtcNow.AddMinutes(-2)
                    ? "native"
                    : (_commandsSvc.Enabled ? "rcon" : "db"),
                NativePlayerId = p.NativePlayerId,
                ItemInHands = p.ItemInHands,
                QuickAccessItems = quickAccessBySteam.TryGetValue(p.SteamId, out var items) ? UpdatePlayerEconomyAndInventory(p, econ.Map.TryGetValue(p.SteamId, out var entry) ? entry : null, items) : UpdatePlayerEconomyAndInventory(p, econ.Map.TryGetValue(p.SteamId, out var fallbackEntry) ? fallbackEntry : null, Array.Empty<string>()),
                LastNativeUpdate = p.LastNativeUpdate == default ? "" : p.LastNativeUpdate.ToString("u"),
                Location = new { p.Location.X, p.Location.Y, p.Location.Z }
            })
            .ToList();
        return new { players };
    }

    private static IReadOnlyList<string> UpdatePlayerEconomyAndInventory(Oxygen.Csharp.API.PlayerBase player, EconomyEntry? entry, IReadOnlyList<string> quickAccessItems)
    {
        player.QuickAccessItems = quickAccessItems;
        player.FakeName = entry?.FakeName ?? player.FakeName;
        player.FamePoints = entry != null ? Convert.ToInt32(entry.FamePoints, CultureInfo.InvariantCulture) : player.FamePoints;
        player.Money = entry?.MoneyBalance ?? player.Money;
        player.IpAddress = entry?.AuthorityIp ?? player.IpAddress;
        player.Inventory.SetSnapshot(BuildSyntheticInventory(player));
        return quickAccessItems;
    }

    private static IReadOnlyList<Oxygen.Csharp.API.Item> BuildSyntheticInventory(Oxygen.Csharp.API.PlayerBase player)
    {
        var items = new List<Oxygen.Csharp.API.Item>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(player.ItemInHands) && seen.Add(player.ItemInHands))
        {
            items.Add(new Oxygen.Csharp.API.Item(player.ItemInHands, player));
        }

        foreach (var quickAccessItem in player.QuickAccessItems)
        {
            if (string.IsNullOrWhiteSpace(quickAccessItem) || !seen.Add(quickAccessItem))
                continue;

            items.Add(new Oxygen.Csharp.API.Item(quickAccessItem, player));
        }

        return items;
    }

    public object ExecuteCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return new { ok = false, error = "empty command" };
        var res = _commandsSvc.Execute(command);
        return new { ok = res.Success, response = res.Response, error = res.Error };
    }

    public object ExecutePluginCommand(string playerQuery, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new { ok = false, error = "empty message" };

        if (!(message.StartsWith("/") || message.StartsWith("!")))
            message = "/" + message.Trim();

        var player = ResolveApiPlayer(playerQuery);
        if (player == null)
            return new { ok = false, error = "player not found" };

        var handled = TryHandlePlayerCommand(player, message);
        return new
        {
            ok = handled,
            player = player.Name,
            steamId = player.SteamId,
            databaseId = player.DatabaseId,
            message
        };
    }

    public object BroadcastMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return new { ok = false, error = "empty message" };
        return ExecuteCommand($"broadcast {message}");
    }

    private Oxygen.Csharp.API.PlayerBase? ResolveApiPlayer(string playerQuery)
    {
        if (!string.IsNullOrWhiteSpace(playerQuery))
        {
            var existing = _players.Find(playerQuery);
            if (existing != null)
                return existing;
        }

        var livePlayers = _players.List()
            .Where(p => p.LastNativeUpdate > DateTimeOffset.UtcNow.AddMinutes(-5))
            .OrderByDescending(p => p.LastNativeUpdate)
            .ToList();

        if (!string.IsNullOrWhiteSpace(playerQuery))
        {
            var liveMatch = livePlayers.FirstOrDefault(p =>
                string.Equals(p.SteamId, playerQuery, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, playerQuery, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.FakeName, playerQuery, StringComparison.OrdinalIgnoreCase));

            if (liveMatch != null)
                return liveMatch;
        }
        else if (livePlayers.Count > 0)
        {
            return livePlayers[0];
        }

        var econ = GetEconomySnapshot().Map.Values
            .OrderByDescending(x => x.LastLogin)
            .ThenBy(x => x.Name)
            .ToList();

        EconomyEntry? match = null;
        if (!string.IsNullOrWhiteSpace(playerQuery))
        {
            match = econ.FirstOrDefault(x =>
                string.Equals(x.SteamId, playerQuery, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Name, playerQuery, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.FakeName, playerQuery, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.AuthorityName, playerQuery, StringComparison.OrdinalIgnoreCase));
        }

        match ??= econ.FirstOrDefault();
        if (match == null)
            return null;

        var player = _players.UpsertFromLogin(
            match.SteamId,
            !string.IsNullOrWhiteSpace(match.Name) ? match.Name : (!string.IsNullOrWhiteSpace(match.FakeName) ? match.FakeName : match.SteamId),
            match.PrisonerId,
            match.AuthorityIp);
        player.FakeName = match.FakeName;
        player.Money = match.MoneyBalance;
        player.FamePoints = Convert.ToInt32(match.FamePoints, CultureInfo.InvariantCulture);
        return player;
    }

    public object GetStatus()
    {
        var (serverId, serverName, source) = ResolveServerIdentity();
        return new
        {
            serverId,
            serverName,
            serverIdentitySource = source,
            rcon = _commandsSvc.Enabled,
            players = _players.List().Count,
            playerSource = _players.List().Any(p => p.LastNativeUpdate > DateTimeOffset.UtcNow.AddMinutes(-2))
                ? "native"
                : (_commandsSvc.Enabled ? "rcon" : "db")
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
                minX = _runtimeConfig.MapMinX,
                maxX = _runtimeConfig.MapMaxX,
                minY = _runtimeConfig.MapMinY,
                maxY = _runtimeConfig.MapMaxY,
                invertX = _runtimeConfig.MapInvertX,
                invertY = _runtimeConfig.MapInvertY
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

    private (string ServerId, string ServerName, string Source) ResolveServerIdentity()
    {
        if (!string.IsNullOrWhiteSpace(_resolvedServerId) && !string.IsNullOrWhiteSpace(_resolvedServerName))
        {
            return (_resolvedServerId!, _resolvedServerName!, "cached");
        }

        var configuredName = (_runtimeConfig.ServerName ?? string.Empty).Trim();
        var configuredId = (_runtimeConfig.ServerId ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            _resolvedServerName = configuredName;
            _resolvedServerId = !string.IsNullOrWhiteSpace(configuredId) ? configuredId : SlugifyServerId(configuredName);
            return (_resolvedServerId, _resolvedServerName, "runtime-config");
        }

        var iniPath = ResolveServerSettingsPath();
        if (!string.IsNullOrWhiteSpace(iniPath) && File.Exists(iniPath))
        {
            var detectedName = ReadIniValue(iniPath, "scum.ServerName");
            if (!string.IsNullOrWhiteSpace(detectedName))
            {
                _resolvedServerName = detectedName.Trim();
                _resolvedServerId = !string.IsNullOrWhiteSpace(configuredId) ? configuredId : SlugifyServerId(_resolvedServerName);
                return (_resolvedServerId, _resolvedServerName, "server-settings");
            }
        }

        _resolvedServerName = "SCUM Server";
        _resolvedServerId = !string.IsNullOrWhiteSpace(configuredId) ? configuredId : "scum-server";
        return (_resolvedServerId, _resolvedServerName, "fallback");
    }

    private string ResolveServerSettingsPath()
    {
        var candidates = new[]
        {
            Path.Combine(OxygenPaths.BaseDir, "..", "..", "..", "Saved", "Config", "WindowsServer", "ServerSettings.ini"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Saved", "Config", "WindowsServer", "ServerSettings.ini"),
            Path.Combine(OxygenPaths.BaseDir, "..", "..", "Saved", "Config", "WindowsServer", "ServerSettings.ini"),
            Path.Combine(OxygenPaths.BaseDir, "..", "..", "..", "Saved", "Config", "WindowsNoEditor", "ServerSettings.ini")
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
                return full;
        }

        return Path.GetFullPath(candidates[0]);
    }

    private static string? ReadIniValue(string path, string key)
    {
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#') || trimmed.StartsWith('['))
                continue;

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var currentKey = trimmed[..separatorIndex].Trim();
            if (!currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            return trimmed[(separatorIndex + 1)..].Trim();
        }

        return null;
    }

    private static string SlugifyServerId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "scum-server";

        var sb = new StringBuilder();
        var lastWasDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                lastWasDash = false;
                continue;
            }

            if (lastWasDash)
                continue;

            sb.Append('-');
            lastWasDash = true;
        }

        var slug = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "scum-server" : slug;
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

    private Dictionary<string, string[]> ReadQuickAccessBySteam()
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var dbPath = ResolveDatabasePath();
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                return result;

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT up.user_id,
       q.slot_index,
       COALESCE(e.class, q.item_entity_setup, '') AS item_name
FROM user_profile up
JOIN prisoner_entity pe ON pe.prisoner_id = up.prisoner_id
JOIN prisoner_inventory_quick_access_slot q ON q.prisoner_entity_id = pe.entity_id
LEFT JOIN entity e ON e.id = q.item_entity_id
WHERE up.user_id IS NOT NULL
ORDER BY up.user_id, q.slot_index";

            using var reader = cmd.ExecuteReader();
            var map = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var steamId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (string.IsNullOrWhiteSpace(steamId))
                    continue;

                var slotIndex = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                var rawName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                if (!map.TryGetValue(steamId, out var slots))
                {
                    slots = new SortedDictionary<int, string>();
                    map[steamId] = slots;
                }

                slots[slotIndex] = CleanEntityName(rawName);
            }

            foreach (var (steamId, slots) in map)
            {
                result[steamId] = slots.Values
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToArray();
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Players/QuickAccess] Failed to read DB: {ex.GetBaseException().Message}");
        }

        return result;
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
            UnregisterWebRoutes(entry);
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
            UnregisterWebRoutes(entry);
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
            var webRoutes = RegisterWebRoutes(plugin, pluginType);
            InvokePlugin(plugin, "OnLoad");
            InvokePlugin(plugin, "OnPluginInit");

            var key = Path.GetFileNameWithoutExtension(sourceFile);
            _loaded[key] = new PluginEntry(ctx, plugin, pluginType, webRoutes);
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
            _log.Info($"[CommandRegistry] Зарегистрирована команда '{cmdName}' из {pluginType.FullName}.{m.Name} perm='{perm}'");
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

    private List<(string Method, string Path)> RegisterWebRoutes(object plugin, Type pluginType)
    {
        var routes = new List<(string Method, string Path)>();
        var methods = pluginType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var method in methods)
        {
            var attr = method.GetCustomAttributes(inherit: true)
                .FirstOrDefault(a => a.GetType().FullName == "Oxygen.Csharp.Web.WebRouteAttribute");
            if (attr == null)
                continue;

            var path = GetAttrValue<string>(attr, "Path") ?? "/";
            var httpMethod = (GetAttrValue<string>(attr, "Method") ?? "POST").ToUpperInvariant();
            var requireAuth = GetAttrValue<bool>(attr, "RequireAuth");
            _web.Register(httpMethod, path, req => InvokeWebRoute(plugin, method, req), requireAuth);
            routes.Add((httpMethod, path));
            _log.Info($"[WebRoute] Registered {httpMethod} {path} auth={requireAuth} from {pluginType.FullName}.{method.Name}");
        }

        return routes;
    }

    private void UnregisterWebRoutes(PluginEntry entry)
    {
        foreach (var route in entry.WebRoutes)
        {
            _web.Unregister(route.Method, route.Path);
            _log.Info($"[WebRoute] Unregistered {route.Method} {route.Path} from {entry.Type.FullName}");
        }
    }

    private string InvokeWebRoute(object plugin, MethodInfo method, WebApiRequest req)
    {
        try
        {
            var parameters = method.GetParameters();
            object? result = parameters.Length switch
            {
                0 => method.Invoke(plugin, null),
                1 => method.Invoke(plugin, new object?[] { req.BodyText }),
                2 => method.Invoke(plugin, new object?[] { req.BodyText, req.Query }),
                3 => method.Invoke(plugin, new object?[] { req.BodyText, req.Query, req.Headers }),
                _ => throw new InvalidOperationException($"Unsupported WebRoute signature: {method.DeclaringType?.FullName}.{method.Name}")
            };

            return result?.ToString() ?? "{}";
        }
        catch (TargetInvocationException ex)
        {
            _log.Error($"[WebRoute] {method.DeclaringType?.FullName}.{method.Name} failed: {ex.InnerException?.GetBaseException().Message ?? ex.GetBaseException().Message}");
            return "{\"ok\":false,\"error\":\"route_failed\"}";
        }
        catch (Exception ex)
        {
            _log.Error($"[WebRoute] {method.DeclaringType?.FullName}.{method.Name} failed: {ex.GetBaseException().Message}");
            return "{\"ok\":false,\"error\":\"route_failed\"}";
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

    internal void DispatchPlayerRespawn(Oxygen.Csharp.API.PlayerBase player, Oxygen.Csharp.API.PlayerRespawnData data)
    {
        foreach (var entry in _loaded.Values)
            InvokePlugin(entry.Instance, "OnPlayerRespawn", player, data);
    }

    internal void DispatchPlayerOpenInventory(Oxygen.Csharp.API.PlayerBase player, string itemName, int ownerDbId, int entityId)
    {
        foreach (var entry in _loaded.Values)
            InvokePlugin(entry.Instance, "OnPlayerOpenInventory", player, itemName, ownerDbId, entityId);
    }

    internal void DispatchPlayerMeleeAttack(Oxygen.Csharp.API.PlayerBase player, string victimName)
    {
        foreach (var entry in _loaded.Values)
            InvokePlugin(entry.Instance, "OnPlayerMeleeAttack", player, victimName);
    }

    internal void DispatchPlayerMiniGameEnded(Oxygen.Csharp.API.PlayerBase player, string gameName, bool succeeded)
    {
        foreach (var entry in _loaded.Values)
            InvokePlugin(entry.Instance, "OnPlayerMiniGameEnded", player, gameName, succeeded);
    }

    internal void DispatchPlayerLockPickEnded(Oxygen.Csharp.API.PlayerBase player, Oxygen.Csharp.API.PlayerLockPickData data)
    {
        foreach (var entry in _loaded.Values)
            InvokePlugin(entry.Instance, "OnPlayerLockPickEnded", player, data);
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
        var deathData = new Oxygen.Csharp.API.DeathData
        {
            KillerId = killer?.SteamId ?? string.Empty,
            KillerName = killer?.Name ?? string.Empty,
            DeadType = info.IsSuicide ? "Suicide" : "Player",
            Reason = info.Weapon,
            Event = false,
            Distance = (float)info.Distance
        };
        foreach (var entry in _loaded.Values)
            InvokePlugin(entry.Instance, "OnPlayerDeath", victim, deathData);
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

    private bool IsDuplicateLiveEvent(
        Dictionary<string, DateTimeOffset> bucket,
        string key,
        TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var now = DateTimeOffset.UtcNow;
        lock (_liveEventDedupLock)
        {
            if (bucket.TryGetValue(key, out var lastSeen) && now - lastSeen <= window)
            {
                bucket[key] = now;
                return true;
            }

            bucket[key] = now;

            if (bucket.Count > 512)
            {
                var cutoff = now - TimeSpan.FromMinutes(3);
                foreach (var staleKey in bucket.Where(p => p.Value < cutoff).Select(p => p.Key).ToArray())
                {
                    bucket.Remove(staleKey);
                }
            }
        }

        return false;
    }

    internal bool DispatchPlayerChat(Oxygen.Csharp.API.PlayerBase player, string message, int chatType)
    {
        var userId = !string.IsNullOrWhiteSpace(player.SteamId) ? player.SteamId : $"name:{player.Name}";
        var chatKey = $"{userId}|{chatType}|{message}";
        if (IsDuplicateLiveEvent(_recentChatEvents, chatKey, ChatDedupWindow))
        {
            _log.Info($"[ChatPipeline] Дубликат подавлен: user='{userId}' type={chatType} message='{message}'");
            return true;
        }

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

    internal bool TryHandlePlayerCommand(Oxygen.Csharp.API.PlayerBase player, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (!(message.StartsWith("/") || message.StartsWith("!")))
            return false;

        var trimmed = message.TrimStart('/', '!');
        var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = split.Length > 0 ? split[0] : string.Empty;
        var argsPart = split.Length > 1 ? split[1] : string.Empty;
        var args = string.IsNullOrWhiteSpace(argsPart)
            ? Array.Empty<string>()
            : argsPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (string.IsNullOrWhiteSpace(cmd))
            return false;

        var userId = !string.IsNullOrWhiteSpace(player.SteamId) ? player.SteamId : $"name:{player.Name}";
        var commandKey = $"{userId}|{message.Trim()}";
        if (IsDuplicateLiveEvent(_recentCommandEvents, commandKey, CommandDedupWindow))
        {
            _log.Info($"[CommandPipeline] Дубликат подавлен: user='{userId}' raw='{message}'");
            return true;
        }

        _log.Info($"[CommandPipeline] Получена команда от {userId}: raw='{message}' cmd='{cmd}' args='{argsPart}'");
        var handled = _commands.TryExecute(cmd, args, _permissions, player, userId, _log);
        if (!handled)
        {
            _log.Warn($"[CommandPipeline] Обработчик не найден: '{cmd}' от {userId}");
        }
        return true;
    }

    internal bool DispatchPlayerTakeItemInHands(Oxygen.Csharp.API.PlayerBase player, string itemName)
    {
        player.ItemInHands = itemName ?? string.Empty;
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
            var method = ResolvePluginMethod(instance.GetType(), methodName, args);
            if (method == null) return null;
            return InvokeMethod(instance, method, args);
        }
        catch (Exception ex)
        {
            _log.Error($"[Plugin] {instance.GetType().FullName}.{methodName} failed: {ex}");
            return null;
        }
    }

    private static MethodInfo? ResolvePluginMethod(Type type, string methodName, object?[] args)
    {
        var methods = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .ToList();

        if (methods.Count == 0)
            return null;

        return methods
            .Where(m => m.GetParameters().Length == args.Length)
            .OrderByDescending(m => ScoreMethodMatch(m, args))
            .FirstOrDefault(m => ScoreMethodMatch(m, args) >= 0);
    }

    private static int ScoreMethodMatch(MethodInfo method, object?[] args)
    {
        var score = 0;
        var parameters = method.GetParameters();
        if (parameters.Length != args.Length)
            return -1;

        for (var i = 0; i < parameters.Length; i++)
        {
            var arg = args[i];
            var targetType = parameters[i].ParameterType;

            if (arg == null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    return -1;

                score += 1;
                continue;
            }

            if (targetType.IsInstanceOfType(arg))
            {
                score += 4;
                continue;
            }

            if (CanAdaptArgument(targetType, arg))
            {
                score += 2;
                continue;
            }

            return -1;
        }

        return score;
    }

    private static bool CanAdaptArgument(Type expectedType, object arg)
    {
        var targetType = Nullable.GetUnderlyingType(expectedType) ?? expectedType;

        if (targetType.FullName is "Oxygen.Csharp.API.PlayerBase" or
            "Oxygen.Csharp.API.KillInfo" or
            "Oxygen.Csharp.API.DeathData" or
            "Oxygen.Csharp.API.PlayerRespawnData" or
            "Oxygen.Csharp.API.PlayerLockPickData" or
            "Oxygen.Csharp.API.Vector3")
        {
            return true;
        }

        if (targetType.IsEnum)
            return true;

        if (targetType.IsPrimitive || targetType == typeof(decimal) || targetType == typeof(string))
            return true;

        return false;
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

        if (targetType.FullName == "Oxygen.Csharp.API.DeathData")
            return CloneDeathData(targetType, arg);

        if (targetType.FullName == "Oxygen.Csharp.API.PlayerRespawnData")
            return ClonePlainObject(targetType, arg, "SpawnLocationType", "SectorX", "SectorY");

        if (targetType.FullName == "Oxygen.Csharp.API.PlayerLockPickData")
            return ClonePlainObject(targetType, arg, "Target", "TargetId", "OwnerSteamId", "OwnerName", "Result", "FailCount");

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
        CopyMemberValue(source, clone, "FakeName");
        CopyMemberValue(source, clone, "Ping");
        CopyMemberValue(source, clone, "FamePoints");
        CopyMemberValue(source, clone, "Gold");
        CopyMemberValue(source, clone, "NativePlayerId");
        CopyMemberValue(source, clone, "ItemInHands");
        CopyMemberValue(source, clone, "QuickAccessItems");
        CopyMemberValue(source, clone, "LastNativeUpdate");

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

    private static object CloneDeathData(Type expectedType, object source)
    {
        var clone = Activator.CreateInstance(expectedType)!;

        if (source.GetType().FullName == "Oxygen.Csharp.API.KillInfo")
        {
            SetMemberValue(clone, "Reason", GetMemberValue(source, "Weapon") ?? string.Empty);
            SetMemberValue(clone, "Distance", Convert.ToSingle(GetMemberValue(source, "Distance") ?? 0f, CultureInfo.InvariantCulture));
            SetMemberValue(clone, "DeadType", Convert.ToBoolean(GetMemberValue(source, "IsSuicide") ?? false, CultureInfo.InvariantCulture) ? "Suicide" : "Player");
            SetMemberValue(clone, "Event", false);
            return clone;
        }

        return ClonePlainObject(expectedType, source, "KillerId", "KillerName", "DeadType", "Reason", "Event", "Distance");
    }

    private static object ClonePlainObject(Type expectedType, object source, params string[] members)
    {
        var clone = Activator.CreateInstance(expectedType)!;
        foreach (var member in members)
        {
            CopyMemberValue(source, clone, member);
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

            _log.Info($"[CommandQueue] user='{userId}' cmd='{cmd}' args='{argsPart}'");
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
