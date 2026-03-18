using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScumOxygen.Core.Models;

namespace ScumOxygen.Core;

public sealed class CommandService
{
    private readonly Logger _log;
    private readonly CommandConfig _cfg;
    private RconClientLite? _rcon;
    private readonly ConsoleCommandSender _console;
    private readonly string _fileQueuePath;
    private Func<string, bool>? _nativeCommandSender;

    public CommandService(Logger log)
    {
        _log = log;
        _cfg = CommandConfig.Load(Path.Combine(OxygenPaths.ConfigsDir, "rcon.json"));
        if (_cfg.Enabled)
        {
            _rcon = new RconClientLite(_cfg.Host, _cfg.Port, _cfg.Password, _log);
        }
        _console = new ConsoleCommandSender(log);
        _fileQueuePath = string.IsNullOrWhiteSpace(_cfg.FileQueuePath)
            ? Path.Combine(OxygenPaths.OxygenDir, "commands.txt")
            : _cfg.FileQueuePath;
    }

    public bool Enabled => _cfg.Enabled;

    public void SetNativeCommandSender(Func<string, bool> sender)
    {
        _nativeCommandSender = sender;
    }

    public CommandResult Execute(string command)
    {
        return ExecuteAsync(command).GetAwaiter().GetResult();
    }

    public async Task<CommandResult> ExecuteAsync(string command, CancellationToken ct = default)
    {
        if (!_cfg.Enabled || _rcon == null)
        {
            if (_nativeCommandSender?.Invoke(command) == true)
            {
                return CommandResult.Ok("native-pipe", TimeSpan.Zero);
            }
            if (_cfg.AllowConsoleFallback && _console.TrySend(command))
            {
                return CommandResult.Ok("console", TimeSpan.Zero);
            }
            if (_cfg.AllowFileQueue && EnqueueFileCommand(command))
            {
                return CommandResult.Ok("file-queue", TimeSpan.Zero);
            }
            _log.Info($"[CommandService] Skipped command (RCON disabled): {command}");
            return CommandResult.Fail("rcon disabled", TimeSpan.Zero);
        }

        try
        {
            return await _rcon.ExecuteAsync(command, ct);
        }
        catch (Exception ex)
        {
            _log.Error($"[CommandService] Command failed: {ex.Message}");
            if (_nativeCommandSender?.Invoke(command) == true)
            {
                return CommandResult.Ok("native-pipe", TimeSpan.Zero);
            }
            if (_cfg.AllowConsoleFallback && _console.TrySend(command))
            {
                return CommandResult.Ok("console-fallback", TimeSpan.Zero);
            }
            if (_cfg.AllowFileQueue && EnqueueFileCommand(command))
            {
                return CommandResult.Ok("file-queue", TimeSpan.Zero);
            }
            return CommandResult.Fail(ex.Message, TimeSpan.Zero);
        }
    }

    private bool EnqueueFileCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_fileQueuePath)!);
            File.AppendAllText(_fileQueuePath, command + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[CommandService] File queue failed: {ex.Message}");
            return false;
        }
    }
}

public sealed class CommandConfig
{
    public bool Enabled { get; set; } = false;
    public bool AllowConsoleFallback { get; set; } = true;
    public bool AllowFileQueue { get; set; } = true;
    public string FileQueuePath { get; set; } = string.Empty;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8881;
    public string Password { get; set; } = "admin";

    public static CommandConfig Load(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var cfg = new CommandConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            return cfg;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CommandConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new CommandConfig();
    }
}
