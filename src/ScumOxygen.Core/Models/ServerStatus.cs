namespace ScumOxygen.Core.Models;

/// <summary>
/// Статус SCUM-сервера
/// </summary>
public sealed record ServerStatus
{
    public string ServerName { get; init; } = string.Empty;
    public string Map { get; init; } = string.Empty;
    public int CurrentPlayers { get; init; }
    public int MaxPlayers { get; init; }
    public TimeSpan Uptime { get; init; }
    public string Version { get; init; } = string.Empty;
    public int Fps { get; init; }
    public float MemoryUsage { get; init; }
    public int TickRate { get; init; }

    public override string ToString() =>
        $"{ServerName} | {Map} | Players: {CurrentPlayers}/{MaxPlayers} | Uptime: {Uptime:dd\\:hh\\:mm\\:ss} | FPS: {Fps}";
}
