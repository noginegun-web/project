using ScumOxygen.Core.Models;

namespace ScumOxygen.Core.Interfaces;

/// <summary>
/// Интерфейс пула соединений RCON
/// </summary>
public interface IRconConnectionPool : IAsyncDisposable
{
    /// <summary>
    /// Получает соединение из пула
    /// </summary>
    Task<IRconClient> RentAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Возвращает соединение в пул
    /// </summary>
    ValueTask ReturnAsync(IRconClient client);
    
    /// <summary>
    /// Текущее количество активных соединений
    /// </summary>
    int ActiveConnections { get; }
    
    /// <summary>
    /// Текущее количество доступных соединений в пуле
    /// </summary>
    int AvailableConnections { get; }
}
