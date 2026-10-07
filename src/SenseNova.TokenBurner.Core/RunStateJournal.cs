namespace SenseNova.TokenBurner.Core;

/// <summary>串行保存检查点，失败锁住本次会话；没有后台队列或周期落盘。</summary>
internal sealed class RunStateJournal(IRunStateStore store, TimeProvider time) : IRunCheckpoint
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    private RecoverySnapshot _snapshot = new(RecoveryState.NotInitialized, null);
    private bool _active;
    public RecoverySnapshot Snapshot => Volatile.Read(ref _snapshot);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State != RecoveryState.NotInitialized) return;
            try
            {
                var document = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                document?.Validate();
                var run = document?.CurrentRun;
                var needsReview = run is { ReviewedUtc: null } && (!RunStateDocument.IsTerminal(run.Snapshot.State)
                    || run.Snapshot.ReviewRequired || run.Snapshot.UnknownUsageRequests > 0);
                Volatile.Write(ref _snapshot, new(needsReview ? RecoveryState.NeedsReview
                    : document is null ? RecoveryState.Ready : RecoveryState.AwaitingConfirmation,
                    document ?? RunStateDocument.Empty));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { Volatile.Write(ref _snapshot, new(RecoveryState.Corrupt, null)); }
        }
        finally { _writes.Release(); }
    }

    public void EnsureCanStart()
    {
        if (Snapshot.State != RecoveryState.Ready)
            throw new InvalidOperationException("运行记录尚未确认或不可用，不能开始消耗。");
    }

    public async Task ConfirmAsync(bool reviewCompleted, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recovery = Snapshot;
            if (_active) throw new InvalidOperationException("当前轮尚未结束，不能确认恢复。");
            if (recovery.State == RecoveryState.NeedsReview && !reviewCompleted)
                throw new InvalidOperationException("上次运行可能有未知用量，请先核查后明确确认。");
            if (recovery.State is not (RecoveryState.AwaitingConfirmation or RecoveryState.NeedsReview))
                throw new InvalidOperationException("当前状态不接受恢复确认。");
            var document = recovery.Document!;
            if (document.CurrentRun is { } run && recovery.State == RecoveryState.NeedsReview)
                document = document with { CurrentRun = run with { ReviewedUtc = LaterNow(run.UpdatedUtc) } };
            // 确认不启用计划，也不改写旧轮的未知用量和在途证据。
            await CommitAsync(document with { PlanRequested = false }, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, new(RecoveryState.Ready, Snapshot.Document));
        }
        finally { _writes.Release(); }
    }

    public async Task BeginAsync(RunSnapshot snapshot)
    {
        await _writes.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureCanStart();
            var document = Snapshot.Document!;
            var history = document.CurrentRun is null ? document.History
                : document.History.Append(document.CurrentRun).TakeLast(RunStateDocument.MaximumHistory).ToArray();
            var now = time.GetUtcNow().ToUniversalTime();
            await CommitAsync(document with { CurrentRun = new(Guid.NewGuid(), now, now, snapshot), History = history },
                CancellationToken.None).ConfigureAwait(false);
            _active = true;
        }
        finally { _writes.Release(); }
    }

    public async Task SaveAsync(RunSnapshot snapshot)
    {
        await _writes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Snapshot.State == RecoveryState.StorageFailed) throw new LocalStorageException("运行记录写入已失败。");
            var document = Snapshot.Document!;
            var current = document.CurrentRun ?? throw new InvalidOperationException("运行记录未开始。");
            await CommitAsync(document with
            {
                CurrentRun = current with { Snapshot = snapshot, UpdatedUtc = LaterNow(current.UpdatedUtc) }
            }, CancellationToken.None).ConfigureAwait(false);
            _active = !RunStateDocument.IsTerminal(snapshot.State);
            if (!_active && snapshot.ReviewRequired)
                Volatile.Write(ref _snapshot, new(RecoveryState.NeedsReview, Snapshot.Document));
        }
        finally { _writes.Release(); }
    }

    public async Task SetPlanAsync(ScheduleConfiguration configuration, bool requested, CancellationToken cancellationToken)
    {
        configuration.Validate();
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCanStart();
            await CommitAsync(Snapshot.Document! with { Plan = configuration, PlanRequested = requested }, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    public async Task ResetProgressAsync(CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCanStart();
            if (_active) throw new InvalidOperationException("当前轮尚未收尾，不能重置进度。");
            var document = Snapshot.Document!;
            var current = document.CurrentRun;
            // Ready下无ReviewRequired的未知计数可来自已核查的续跑历史；新的未知必定触发核查保护。
            if (current is { ReviewedUtc: null } && (!RunStateDocument.IsTerminal(current.Snapshot.State)
                || current.Snapshot.ReviewRequired))
                throw new InvalidOperationException("存在未知用量，请先核查再重置。");
            var history = current is null ? document.History
                : document.History.Append(current).TakeLast(RunStateDocument.MaximumHistory).ToArray();
            await CommitAsync(document with { CurrentRun = null, History = history, PlanRequested = false }, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    private DateTimeOffset LaterNow(DateTimeOffset previous)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        return now > previous ? now : previous;
    }

    private async Task CommitAsync(RunStateDocument document, CancellationToken cancellationToken)
    {
        try
        {
            await store.SaveAsync(document, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, Snapshot with { Document = document });
        }
        catch (Exception)
        {
            // 包括取消：调用方不能确定替换是否已完成，须重新打开并核查持久记录。
            Volatile.Write(ref _snapshot, Snapshot with { State = RecoveryState.StorageFailed });
            throw new LocalStorageException("运行记录无法安全保存，本次会话已阻止继续消耗。");
        }
    }
}
