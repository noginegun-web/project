using ScumOxygen.Core.Models;

namespace ScumOxygen.Core.Interfaces;

/// <summary>
/// Интерфейс RCON-клиента
/// </summary>
public interface IRconClient : IAsyncDisposable
{
    bool IsConnected { get; }
    
    /// <summary>
    /// Устанавливает соединение с RCON-сервером
    /// </summary>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Отправляет команду и возвращает результат
    /// </summary>
    Task<CommandResult> ExecuteAsync(string command, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Отключается от сервера
    /// </summary>
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}
