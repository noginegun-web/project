using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var baseDir = AppContext.BaseDirectory;
builder.Host.UseContentRoot(baseDir);
builder.WebHost.UseWebRoot(Path.Combine(baseDir, "wwwroot"));

builder.Configuration.AddJsonFile("control.config.json", optional: true, reloadOnChange: true);

var bind = builder.Configuration.GetValue<string>("bind", "0.0.0.0");
var port = builder.Configuration.GetValue<int>("port", 18800);

builder.WebHost.UseUrls($"http://{bind}:{port}");

var app = builder.Build();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

var hub = new ControlHub();

app.Map("/ws", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.HandleSocket(socket);
});

app.MapGet("/api/servers", () => hub.GetServers());
app.MapGet("/api/plugins", (string serverId) => hub.Request(serverId, "list_plugins"));
app.MapGet("/api/plugin", (string serverId, string name) => hub.Request(serverId, "get_plugin", new { name }));
app.MapPost("/api/plugin", async (PluginSaveRequest req) => await hub.Request(req.ServerId, "save_plugin", new { name = req.Name, code = req.Code }));
app.MapDelete("/api/plugin", (string serverId, string name) => hub.Request(serverId, "delete_plugin", new { name }));
app.MapPost("/api/reload", async (PluginActionRequest req) => await hub.Request(req.ServerId, "reload_plugin", new { name = req.Name }));
app.MapGet("/api/logs", (string serverId) => hub.Request(serverId, "get_logs"));
app.MapGet("/api/players", (string serverId) => hub.Request(serverId, "get_players"));
app.MapGet("/api/status", (string serverId) => hub.Request(serverId, "get_status"));
app.MapPost("/api/command", async (CommandRequest req) => await hub.Request(req.ServerId, "exec_command", new { command = req.Command }));
app.MapPost("/api/broadcast", async (BroadcastRequest req) => await hub.Request(req.ServerId, "broadcast", new { message = req.Message }));
app.MapGet("/api/chat", (string serverId) => hub.Request(serverId, "get_chat"));
app.MapPost("/api/chat", async (ChatSendRequest req) => await hub.Request(req.ServerId, "send_chat", new { channel = req.Channel, message = req.Message }));
app.MapGet("/api/squads", (string serverId) => hub.Request(serverId, "get_squads"));
app.MapGet("/api/kills", (string serverId) => hub.Request(serverId, "get_kills"));

app.Run();

record PluginSaveRequest(string ServerId, string Name, string Code);
record PluginActionRequest(string ServerId, string Name);
record CommandRequest(string ServerId, string Command);
record BroadcastRequest(string ServerId, string Message);
record ChatSendRequest(string ServerId, string Channel, string Message);

sealed class ControlHub
{
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    public object GetServers()
    {
        var list = _sessions.Values
            .Select(s => new
            {
                id = s.ServerId,
                token = s.Token,
                version = s.Version,
                connectedAt = s.ConnectedAt
            })
            .ToList();
        return new { servers = list };
    }

    public async Task<object> Request(string serverId, string action, object? args = null)
    {
        if (!_sessions.TryGetValue(serverId, out var session))
            return new { ok = false, error = "server not connected" };

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        var payload = new
        {
            type = "request",
            requestId,
            action,
            name = args?.GetType().GetProperty("name")?.GetValue(args),
            code = args?.GetType().GetProperty("code")?.GetValue(args),
            command = args?.GetType().GetProperty("command")?.GetValue(args),
            message = args?.GetType().GetProperty("message")?.GetValue(args)
        };

        await Send(session.Socket, payload);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (result.TryGetProperty("data", out var data))
            return data;
        return new { ok = false };
    }

    public async Task HandleSocket(WebSocket socket)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        string? serverId = null;

        while (socket.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult res;
            do
            {
                res = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    if (serverId != null) _sessions.TryRemove(serverId, out _);
                    return;
                }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
            } while (!res.EndOfMessage);

            using var doc = JsonDocument.Parse(sb.ToString());
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "hello")
            {
                serverId = doc.RootElement.GetProperty("serverId").GetString() ?? Guid.NewGuid().ToString("N");
                var token = doc.RootElement.TryGetProperty("token", out var tokenEl) ? tokenEl.GetString() ?? "" : "";
                var version = doc.RootElement.TryGetProperty("version", out var versionEl) ? versionEl.GetString() ?? "" : "";
                _sessions[serverId] = new ClientSession(serverId, socket, token, version, DateTimeOffset.UtcNow);
                continue;
            }
            if (type == "response")
            {
                var reqId = doc.RootElement.GetProperty("requestId").GetString();
                if (reqId != null && _pending.TryRemove(reqId, out var tcs))
                {
                    tcs.TrySetResult(doc.RootElement.Clone());
                }
            }
        }
    }

    private static async Task Send(WebSocket socket, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
}

record ClientSession(string ServerId, WebSocket Socket, string Token, string Version, DateTimeOffset ConnectedAt);
