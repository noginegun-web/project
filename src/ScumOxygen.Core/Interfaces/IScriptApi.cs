namespace ScumOxygen.Core.Interfaces;

/// <summary>
/// API сервера для скриптов
/// </summary>
public interface IServer
{
    /// <summary>
    /// Количество игроков онлайн
    /// </summary>
    int OnlinePlayers { get; }

    /// <summary>
    /// Выполняет RCON команду
    /// </summary>
    Task ExecuteCommandAsync(string command);

    /// <summary>
    /// Отправляет сообщение всем игрокам
    /// </summary>
    Task AnnounceAsync(string message);

    /// <summary>
    /// Получает игрока по Steam ID
    /// </summary>
    Task<IPlayer?> GetPlayerAsync(string steamId);

    /// <summary>
    /// Получает список всех игроков
    /// </summary>
    Task<IReadOnlyList<IPlayer>> GetPlayersAsync();
}

/// <summary>
/// API игрока для скриптов
/// </summary>
public interface IPlayer
{
    string SteamId { get; }
    string CharacterName { get; }
    float Health { get; }
    float Stamina { get; }
    int FamePoints { get; }
    
    Task TeleportAsync(float x, float y, float z);
    Task GiveItemAsync(string itemId, int count);
    Task KickAsync(string reason);
    Task BanAsync(int durationMinutes, string? reason = null);
}

/// <summary>
/// Интерфейс для скриптов
/// </summary>
public interface IScript
{
    Task InitializeAsync(IServer server);
    Task ExecuteAsync();
    Task ShutdownAsync();
}
