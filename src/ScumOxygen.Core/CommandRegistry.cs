using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Oxygen.Csharp.API;

namespace ScumOxygen.Core;

public sealed class CommandRegistry
{
    public sealed record CommandHandler(string Name, string Description, string Permission, Action<PlayerBase?, string[]> Handler);

    private readonly ConcurrentDictionary<string, CommandHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(CommandHandler handler)
    {
        _handlers[handler.Name] = handler;
    }

    public bool TryExecute(string name, string[] args, PermissionService permissions, PlayerBase? player, string userId, Logger log)
    {
        if (!_handlers.TryGetValue(name, out var handler))
        {
            log.Warn($"[CommandRegistry] Команда не найдена: {name} (user={userId})");
            return false;
        }

        log.Info($"[CommandRegistry] Выполнение '{handler.Name}' для {userId} args=[{string.Join(", ", args ?? Array.Empty<string>())}]");

        if (!string.IsNullOrWhiteSpace(handler.Permission) && !permissions.HasPermission(userId, handler.Permission))
        {
            log.Warn($"[CommandRegistry] Нет прав: {userId} -> {handler.Permission}");
            return true;
        }

        try
        {
            handler.Handler(player, args ?? Array.Empty<string>());
            log.Info($"[CommandRegistry] '{handler.Name}' выполнена успешно для {userId}");
        }
        catch (Exception ex)
        {
            log.Error($"[CommandRegistry] '{name}' failed: {ex}");
        }

        return true;
    }
}
