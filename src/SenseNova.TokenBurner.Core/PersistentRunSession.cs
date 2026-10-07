namespace SenseNova.TokenBurner.Core;

/// <summary>面板共用的持久运行入口；初始化与核查确认不开始消耗。</summary>
public sealed class PersistentRunSession
{
    private readonly RunStateJournal _journal;
    private readonly SingleRunEngine _engine;
    private readonly RunScheduler _scheduler;
    private readonly SemaphoreSlim _controls = new(1, 1);

    public PersistentRunSession(IRunRequestExecutor executor, IRunStateStore store,
        RunProtectionOptions? protection = null, TimeProvider? timeProvider = null, RequestConcurrencyLimiter? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var time = timeProvider ?? TimeProvider.System;
        _journal = new(store, time);
        _engine = new(executor, protection, time, _journal, limiter);
        _scheduler = new(_engine, time);
    }

    public RecoverySnapshot Recovery => _journal.Snapshot;
    public RunSnapshot CurrentRun => _engine.Snapshot;
    public ScheduleSnapshot Schedule => _scheduler.Snapshot;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => _journal.InitializeAsync(cancellationToken);
    public bool Pause() => _engine.Pause();
    public bool Resume() => _engine.Resume();
    public Task<RunSnapshot> StopAsync(CancellationToken cancellationToken = default) => _engine.StopAsync(cancellationToken);
    public Task<RunSnapshot> WaitForCompletionAsync() => _engine.WaitForCompletionAsync();

    public async Task ConfirmRecoveryAsync(bool reviewCompleted = false, CancellationToken cancellationToken = default)
    {
        await _controls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_engine.HasActiveRun)
                throw new InvalidOperationException("请先停止并等待当前轮实际结束，再核查确认。");
            await _journal.ConfirmAsync(reviewCompleted, cancellationToken).ConfigureAwait(false);
            _engine.AcknowledgeReview();
        }
        finally { _controls.Release(); }
    }

    public async Task<RunSnapshot> RunOnceAsync(RunParameters parameters, CancellationToken cancellationToken = default)
    {
        Task<RunSnapshot> run;
        await _controls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Schedule.State == ScheduleState.Enabled) throw new InvalidOperationException("请先关闭计划，再手动开始单轮。");
            run = _engine.RunAsync(parameters, cancellationToken);
        }
        finally { _controls.Release(); }
        return await run.ConfigureAwait(false);
    }

    /// <summary>显式继续已确认进度；旧检查点归档保留，未知请求不会作为原请求自动重发。</summary>
    public async Task<RunSnapshot> ContinueRemainingAsync(CancellationToken cancellationToken = default,
        RunParameters? executionParameters = null)
    {
        Task<RunSnapshot> run;
        await _controls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInactive();
            if (Recovery.State != RecoveryState.Ready)
                throw new InvalidOperationException("请先确认记录；未知请求须先核查官方用量。");
            var previous = Recovery.Document?.CurrentRun?.Snapshot;
            if (previous?.Parameters is null || previous.State == RunState.Completed
                || previous.ConfirmedUsage.TotalTokens >= previous.Parameters.TargetTokens)
                throw new InvalidOperationException("没有可继续的剩余目标。");
            var parameters = executionParameters ?? previous.Parameters;
            if (parameters.TargetTokens != previous.Parameters.TargetTokens || parameters.ModelId != previous.Parameters.ModelId)
                throw new ArgumentException("继续剩余目标不能修改原轮的模型或目标。");
            run = _engine.RunWithProgressAsync(parameters, previous, cancellationToken);
        }
        finally { _controls.Release(); }
        return await run.ConfigureAwait(false);
    }

    /// <summary>显式归档本轮并清零客户端进度；不删除历史、不请求服务端、不启用计划。</summary>
    public async Task ResetProgressAsync(bool reviewCompleted = false, CancellationToken cancellationToken = default)
    {
        await _controls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInactive();
            if (Recovery.State is RecoveryState.AwaitingConfirmation or RecoveryState.NeedsReview)
                await _journal.ConfirmAsync(reviewCompleted, cancellationToken).ConfigureAwait(false);
            if (Recovery.Document?.CurrentRun?.ReviewedUtc is not null) _engine.AcknowledgeReview();
            _engine.EnsureCanReset();
            await _journal.ResetProgressAsync(cancellationToken).ConfigureAwait(false);
            _engine.ResetProgress(); // 只有归档成功才能清内存，写失败仍保留原进度。
        }
        finally { _controls.Release(); }
    }

    public async Task<bool> EnableScheduleAsync(ScheduleConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _controls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInactive();
            await SavePlanAsync(configuration, configuration.Run.TargetTokens > 0, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return _scheduler.Enable(configuration, cancellationToken);
        }
        finally { _controls.Release(); }
    }

    public async Task UpdateScheduleAsync(ScheduleConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _controls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SavePlanAsync(configuration, Schedule.State == ScheduleState.Enabled && configuration.Run.TargetTokens > 0,
                cancellationToken).ConfigureAwait(false);
            _scheduler.UpdateConfiguration(configuration);
        }
        finally { _controls.Release(); }
    }

    public async Task DisableScheduleAsync(CancellationToken cancellationToken = default)
    {
        await _controls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _scheduler.DisableAsync(cancellationToken).ConfigureAwait(false);
            if (Recovery.State == RecoveryState.Ready && Recovery.Document?.Plan is { } plan)
                await SavePlanAsync(plan, false, cancellationToken).ConfigureAwait(false);
        }
        finally { _controls.Release(); }
    }

    public async Task<RunSnapshot> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        // 即使保存计划失败，也须执行退出收尾；在途证据由检查点保留。
        try { await DisableScheduleAsync(cancellationToken).ConfigureAwait(false); }
        finally { await _scheduler.ShutdownAsync(cancellationToken).ConfigureAwait(false); }
        return CurrentRun;
    }

    private void EnsureInactive()
    {
        if (Schedule.State == ScheduleState.Enabled || _engine.HasActiveRun)
            throw new InvalidOperationException("已有计划或运行尚未收尾。");
    }

    private async Task SavePlanAsync(ScheduleConfiguration configuration, bool requested, CancellationToken cancellationToken)
    {
        try { await _journal.SetPlanAsync(configuration, requested, cancellationToken).ConfigureAwait(false); }
        catch (LocalStorageException)
        {
            _engine.BlockStorage();
            await _scheduler.DisableAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
