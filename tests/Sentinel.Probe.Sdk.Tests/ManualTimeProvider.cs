using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sentinel.Probe.Sdk.Tests;

// A TimeProvider whose clock and timers move only when a test says so. The heartbeat's
// PeriodicTimer is built over the provider, so a test fires a tick with ManualTimer.Fire and
// never waits on a wall clock. (No package dependency: Microsoft.Extensions.TimeProvider.Testing
// is not in the locked graph.)
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _now;
    private readonly List<ManualTimer> _timers = new();

    public ManualTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public static ManualTimeProvider AtUnixMilliseconds(long milliseconds) =>
        new(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));

    /// <summary>When set, the next clock read throws it once.</summary>
    public Exception? FailNextRead { get; set; }

    public IReadOnlyList<ManualTimer> Timers
    {
        get { lock (_lock) return new List<ManualTimer>(_timers); }
    }

    public void SetUnixMilliseconds(long milliseconds)
    {
        lock (_lock) _now = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            var failure = FailNextRead;
            if (failure != null)
            {
                FailNextRead = null;
                throw failure;
            }
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime, period);
        lock (_lock) _timers.Add(timer);
        return timer;
    }
}

internal sealed class ManualTimer : ITimer
{
    private readonly TimerCallback _callback;
    private readonly object? _state;

    public ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _callback = callback;
        _state = state;
        DueTime = dueTime;
        Period = period;
    }

    public TimeSpan DueTime { get; private set; }
    public TimeSpan Period { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Runs the timer callback once, as an elapsed period would. A disposed timer is inert.</summary>
    public void Fire()
    {
        if (!Disposed)
        {
            _callback(_state);
        }
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        DueTime = dueTime;
        Period = period;
        return !Disposed;
    }

    public void Dispose() => Disposed = true;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
