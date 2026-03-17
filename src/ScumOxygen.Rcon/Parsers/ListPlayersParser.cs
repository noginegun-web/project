using System.Globalization;
using System.Text.RegularExpressions;
using ScumOxygen.Core.Interfaces;
using ScumOxygen.Core.Models;

namespace ScumOxygen.Rcon.Parsers;

/// <summary>
/// Парсер ответа команды listplayers
/// </summary>
public sealed class ListPlayersParser : ICommandParser<IReadOnlyList<PlayerInfo>>
{
    // Примеры строк из SCUM:
    // 0, Username, 76561198000000000, 192.168.1.100, 01:23:45, 45, Sector A1, 100.0, 85.0
    // 1, Player Name, 76561198000000001, 10.0.0.5, 00:15:30, 120, Sector B2, 75.5, 60.0
    
    private static readonly Regex PlayerRegex = new(
        @"^(\d+)\s*,\s*([^,]+)\s*,\s*(\d+)\s*,\s*([\d\.]+)\s*,\s*(\d{2}:\d{2}:\d{2})\s*,\s*(\d+)\s*,\s*([^,]+)\s*,\s*([\d\.]+)\s*,\s*([\d\.]+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public IReadOnlyList<PlayerInfo>? Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return Array.Empty<PlayerInfo>();

        var players = new List<PlayerInfo>();
        
        var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine))
                continue;

            // Пробуем разные форматы
            var player = TryParseCsvFormat(trimmedLine) 
                      ?? TryParseColonFormat(trimmedLine)
                      ?? TryParseSimpleFormat(trimmedLine);
            
            if (player != null)
            {
                players.Add(player);
            }
        }

        return players.AsReadOnly();
    }

    private PlayerInfo? TryParseCsvFormat(string line)
    {
        // ID, Name, SteamID, IP, Time, Ping, Location, Health, Stamina
        var parts = line.Split(',').Select(p => p.Trim()).ToArray();
        
        if (parts.Length < 5)
            return null;

        try
        {
            int id = int.Parse(parts[0], CultureInfo.InvariantCulture);
            string name = parts[1];
            string steamId = parts.Length > 2 ? parts[2] : "0";
            string ip = parts.Length > 3 ? parts[3] : "0.0.0.0";
            
            TimeSpan connectedTime = ParseTime(parts.Length > 4 ? parts[4] : "0:00:00");
            int ping = parts.Length > 5 ? int.Parse(parts[5], CultureInfo.InvariantCulture) : 0;
            string location = parts.Length > 6 ? parts[6] : "Unknown";
            float health = parts.Length > 7 ? float.Parse(parts[7], CultureInfo.InvariantCulture) : 100f;
            float stamina = parts.Length > 8 ? float.Parse(parts[8], CultureInfo.InvariantCulture) : 100f;

            return new PlayerInfo
            {
                Id = id,
                Name = name,
                SteamId = steamId,
                IpAddress = ip,
                ConnectedTime = connectedTime,
                Ping = ping,
                Location = location,
                Health = health,
                Stamina = stamina
            };
        }
        catch
        {
            return null;
        }
    }

    private PlayerInfo? TryParseColonFormat(string line)
    {
        // Формат: ID: 0, Name: Username, SteamID: 7656..., IP: ..., Time: ..., Ping: ..., Location: ..., Health: ..., Stamina: ...
        if (!line.Contains("Name:"))
            return null;

        try
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pairs = line.Split(',');
            
            foreach (var pair in pairs)
            {
                var kv = pair.Split(':', 2);
                if (kv.Length == 2)
                {
                    dict[kv[0].Trim()] = kv[1].Trim();
                }
            }

            return new PlayerInfo
            {
                Id = dict.TryGetValue("ID", out var id) ? int.Parse(id) : 0,
                Name = dict.GetValueOrDefault("Name") ?? "Unknown",
                SteamId = dict.GetValueOrDefault("SteamID") ?? "0",
                IpAddress = dict.GetValueOrDefault("IP") ?? "0.0.0.0",
                ConnectedTime = ParseTime(dict.GetValueOrDefault("Time") ?? "0:00:00"),
                Ping = dict.TryGetValue("Ping", out var ping) ? int.Parse(ping) : 0,
                Location = dict.GetValueOrDefault("Location") ?? "Unknown",
                Health = dict.TryGetValue("Health", out var health) ? float.Parse(health, CultureInfo.InvariantCulture) : 100f,
                Stamina = dict.TryGetValue("Stamina", out var stamina) ? float.Parse(stamina, CultureInfo.InvariantCulture) : 100f
            };
        }
        catch
        {
            return null;
        }
    }

    private PlayerInfo? TryParseSimpleFormat(string line)
    {
        // Простой формат: ID Name SteamID IP Time
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length < 3)
            return null;

        try
        {
            if (!int.TryParse(parts[0], out int id))
                return null;

            // SteamID обычно 17 цифр
            var steamIdPart = parts.FirstOrDefault(p => p.Length >= 17 && p.All(char.IsDigit));
            var steamId = steamIdPart ?? "0";
            
            // Имя - часто в кавычках или между ID и SteamID
            var name = parts.Length > 1 ? parts[1].Trim('"') : "Unknown";
            
            return new PlayerInfo
            {
                Id = id,
                Name = name,
                SteamId = steamId,
                IpAddress = "0.0.0.0",
                ConnectedTime = TimeSpan.Zero,
                Ping = 0,
                Location = "Unknown",
                Health = 100f,
                Stamina = 100f
            };
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan ParseTime(string timeStr)
    {
        // Форматы: "01:23:45", "1:23:45", "01:23", "1:23"
        var parts = timeStr.Split(':');
        
        if (parts.Length == 3)
        {
            int hours = int.Parse(parts[0]);
            int minutes = int.Parse(parts[1]);
            int seconds = int.Parse(parts[2]);
            return new TimeSpan(hours, minutes, seconds);
        }
        else if (parts.Length == 2)
        {
            int minutes = int.Parse(parts[0]);
            int seconds = int.Parse(parts[1]);
            return new TimeSpan(0, minutes, seconds);
        }
        
        return TimeSpan.Zero;
    }

    public Task<IReadOnlyList<PlayerInfo>?> ParseAsync(string response, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Parse(response));
    }
}
