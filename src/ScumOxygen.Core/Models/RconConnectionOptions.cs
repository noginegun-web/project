namespace ScumOxygen.Core.Models;

/// <summary>
/// Настройки подключения к RCON-серверу
/// </summary>
public sealed class RconConnectionOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 25575;
    public string Password { get; init; } = string.Empty;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);
    
    // Настройки пула соединений
    public int MinPoolSize { get; init; } = 2;
    public int MaxPoolSize { get; init; } = 5;
    public TimeSpan PoolConnectionIdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    
    // Настройки переподключения
    public int MaxReconnectAttempts { get; init; } = 5;
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);
    public double ReconnectBackoffMultiplier { get; init; } = 2.0;
}
