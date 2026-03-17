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
            return false;
        }

        if (!string.IsNullOrWhiteSpace(handler.Permission) && !permissions.HasPermission(userId, handler.Permission))
        {
            log.Info($"Permission denied: {userId} -> {handler.Permission}");
            return true;
        }

        try
        {
            handler.Handler(player, args);
        }
        catch (Exception ex)
        {
            log.Error($"Command '{name}' failed: {ex.Message}");
        }

        return true;
    }
}
