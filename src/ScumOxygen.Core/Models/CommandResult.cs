namespace ScumOxygen.Core.Models;

/// <summary>
/// Результат выполнения RCON-команды
/// </summary>
public readonly struct CommandResult
{
    public bool Success { get; init; }
    public string Response { get; init; }
    public string? Error { get; init; }
    public TimeSpan ExecutionTime { get; init; }

    public static CommandResult Ok(string response, TimeSpan executionTime) => new()
    {
        Success = true,
        Response = response,
        ExecutionTime = executionTime
    };

    public static CommandResult Fail(string error, TimeSpan executionTime) => new()
    {
        Success = false,
        Response = string.Empty,
        Error = error,
        ExecutionTime = executionTime
    };
}
