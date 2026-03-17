using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ScumOxygen.Core;

public sealed class TimerService
{
    private readonly ConcurrentDictionary<Guid, Timer> _timers = new();

    public Guid Every(TimeSpan interval, Action action)
    {
        var id = Guid.NewGuid();
        var timer = new Timer(_ => action(), null, interval, interval);
        _timers[id] = timer;
        return id;
    }

    public Guid After(TimeSpan delay, Action action)
    {
        var id = Guid.NewGuid();
        var timer = new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan);
        _timers[id] = timer;
        return id;
    }

    public bool Cancel(Guid id)
    {
        if (_timers.TryRemove(id, out var timer))
        {
            timer.Dispose();
            return true;
        }
        return false;
    }
}
