using System;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Oxygen.Csharp.API;

namespace ScumOxygen.Core;

[SupportedOSPlatform("windows")]
public sealed class NativeBridgeService
{
    private readonly Logger _log;
    private readonly PlayerRegistry _players;
    private readonly OxygenRuntime _runtime;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public NativeBridgeService(Logger log, PlayerRegistry players, OxygenRuntime runtime)
    {
        _log = log;
        _players = players;
        _runtime = runtime;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoop(_cts.Token));
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        if (cts == null) return;

        try
        {
            cts.Cancel();
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    "ScumOxygen",
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                await pipe.ConnectAsync(2500, ct);
                pipe.ReadMode = PipeTransmissionMode.Message;
                _log.Info("[NativeBridge] Connected to ScumOxygen native pipe.");

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    var message = await ReadMessage(pipe, ct);
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        await Task.Delay(25, ct);
                        continue;
                    }

                    HandleMessage(message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (TimeoutException)
            {
            }
            catch (Exception ex)
            {
                _log.Error($"[NativeBridge] {ex.GetBaseException().Message}");
            }

            try
            {
                await Task.Delay(1500, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task<string> ReadMessage(NamedPipeClientStream pipe, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new System.IO.MemoryStream();

        do
        {
            var bytesRead = await pipe.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (bytesRead <= 0)
                return string.Empty;

            ms.Write(buffer, 0, bytesRead);
        }
        while (!pipe.IsMessageComplete);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void HandleMessage(string message)
    {
        var separator = message.IndexOf('|');
        if (separator <= 0) return;

        var type = message[..separator].Trim();
        var payload = separator + 1 < message.Length ? message[(separator + 1)..] : string.Empty;

        switch (type.ToUpperInvariant())
        {
            case "PLAYER_JOIN":
                HandlePlayerJoin(payload);
                break;
            case "PLAYER_LEAVE":
                HandlePlayerLeave(payload);
                break;
            case "PLAYER_SNAPSHOT":
                HandlePlayerSnapshot(payload);
                break;
            case "PLAYER_ITEM_IN_HANDS":
                HandleItemInHands(payload);
                break;
            case "CHAT_MESSAGE":
                HandleChatMessage(payload);
                break;
        }
    }

    private void HandlePlayerJoin(string payload)
    {
        var dto = Deserialize<NativeJoinLeaveDto>(payload);
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name)) return;

        var player = _players.UpsertFromNative(dto.Name, new Vector3(0, 0, 0));
        _runtime.DispatchPlayerConnected(player);
    }

    private void HandlePlayerLeave(string payload)
    {
        var dto = Deserialize<NativeJoinLeaveDto>(payload);
        if (dto == null) return;

        var player = !string.IsNullOrWhiteSpace(dto.Name)
            ? _players.Find(dto.Name)
            : null;

        if (player != null)
        {
            _players.RemoveFromLogin(player.SteamId, player.Name);
            _runtime.DispatchPlayerDisconnected(player);
        }
        else if (!string.IsNullOrWhiteSpace(dto.Name))
        {
            _players.RemoveFromLogin(string.Empty, dto.Name);
        }
    }

    private void HandlePlayerSnapshot(string payload)
    {
        var dto = Deserialize<NativePlayerSnapshotDto>(payload);
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name)) return;

        var player = _players.UpsertFromNative(dto.Name, new Vector3(dto.X, dto.Y, dto.Z));
        player.Location = new Vector3(dto.X, dto.Y, dto.Z);

        if (!string.IsNullOrWhiteSpace(dto.ItemInHands))
        {
            _runtime.DispatchPlayerTakeItemInHands(player, dto.ItemInHands);
        }
    }

    private void HandleItemInHands(string payload)
    {
        var dto = Deserialize<NativeItemInHandsDto>(payload);
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.ItemInHands)) return;

        var player = _players.UpsertFromNative(dto.Name, new Vector3(0, 0, 0));
        _runtime.DispatchPlayerTakeItemInHands(player, dto.ItemInHands);
    }

    private void HandleChatMessage(string payload)
    {
        var dto = Deserialize<NativeChatDto>(payload);
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Message)) return;

        var player = _players.UpsertFromNative(dto.Name, new Vector3(0, 0, 0));
        _runtime.DispatchPlayerChat(player, dto.Message, dto.ChatType);
    }

    private T? Deserialize<T>(string payload) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _log.Error($"[NativeBridge] Invalid payload for {typeof(T).Name}: {ex.GetBaseException().Message}");
            return null;
        }
    }

    private sealed class NativeJoinLeaveDto
    {
        public string Name { get; set; } = string.Empty;
        public int PlayerId { get; set; }
    }

    private sealed class NativePlayerSnapshotDto
    {
        public string Name { get; set; } = string.Empty;
        public int PlayerId { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string ItemInHands { get; set; } = string.Empty;
    }

    private sealed class NativeItemInHandsDto
    {
        public string Name { get; set; } = string.Empty;
        public int PlayerId { get; set; }
        public string ItemInHands { get; set; } = string.Empty;
    }

    private sealed class NativeChatDto
    {
        public string Name { get; set; } = string.Empty;
        public int PlayerId { get; set; }
        public string Message { get; set; } = string.Empty;
        public int ChatType { get; set; }
    }
}
