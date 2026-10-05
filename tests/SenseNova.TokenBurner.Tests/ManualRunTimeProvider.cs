namespace SenseNova.TokenBurner.Tests;

/// <summary>仅供行为测试，推进异步定时而不等待真实分钟；不模拟网络或计费。</summary>
internal sealed class ManualRunTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public bool HasTimer(TimeSpan remaining)
    {
        lock (_gate) return _timers.Any(timer => timer.Due == _ticks + remaining.Ticks);
    }

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        List<ManualTimer> due;
        lock (_gate)
        {
            _ticks += elapsed.Ticks;
            due = _timers.Where(timer => timer.Due <= _ticks).ToList();
            foreach (var timer in due)
            {
                if (timer.Period > 0) timer.Due = _ticks + timer.Period;
                else { timer.Due = long.MaxValue; _timers.Remove(timer); }
            }
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }

    private sealed class ManualTimer(ManualRunTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long Due { get; set; }
        public long Period { get; private set; }
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed) return false;
                owner._timers.Remove(this);
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                Period = period.Ticks;
                if (Due != long.MaxValue) owner._timers.Add(this);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate) { _disposed = true; owner._timers.Remove(this); }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
