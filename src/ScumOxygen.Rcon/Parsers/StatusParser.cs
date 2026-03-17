using System.Globalization;
using System.Text.RegularExpressions;
using ScumOxygen.Core.Interfaces;
using ScumOxygen.Core.Models;

namespace ScumOxygen.Rcon.Parsers;

/// <summary>
/// Парсер ответа команды status
/// </summary>
public sealed class StatusParser : ICommandParser<ServerStatus>
{
    // Примеры вывода status:
    // hostname: SCUM Server
    // map: Croatia
    // players: 25/64
    // uptime: 03:45:12
    // version: 1.0.12345
    // fps: 60
    // memory: 2048 MB
    // tickrate: 30

    public ServerStatus? Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        var result = new ServerStatus
        {
            ServerName = ExtractValue(response, "hostname", "Server Name"),
            Map = ExtractValue(response, "map", "Unknown"),
            Version = ExtractValue(response, "version", "Unknown"),
        };

        // Players
        var playersStr = ExtractValue(response, "players", "0/0");
        var playersMatch = Regex.Match(playersStr, @"(\d+)/(\d+)");
        if (playersMatch.Success)
        {
            result = result with 
            { 
                CurrentPlayers = int.Parse(playersMatch.Groups[1].Value),
                MaxPlayers = int.Parse(playersMatch.Groups[2].Value)
            };
        }

        // Uptime
        var uptimeStr = ExtractValue(response, "uptime", "0:00:00");
        result = result with { Uptime = ParseUptime(uptimeStr) };

        // FPS
        if (TryExtractInt(response, @"fps[:\s]+(\d+)", out int fps))
        {
            result = result with { Fps = fps };
        }

        // Memory
        if (TryExtractFloat(response, @"memory[:\s]+([\d\.]+)", out float memory))
        {
            result = result with { MemoryUsage = memory };
        }

        // TickRate
        if (TryExtractInt(response, @"tickrate[:\s]+(\d+)", out int tickRate))
        {
            result = result with { TickRate = tickRate };
        }

        return result;
    }

    private static string ExtractValue(string text, string key, string defaultValue)
    {
        // Формат 1: key: value
        var match = Regex.Match(text, $"^\\s*{key}[:\\s]+(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        // Формат 2: "key": "value"
        match = Regex.Match(text, $"\"?{key}\"?\\s*[:=]\\s*\"?([^\"\\n]+)\"?", RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        return defaultValue;
    }

    private static bool TryExtractInt(string text, string pattern, out int value)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (match.Success && int.TryParse(match.Groups[1].Value, out value))
            return true;
        
        value = 0;
        return false;
    }

    private static bool TryExtractFloat(string text, string pattern, out float value)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;
        
        value = 0;
        return false;
    }

    private static TimeSpan ParseUptime(string uptimeStr)
    {
        // Форматы: "03:45:12", "3:45:12", "03:45", "3h 45m 12s", "3h45m", "3h"
        uptimeStr = uptimeStr.Trim();

        // Формат с h/m/s
        var hmsMatch = Regex.Match(uptimeStr, @"(?:(\d+)h)?\s*(?:(\d+)m)?\s*(?:(\d+)s)?", RegexOptions.IgnoreCase);
        if (hmsMatch.Success && (hmsMatch.Groups[1].Success || hmsMatch.Groups[2].Success || hmsMatch.Groups[3].Success))
        {
            int hours = hmsMatch.Groups[1].Success ? int.Parse(hmsMatch.Groups[1].Value) : 0;
            int minutes = hmsMatch.Groups[2].Success ? int.Parse(hmsMatch.Groups[2].Value) : 0;
            int seconds = hmsMatch.Groups[3].Success ? int.Parse(hmsMatch.Groups[3].Value) : 0;
            return new TimeSpan(hours, minutes, seconds);
        }

        // Формат с двоеточиями
        var parts = uptimeStr.Split(':');
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

        // Формат "3 hours" или "3h"
        var hourMatch = Regex.Match(uptimeStr, @"(\d+)\s*(?:hours?|h)", RegexOptions.IgnoreCase);
        if (hourMatch.Success)
        {
            int hours = int.Parse(hourMatch.Groups[1].Value);
            return TimeSpan.FromHours(hours);
        }

        // Формат минут
        var minMatch = Regex.Match(uptimeStr, @"(\d+)\s*(?:minutes?|m)", RegexOptions.IgnoreCase);
        if (minMatch.Success)
        {
            int minutes = int.Parse(minMatch.Groups[1].Value);
            return TimeSpan.FromMinutes(minutes);
        }

        // Пробуем как double (дни)
        if (double.TryParse(uptimeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double days))
        {
            return TimeSpan.FromDays(days);
        }

        return TimeSpan.Zero;
    }

    public Task<ServerStatus?> ParseAsync(string response, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Parse(response));
    }
}
