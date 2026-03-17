namespace ScumOxygen.Core.Models;

/// <summary>
/// Информация об игроке на сервере SCUM
/// </summary>
public sealed class PlayerInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string SteamId { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public TimeSpan ConnectedTime { get; init; }
    public int Ping { get; init; }
    public string Location { get; init; } = string.Empty;
    public float Health { get; init; }
    public float Stamina { get; init; }

    public override string ToString() => 
        $"Player[{Id}] {Name} (Steam: {SteamId}, Ping: {Ping}ms, Connected: {ConnectedTime:hh\\:mm\\:ss})";
}
