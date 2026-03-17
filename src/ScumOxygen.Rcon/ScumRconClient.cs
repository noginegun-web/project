using Microsoft.Extensions.Logging;
using ScumOxygen.Core.Interfaces;
using ScumOxygen.Core.Models;
using ScumOxygen.Rcon.Parsers;
using ScumOxygen.Rcon.Pool;

namespace ScumOxygen.Rcon;

/// <summary>
/// Высокоуровневый клиент для работы с SCUM RCON
/// </summary>
public sealed class ScumRconClient : IAsyncDisposable
{
    private readonly IRconConnectionPool _pool;
    private readonly ILogger<ScumRconClient>? _logger;
    private readonly ListPlayersParser _playersParser = new();
    private readonly StatusParser _statusParser = new();

    public ScumRconClient(RconConnectionOptions options, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<ScumRconClient>();
        _pool = new RconConnectionPool(options, loggerFactory);
    }

    /// <summary>
    /// Получает список игроков на сервере
    /// </summary>
    public async Task<IReadOnlyList<PlayerInfo>> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _pool.RentAsync(cancellationToken);
        
        var result = await connection.ExecuteAsync("listplayers", cancellationToken);
        
        if (!result.Success)
        {
            _logger?.LogError("Failed to get players: {Error}", result.Error);
            return Array.Empty<PlayerInfo>();
        }

        var players = _playersParser.Parse(result.Response);
        return players ?? Array.Empty<PlayerInfo>();
    }

    /// <summary>
    /// Получает статус сервера
    /// </summary>
    public async Task<ServerStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _pool.RentAsync(cancellationToken);
        
        var result = await connection.ExecuteAsync("status", cancellationToken);
        
        if (!result.Success)
        {
            _logger?.LogError("Failed to get status: {Error}", result.Error);
            return null;
        }

        return _statusParser.Parse(result.Response);
    }

    /// <summary>
    /// Выполняет произвольную RCON-команду
    /// </summary>
    public async Task<CommandResult> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        
        await using var connection = await _pool.RentAsync(cancellationToken);
        return await connection.ExecuteAsync(command, cancellationToken);
    }

    /// <summary>
    /// Кикает игрока с сервера
    /// </summary>
    public async Task<bool> KickPlayerAsync(string playerName, string? reason = null, CancellationToken cancellationToken = default)
    {
        var command = string.IsNullOrEmpty(reason) 
            ? $"kick {playerName}" 
            : $"kick {playerName} {reason}";
        
        var result = await ExecuteCommandAsync(command, cancellationToken);
        return result.Success;
    }

    /// <summary>
    /// Банит игрока по SteamID
    /// </summary>
    public async Task<bool> BanPlayerAsync(string steamId, string? reason = null, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        var command = duration.HasValue
            ? $"ban {steamId} {(int)duration.Value.TotalMinutes} {reason ?? ""}"
            : $"ban {steamId} {reason ?? ""}";
        
        var result = await ExecuteCommandAsync(command, cancellationToken);
        return result.Success;
    }

    /// <summary>
    /// Отправляет сообщение всем игрокам
    /// </summary>
    public async Task<bool> BroadcastAsync(string message, CancellationToken cancellationToken = default)
    {
        var command = $"broadcast {message}";
        var result = await ExecuteCommandAsync(command, cancellationToken);
        return result.Success;
    }

    /// <summary>
    /// Телепортирует игрока
    /// </summary>
    public async Task<bool> TeleportPlayerAsync(string playerName, float x, float y, float z, CancellationToken cancellationToken = default)
    {
        var command = $"teleport {playerName} {x} {y} {z}";
        var result = await ExecuteCommandAsync(command, cancellationToken);
        return result.Success;
    }

    /// <summary>
    /// Устанавливает время на сервере
    /// </summary>
    public async Task<bool> SetTimeAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        var command = $"settime {time.Hours:00}:{time.Minutes:00}";
        var result = await ExecuteCommandAsync(command, cancellationToken);
        return result.Success;
    }

    /// <summary>
    /// Сохраняет мир
    /// </summary>
    public async Task<bool> SaveWorldAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCommandAsync("saveworld", cancellationToken);
        return result.Success;
    }

    /// <summary>
    /// Останавливает сервер
    /// </summary>
    public async Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCommandAsync("shutdown", cancellationToken);
        return result.Success;
    }

    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync();
    }
}
