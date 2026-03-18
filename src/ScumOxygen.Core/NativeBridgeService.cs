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
    private readonly object _pipeLock = new();
    private DateTimeOffset _lastPipeUnavailableLog = DateTimeOffset.MinValue;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private NamedPipeClientStream? _pipe;

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

    public bool IsConnected
    {
        get
        {
            lock (_pipeLock)
            {
                return _pipe != null && _pipe.IsConnected;
            }
        }
    }

    public bool WaitUntilConnected(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (IsConnected)
                return true;

            Thread.Sleep(100);
        }

        return IsConnected;
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
                lock (_pipeLock)
                {
                    _pipe = pipe;
                }
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
            finally
            {
                lock (_pipeLock)
                {
                    _pipe = null;
                }
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

    public bool TrySendServerCommand(string command, bool raw = false)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        try
        {
            lock (_pipeLock)
            {
                if (_pipe == null || !_pipe.IsConnected)
                {
                    LogPipeUnavailable(command, raw);
                    return false;
                }

                var payload = $"{(raw ? "RAW" : "CMD")}|{command}";
                var bytes = Encoding.UTF8.GetBytes(payload);
                _pipe.Write(bytes, 0, bytes.Length);
                _pipe.Flush();
                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[NativeBridge] Command send failed: {ex.GetBaseException().Message}");
            return false;
        }
    }

    private void LogPipeUnavailable(string command, bool raw)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPipeUnavailableLog < TimeSpan.FromSeconds(10))
            return;

        _lastPipeUnavailableLog = now;
        _log.Warn($"[NativeBridge] Pipe unavailable for {(raw ? "raw" : "command")} send: {command}");
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

        var displayName = !string.IsNullOrWhiteSpace(dto.ProfileName) ? dto.ProfileName : dto.Name;
        var player = _players.UpsertFromNative(displayName, new Vector3(0, 0, 0), dto.SteamId);
        player.NativePlayerId = dto.PlayerId;
        if (dto.DatabaseId > 0)
            player.DatabaseId = (int)Math.Clamp(dto.DatabaseId, int.MinValue, int.MaxValue);
        if (!string.IsNullOrWhiteSpace(dto.FakeName))
            player.FakeName = dto.FakeName;
        player.LastNativeUpdate = DateTimeOffset.UtcNow;
        _runtime.DispatchPlayerConnected(player);
    }

    private void HandlePlayerLeave(string payload)
    {
        var dto = Deserialize<NativeJoinLeaveDto>(payload);
        if (dto == null) return;

        var player = !string.IsNullOrWhiteSpace(dto.Name)
            ? _players.Find(dto.Name)
            : (!string.IsNullOrWhiteSpace(dto.SteamId) ? _players.Find(dto.SteamId) : null);

        if (player != null)
        {
            _players.RemoveFromLogin(!string.IsNullOrWhiteSpace(dto.SteamId) ? dto.SteamId : player.SteamId, player.Name);
            _runtime.DispatchPlayerDisconnected(player);
        }
        else if (!string.IsNullOrWhiteSpace(dto.Name))
        {
            _players.RemoveFromLogin(dto.SteamId, dto.Name);
        }
    }

    private void HandlePlayerSnapshot(string payload)
    {
        var dto = Deserialize<NativePlayerSnapshotDto>(payload);
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name)) return;

        var displayName = !string.IsNullOrWhiteSpace(dto.ProfileName) ? dto.ProfileName : dto.Name;
        var player = _players.UpsertFromNative(displayName, new Vector3(dto.X, dto.Y, dto.Z), dto.SteamId);
        player.NativePlayerId = dto.PlayerId;
        if (dto.DatabaseId > 0)
            player.DatabaseId = (int)Math.Clamp(dto.DatabaseId, int.MinValue, int.MaxValue);
        if (!string.IsNullOrWhiteSpace(dto.FakeName))
            player.FakeName = dto.FakeName;
        player.Location = new Vector3(dto.X, dto.Y, dto.Z);
        player.LastNativeUpdate = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(dto.ItemInHands))
        {
            player.ItemInHands = dto.ItemInHands;
            _runtime.DispatchPlayerTakeItemInHands(player, dto.ItemInHands);
        }
    }

    private void HandleItemInHands(string payload)
    {
        var dto = Deserialize<NativeItemInHandsDto>(payload);
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.ItemInHands)) return;

        var displayName = !string.IsNullOrWhiteSpace(dto.ProfileName) ? dto.ProfileName : dto.Name;
        var player = _players.UpsertFromNative(displayName, new Vector3(0, 0, 0), dto.SteamId);
        player.NativePlayerId = dto.PlayerId;
        if (dto.DatabaseId > 0)
            player.DatabaseId = (int)Math.Clamp(dto.DatabaseId, int.MinValue, int.MaxValue);
        if (!string.IsNullOrWhiteSpace(dto.FakeName))
            player.FakeName = dto.FakeName;
        player.ItemInHands = dto.ItemInHands;
        player.LastNativeUpdate = DateTimeOffset.UtcNow;
        _runtime.DispatchPlayerTakeItemInHands(player, dto.ItemInHands);
    }

    private void HandleChatMessage(string payload)
    {
        var dto = Deserialize<NativeChatDto>(payload);
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Message)) return;

        var displayName = !string.IsNullOrWhiteSpace(dto.ProfileName) ? dto.ProfileName : dto.Name;
        var player = _players.UpsertFromNative(displayName, new Vector3(0, 0, 0), dto.SteamId);
        player.NativePlayerId = dto.PlayerId;
        if (dto.DatabaseId > 0)
            player.DatabaseId = (int)Math.Clamp(dto.DatabaseId, int.MinValue, int.MaxValue);
        if (!string.IsNullOrWhiteSpace(dto.FakeName))
            player.FakeName = dto.FakeName;
        player.LastNativeUpdate = DateTimeOffset.UtcNow;
        _log.Info($"[NativeBridge] CHAT_MESSAGE name={dto.Name} playerId={dto.PlayerId} chatType={dto.ChatType} message={dto.Message}");
        _runtime.TryHandlePlayerCommand(player, dto.Message);
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
        public string ProfileName { get; set; } = string.Empty;
        public string FakeName { get; set; } = string.Empty;
        public string SteamId { get; set; } = string.Empty;
        public long DatabaseId { get; set; }
        public int PlayerId { get; set; }
    }

    private sealed class NativePlayerSnapshotDto
    {
        public string Name { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
        public string FakeName { get; set; } = string.Empty;
        public string SteamId { get; set; } = string.Empty;
        public long DatabaseId { get; set; }
        public int PlayerId { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string ItemInHands { get; set; } = string.Empty;
    }

    private sealed class NativeItemInHandsDto
    {
        public string Name { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
        public string FakeName { get; set; } = string.Empty;
        public string SteamId { get; set; } = string.Empty;
        public long DatabaseId { get; set; }
        public int PlayerId { get; set; }
        public string ItemInHands { get; set; } = string.Empty;
    }

    private sealed class NativeChatDto
    {
        public string Name { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
        public string FakeName { get; set; } = string.Empty;
        public string SteamId { get; set; } = string.Empty;
        public long DatabaseId { get; set; }
        public int PlayerId { get; set; }
        public string Message { get; set; } = string.Empty;
        public int ChatType { get; set; }
    }
}
