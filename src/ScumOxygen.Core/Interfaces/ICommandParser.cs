using ScumOxygen.Core.Models;

namespace ScumOxygen.Core.Interfaces;

/// <summary>
/// Парсер ответов RCON-команд
/// </summary>
public interface ICommandParser<T> where T : class
{
    /// <summary>
    /// Парсит ответ команды в объект
    /// </summary>
    T? Parse(string response);
    
    /// <summary>
    /// Пытается парсить ответ асинхронно
    /// </summary>
    Task<T?> ParseAsync(string response, CancellationToken cancellationToken = default);
}
