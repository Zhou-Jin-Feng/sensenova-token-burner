namespace SenseNova.TokenBurner.Core;

/// <summary>单进程 UTC 锚定计划；异步定时、错过不补跑，保护信号立即关闭计划。</summary>
public sealed class RunScheduler
{
    private static readonly TimeSpan ClockCheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ClockJumpTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumStartLateness = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly SingleRunEngine _engine;
    private readonly TimeProvider _time;
    private ScheduleSnapshot _snapshot = ScheduleSnapshot.Disabled;
    private Session? _session;
    private Task? _loop;
    private Task<RunSnapshot>? _activeRun;
    private bool _shutdown;

    public RunScheduler(SingleRunEngine engine, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>由外部协调器注入的运行准入检查（如周额度超额拦截）；返回 false 时跳过本轮。</summary>
    public Func<bool>? CanStartSchedule { get; set; }

    public ScheduleSnapshot Snapshot
    {
        get
        {
            lock (_gate) return _snapshot with
            {
                CurrentRun = _engine.Snapshot,
                LastRun = _activeRun is { IsCompletedSuccessfully: true } ? _activeRun.Result : _snapshot.LastRun
            };
        }
    }

    /// <summary>首次或重新启用均须显式调用，立即开始首轮；0% 不启动计划或请求。</summary>
    public bool Enable(ScheduleConfiguration configuration, CancellationToken cancellationToken = default)
        => Enable(configuration, null, cancellationToken);

    public bool Enable(ScheduleConfiguration configuration, RunSnapshot? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        lock (_gate)
        {
            if (_shutdown) throw new InvalidOperationException("调度器已关闭。");
            if (_loop is { IsCompleted: false } || _activeRun is { IsCompleted: false })
                throw new InvalidOperationException("计划或当前轮尚未收尾。");
            var now = _time.GetUtcNow().ToUniversalTime();
            var next = NextBoundary(now, now, configuration.Interval);
            if (configuration.Run.TargetTokens == 0)
            {
                _snapshot = new(ScheduleState.Disabled, configuration, null, null, 0, 0, null, null);
                return false;
            }
            var session = new Session(now, _time.GetTimestamp());
            _session = session;
            if (CanStartSchedule?.Invoke() == false)
            {
                _snapshot = new(ScheduleState.Enabled, configuration, now, next, 0, 0, null, null);
                _loop = Task.Run(() => ExecuteScheduleAsync(session));
                return true;
            }
            // 先做引擎预检，再公开启用；入口失败不留下半启动计划。
            _activeRun = progress is not null ? _engine.RunWithProgressAsync(configuration.Run, progress, cancellationToken) : _engine.RunAsync(configuration.Run, cancellationToken);
            _snapshot = new(ScheduleState.Enabled, configuration, now, next, 1, 0, null, null);
            _loop = Task.Run(() => ExecuteScheduleAsync(session));
            return true;
        }
    }

    /// <summary>配置不改变本轮参数；改间隔从原锚点计算未来边界，不触发立即补跑。</summary>
    public void UpdateConfiguration(ScheduleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        lock (_gate)
        {
            if (_shutdown) throw new InvalidOperationException("调度器已关闭。");
            var next = _snapshot.NextRunUtc;
            if (_snapshot.State == ScheduleState.Enabled && _snapshot.Configuration!.Interval != configuration.Interval)
            {
                var reference = Later(_time.GetUtcNow(), _session!.HighWatermarkUtc);
                next = NextBoundary(_snapshot.AnchorUtc!.Value, reference, configuration.Interval);
            }
            _snapshot = _snapshot with { Configuration = configuration, NextRunUtc = next };
            if (configuration.Run.TargetTokens == 0 && _snapshot.State == ScheduleState.Enabled)
                DisableLocked(ScheduleState.Disabled, ScheduleStopReason.UserDisabled);
            else _session?.Wake.TrySetResult();
        }
    }

    /// <summary>仅关闭未来调度并等待定时任务退出，不取消当前消耗请求。</summary>
    public async Task<ScheduleSnapshot> DisableAsync(CancellationToken waitCancellation = default)
    {
        Task? loop;
        lock (_gate)
        {
            if (!_shutdown && _snapshot.State == ScheduleState.Enabled)
                DisableLocked(ScheduleState.Disabled, ScheduleStopReason.UserDisabled);
            loop = _loop;
        }
        if (loop is not null) await loop.WaitAsync(waitCancellation).ConfigureAwait(false);
        return Snapshot;
    }

    /// <summary>停止本轮保留计划；若停止产生未知或时限问题，保护规则仍会关闭计划。</summary>
    public Task<RunSnapshot> StopCurrentRunAsync(CancellationToken waitCancellation = default)
        => _engine.StopAsync(waitCancellation);

    /// <summary>退出时先关闭计划再停止本轮；未响应取消的请求仍由引擎保留互斥。</summary>
    public async Task<RunSnapshot> ShutdownAsync(CancellationToken waitCancellation = default)
    {
        Task? loop;
        lock (_gate)
        {
            _shutdown = true;
            DisableLocked(ScheduleState.Shutdown, ScheduleStopReason.Shutdown);
            loop = _loop;
        }
        var stopping = _engine.StopAsync(waitCancellation);
        if (loop is not null) await loop.WaitAsync(waitCancellation).ConfigureAwait(false);
        return await stopping.ConfigureAwait(false);
    }

    private async Task ExecuteScheduleAsync(Session session)
    {
        while (true)
        {
            TimeSpan delay;
            Task wake;
            Task<RunSnapshot>? current;
            Task? protection;
            lock (_gate)
            {
                CollectCompletedRunLocked();
                if (_snapshot.State != ScheduleState.Enabled) return;
                var run = _engine.Snapshot;
                if (run.ReviewRequired || run.Failure is not null || run.State is RunState.AwaitingReview or RunState.Faulted)
                {
                    DisableLocked(ScheduleState.Blocked, run.ReviewRequired || run.State == RunState.AwaitingReview
                        ? ScheduleStopReason.RunProtection : ScheduleStopReason.RunFailure);
                    return;
                }
                var now = _time.GetUtcNow().ToUniversalTime();
                var timestamp = _time.GetTimestamp();
                var elapsed = _time.GetElapsedTime(session.PreviousTimestamp, timestamp);
                var drift = now - session.PreviousUtc - elapsed;
                session.PreviousUtc = now;
                session.PreviousTimestamp = timestamp;
                var next = _snapshot.NextRunUtc!.Value;
                var interval = _snapshot.Configuration!.Interval;
                var clockChanged = elapsed < TimeSpan.Zero || drift.Duration() > ClockJumpTolerance;
                if (clockChanged || now >= next)
                {
                    // 长休眠/晚唤醒/时钟跳变只重排未来边界，不执行过去周期。
                    var onTime = !clockChanged && now >= next && now - next <= MaximumStartLateness;
                    var canStart = false;
                    if (now >= next)
                    {
                        var passed = (now.UtcTicks - next.UtcTicks) / interval.Ticks + 1;
                        var last = next.AddTicks((passed - 1) * interval.Ticks);
                        session.HighWatermarkUtc = Later(session.HighWatermarkUtc, last);
                        canStart = onTime && passed == 1 && _activeRun is null && (CanStartSchedule?.Invoke() ?? true);
                        _snapshot = _snapshot with { SkippedCycles = checked(_snapshot.SkippedCycles + passed - (canStart ? 1 : 0)) };
                    }
                    try
                    {
                        next = NextBoundary(_snapshot.AnchorUtc!.Value, Later(now, session.HighWatermarkUtc), interval);
                        _snapshot = _snapshot with { NextRunUtc = next };
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        DisableLocked(ScheduleState.Blocked, ScheduleStopReason.ClockRangeExceeded);
                        return;
                    }
                    if (canStart && !TryStartRunLocked()) return;
                }
                delay = next - now;
                if (delay > ClockCheckInterval) delay = ClockCheckInterval;
                if (session.Wake.Task.IsCompleted) session.Wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                wake = session.Wake.Task;
                current = _activeRun;
                protection = current is null ? null : _engine.ProtectionTriggered;
            }
            // 最多每分钟观察一次时钟；其他操作/运行完成/保护立即唤醒。无独立线程忙轮询。
            using var waitCancellation = new CancellationTokenSource();
            var timer = Task.Delay(delay, _time, waitCancellation.Token);
            if (current is null) await Task.WhenAny(timer, wake).ConfigureAwait(false);
            else await Task.WhenAny(timer, wake, current, protection!).ConfigureAwait(false);
            await waitCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private bool TryStartRunLocked()
    {
        try
        {
            _activeRun = _engine.RunAsync(_snapshot.Configuration!.Run);
            _snapshot = _snapshot with { StartedRuns = checked(_snapshot.StartedRuns + 1) };
            return true;
        }
        catch (Exception)
        {
            // 参数预检或适配器拒绝均安全停计划，不保存私有异常正文。
            DisableLocked(ScheduleState.Blocked, ScheduleStopReason.StartRejected);
            return false;
        }
    }

    private void CollectCompletedRunLocked()
    {
        if (_activeRun is not { IsCompleted: true }) return;
        if (_activeRun.IsCompletedSuccessfully) _snapshot = _snapshot with { LastRun = _activeRun.Result };
        else DisableLocked(ScheduleState.Blocked, ScheduleStopReason.RunFailure);
        _activeRun = null;
    }

    private void DisableLocked(ScheduleState state, ScheduleStopReason reason)
    {
        _snapshot = _snapshot with { State = state, StopReason = reason, NextRunUtc = null };
        _session?.Wake.TrySetResult();
    }

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static DateTimeOffset NextBoundary(DateTimeOffset anchor, DateTimeOffset reference, TimeSpan interval)
    {
        var cycles = reference >= anchor ? (reference.UtcTicks - anchor.UtcTicks) / interval.Ticks + 1 : 1;
        if (cycles > (DateTimeOffset.MaxValue.UtcTicks - anchor.UtcTicks) / interval.Ticks)
            throw new ArgumentOutOfRangeException(nameof(interval), "计划的未来时间超出可表示范围。");
        return anchor.AddTicks(cycles * interval.Ticks);
    }

    private sealed class Session(DateTimeOffset now, long timestamp)
    {
        public DateTimeOffset HighWatermarkUtc { get; set; } = now;
        public DateTimeOffset PreviousUtc { get; set; } = now;
        public long PreviousTimestamp { get; set; } = timestamp;
        public TaskCompletionSource Wake { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
