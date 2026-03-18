using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScumOxygen.Core;

public sealed class ApiController
{
    private readonly Logger _log;
    private readonly OxygenRuntime _runtime;

    public ApiController(Logger log, OxygenRuntime runtime)
    {
        _log = log;
        _runtime = runtime;

        _runtime.Web.Register("GET", "/api/status", _ => GetStatus());
        _runtime.Web.Register("GET", "/api/servers", _ => GetServers());
        _runtime.Web.Register("GET", "/api/players", _ => GetPlayers());
        _runtime.Web.Register("POST", "/api/command", ExecuteCommand);
        _runtime.Web.Register("POST", "/api/plugin-command", ExecutePluginCommand);
        _runtime.Web.Register("POST", "/api/broadcast", Broadcast);
        _runtime.Web.Register("GET", "/api/chat", _ => GetChat());
        _runtime.Web.Register("POST", "/api/chat", SendChat);
        _runtime.Web.Register("GET", "/api/squads", _ => GetSquads());
        _runtime.Web.Register("GET", "/api/kills", _ => GetKills());
        _runtime.Web.Register("GET", "/api/economy", _ => GetEconomy());
        _runtime.Web.Register("GET", "/api/map", _ => GetMap());

        _runtime.Web.Register("GET", "/api/plugins", _ => ListPlugins());
        _runtime.Web.Register("GET", "/api/plugin", GetPlugin);
        _runtime.Web.Register("POST", "/api/plugin", SavePlugin);
        _runtime.Web.Register("DELETE", "/api/plugin", DeletePlugin);
        _runtime.Web.Register("GET", "/api/reload", ReloadPlugin);
        _runtime.Web.Register("POST", "/api/reload", ReloadPlugin);
        _runtime.Web.Register("GET", "/api/logs", _ => ReadLogs());
        _runtime.Web.Register("GET", "/api/runtime-log", _ => ReadRuntimeLog());
    }

    private string GetServers()
    {
        var token = _runtime.Config.ApiKey;
        var identity = _runtime.GetResolvedServerIdentity();
        var servers = new[]
        {
            new
            {
                id = identity.ServerId,
                name = identity.ServerName,
                version = _runtime.Version,
                token
            }
        };
        return JsonSerializer.Serialize(new { servers });
    }

    private string GetStatus()
    {
        var status = _runtime.GetStatus();
        return JsonSerializer.Serialize(status);
    }

    private string GetPlayers()
    {
        var players = _runtime.GetPlayers();
        return JsonSerializer.Serialize(players);
    }

    private string ExecuteCommand(WebApiRequest req)
    {
        var data = ReadJsonBody(req);
        var command = data.TryGetValue("command", out var cmd) ? cmd : string.Empty;
        var res = _runtime.ExecuteCommand(command);
        return JsonSerializer.Serialize(res);
    }

    private string Broadcast(WebApiRequest req)
    {
        var data = ReadJsonBody(req);
        var message = data.TryGetValue("message", out var msg) ? msg : string.Empty;
        var res = _runtime.BroadcastMessage(message);
        return JsonSerializer.Serialize(res);
    }

    private string ExecutePluginCommand(WebApiRequest req)
    {
        var data = ReadJsonBody(req);
        var player = data.TryGetValue("player", out var playerQuery) ? playerQuery : string.Empty;
        var message = data.TryGetValue("message", out var msg) ? msg : string.Empty;
        var res = _runtime.ExecutePluginCommand(player, message);
        return JsonSerializer.Serialize(res);
    }

    private string GetChat()
    {
        var chat = _runtime.GetChat();
        return JsonSerializer.Serialize(chat);
    }

    private string SendChat(WebApiRequest req)
    {
        var data = ReadJsonBody(req);
        var channel = data.TryGetValue("channel", out var ch) ? ch : "Global";
        var message = data.TryGetValue("message", out var msg) ? msg : string.Empty;
        var res = _runtime.SendChat(channel, message);
        return JsonSerializer.Serialize(res);
    }

    private string GetSquads()
    {
        var squads = _runtime.GetSquads();
        return JsonSerializer.Serialize(squads);
    }

    private string GetKills()
    {
        var kills = _runtime.GetKills();
        return JsonSerializer.Serialize(kills);
    }

    private string GetEconomy()
    {
        var econ = _runtime.GetEconomy();
        return JsonSerializer.Serialize(econ);
    }

    private string GetMap()
    {
        var map = _runtime.GetMapData();
        return JsonSerializer.Serialize(map);
    }

    private string ListPlugins()
    {
        var files = Directory.GetFiles(OxygenPaths.PluginsDir, "*.cs");
        var list = new List<string>();
        foreach (var f in files) list.Add(Path.GetFileName(f));
        return JsonSerializer.Serialize(new { plugins = list });
    }

    private string GetPlugin(WebApiRequest req)
    {
        req.Query.TryGetValue("name", out var rawName);
        var name = Path.GetFileName(rawName ?? string.Empty);
        var path = Path.Combine(OxygenPaths.PluginsDir, name);
        if (!File.Exists(path)) return JsonSerializer.Serialize(new { code = "" });
        return JsonSerializer.Serialize(new { code = File.ReadAllText(path) });
    }

    private string SavePlugin(WebApiRequest req)
    {
        var data = ReadJsonBody(req);
        if (!data.TryGetValue("name", out var name) || !data.TryGetValue("code", out var code))
            return JsonSerializer.Serialize(new { ok = false, error = "missing name or code" });
        var res = _runtime.SavePlugin(name, code);
        return JsonSerializer.Serialize(res);
    }

    private string DeletePlugin(WebApiRequest req)
    {
        req.Query.TryGetValue("name", out var rawName);
        var name = Path.GetFileName(rawName ?? string.Empty);
        var res = _runtime.DeletePlugin(name);
        return JsonSerializer.Serialize(res);
    }

    private string ReloadPlugin(WebApiRequest req)
    {
        var data = ReadJsonBody(req);
        req.Query.TryGetValue("name", out var rawName);
        var name = rawName ?? string.Empty;
        if (data.TryGetValue("name", out var bodyName) && !string.IsNullOrWhiteSpace(bodyName))
            name = bodyName;
        var res = _runtime.ReloadPlugin(name);
        return JsonSerializer.Serialize(res);
    }

    private string ReadLogs()
    {
        var logPath = Path.Combine(OxygenPaths.LogsDir, "Oxygen.log");
        if (!File.Exists(logPath)) return JsonSerializer.Serialize(new { text = "" });
        var text = File.ReadAllText(logPath);
        return JsonSerializer.Serialize(new { text });
    }

    private string ReadRuntimeLog()
    {
        var data = _runtime.GetRuntimeLog();
        return JsonSerializer.Serialize(data);
    }

    private static Dictionary<string, string> ReadJsonBody(WebApiRequest req)
    {
        try
        {
            var body = req.BodyText;
            if (string.IsNullOrWhiteSpace(body)) return new Dictionary<string, string>();
            using var doc = JsonDocument.Parse(body);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ToString();
            }
            return dict;
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}
