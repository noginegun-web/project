using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ScumOxygen.Core;

public sealed class HookService
{
    private readonly ConcurrentDictionary<string, List<Delegate>> _hooks = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string eventName, Delegate handler)
    {
        var list = _hooks.GetOrAdd(eventName, _ => new List<Delegate>());
        lock (list)
        {
            list.Add(handler);
        }
    }

    public void Emit(string eventName, params object?[] args)
    {
        if (!_hooks.TryGetValue(eventName, out var list)) return;
        Delegate[] snapshot;
        lock (list)
        {
            snapshot = list.ToArray();
        }
        foreach (var d in snapshot)
        {
            try
            {
                d.DynamicInvoke(args);
            }
            catch
            {
                // Swallow to keep server stable
            }
        }
    }
}
