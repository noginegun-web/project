using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScumOxygen.Core;

public sealed class ControlClient
{
    private readonly Logger _log;
    private readonly OxygenRuntime _runtime;
    private readonly CancellationTokenSource _cts = new();

    public ControlClient(Logger log, OxygenRuntime runtime)
    {
        _log = log;
        _runtime = runtime;
    }

    public void Start()
    {
        var cfgPath = Path.Combine(OxygenPaths.ConfigsDir, "control.json");
        var cfg = ControlConfig.Load(cfgPath);
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.WsUrl))
        {
            _log.Info("ControlClient disabled or WsUrl empty");
            return;
        }

        Task.Run(() => RunLoop(cfg, _cts.Token));
    }

    private async Task RunLoop(ControlConfig cfg, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(cfg.WsUrl), ct);
                _log.Info($"ControlClient connected: {cfg.WsUrl}");

                await SendJson(ws, new
                {
                    type = "hello",
                    serverId = cfg.ServerId,
                    token = cfg.Token,
                    version = "1.0.0"
                }, ct);

                await ReceiveLoop(ws, ct);
            }
            catch (Exception ex)
            {
                _log.Error($"ControlClient error: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult? res;
            do
            {
                res = await ws.ReceiveAsync(buffer, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                    return;
                }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
            } while (!res.EndOfMessage);

            HandleMessage(ws, sb.ToString(), ct);
        }
    }

    private void HandleMessage(ClientWebSocket ws, string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;
        var type = typeEl.GetString() ?? string.Empty;
        if (!type.Equals("request", StringComparison.OrdinalIgnoreCase)) return;

        var requestId = doc.RootElement.GetProperty("requestId").GetString() ?? string.Empty;
        var action = doc.RootElement.GetProperty("action").GetString() ?? string.Empty;

        object? data = action switch
        {
            "list_plugins" => _runtime.ListPlugins(),
            "get_plugin" => _runtime.GetPlugin(doc.RootElement.GetProperty("name").GetString() ?? ""),
            "save_plugin" => _runtime.SavePlugin(doc.RootElement.GetProperty("name").GetString() ?? "", doc.RootElement.GetProperty("code").GetString() ?? ""),
            "delete_plugin" => _runtime.DeletePlugin(doc.RootElement.GetProperty("name").GetString() ?? ""),
            "reload_plugin" => _runtime.ReloadPlugin(doc.RootElement.GetProperty("name").GetString() ?? ""),
            "get_logs" => _runtime.GetLogs(),
            "get_players" => _runtime.GetPlayers(),
            "exec_command" => _runtime.ExecuteCommand(doc.RootElement.GetProperty("command").GetString() ?? ""),
            "broadcast" => _runtime.BroadcastMessage(doc.RootElement.GetProperty("message").GetString() ?? ""),
            "get_status" => _runtime.GetStatus(),
            "get_chat" => _runtime.GetChat(),
            "send_chat" => _runtime.SendChat(
                doc.RootElement.TryGetProperty("channel", out var ch) ? ch.GetString() ?? "Global" : "Global",
                doc.RootElement.GetProperty("message").GetString() ?? string.Empty),
            "get_squads" => _runtime.GetSquads(),
            "get_kills" => _runtime.GetKills(),
            "get_map" => _runtime.GetMapData(),
            _ => new { ok = false, error = "unknown action" }
        };

        _ = SendJson(ws, new { type = "response", requestId, ok = true, data }, ct);
    }

    private static async Task SendJson(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}

public sealed class ControlConfig
{
    public bool Enabled { get; set; } = false;
    public string WsUrl { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string Token { get; set; } = "";

    public static ControlConfig Load(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var cfg = new ControlConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            return cfg;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ControlConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new ControlConfig();
    }
}
