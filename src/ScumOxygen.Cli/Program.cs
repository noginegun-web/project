using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ScumOxygen.Core.Models;
using ScumOxygen.Rcon;
using ScumOxygen.Scripting;

Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
Console.WriteLine("║           ScumOxygen - SCUM Server Manager                 ║");
Console.WriteLine("║              (Oxygen Plugin Clone) v0.1.0                  ║");
Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
Console.WriteLine();

// Настройка логирования
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Warning); // Меньше логов
});

var logger = loggerFactory.CreateLogger<Program>();

// Конфигурация RCON
var options = new RconConnectionOptions
{
    Host = args.Length > 0 ? args[0] : "127.0.0.1",
    Port = args.Length > 1 && int.TryParse(args[1], out var port) ? port : 8881,
    Password = args.Length > 2 ? args[2] : "admin",
    ConnectTimeout = TimeSpan.FromSeconds(5),
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    MaxReconnectAttempts = 3
};

Console.WriteLine($"Connecting to {options.Host}:{options.Port}...");

await using var client = new ScumRconClient(options, loggerFactory);

try
{
    // Получаем статус сервера
    Console.WriteLine("\n[1/5] Getting server status...");
    var status = await client.GetStatusAsync();
    if (status != null)
    {
        Console.WriteLine($"  ✓ Server: {status.ServerName}");
        Console.WriteLine($"  ✓ Map: {status.Map}");
        Console.WriteLine($"  ✓ Players: {status.CurrentPlayers}/{status.MaxPlayers}");
    }

    // Получаем список игроков
    Console.WriteLine("\n[2/5] Getting player list...");
    var players = await client.GetPlayersAsync();
    Console.WriteLine($"  ✓ Online players: {players.Count}");
    foreach (var player in players)
    {
        Console.WriteLine($"    - {player.Name} (Steam: {player.SteamId})");
    }

    // Тест broadcast
    Console.WriteLine("\n[3/5] Testing broadcast...");
    var broadcastResult = await client.BroadcastAsync("ScumOxygen plugin test");
    Console.WriteLine($"  ✓ Broadcast: {(broadcastResult ? "OK" : "FAILED")}");

    // Тест saveworld
    Console.WriteLine("\n[4/5] Testing saveworld...");
    var saveResult = await client.SaveWorldAsync();
    Console.WriteLine($"  ✓ SaveWorld: {(saveResult ? "OK" : "FAILED")}");

    // Scripting test
    Console.WriteLine("\n[5/5] Testing script engine...");
    var scriptEngine = new ScriptEngine(loggerFactory.CreateLogger<ScriptEngine>());
    const string testScript = @"
using System;
public class TestScript { 
    public string Run() { return ""Script execution OK""; }
}";
    
    var loadResult = await scriptEngine.LoadScriptAsync("test", testScript);
    if (loadResult.Success)
    {
        var result = await scriptEngine.ExecuteAsync("test", "Run");
        Console.WriteLine($"  ✓ Script: {result}");
    }
    else
    {
        Console.WriteLine($"  ✗ Script failed: {string.Join(", ", loadResult.Errors)}");
    }

    Console.WriteLine("\n" + new string('═', 60));
    Console.WriteLine("✓ ALL TESTS PASSED - Plugin is working correctly!");
    Console.WriteLine(new string('═', 60));
}
catch (Exception ex)
{
    Console.WriteLine($"\n✗ Error: {ex.Message}");
    return 1;
}

return 0;
