namespace SenseNova.TokenBurner.Core;

/// <summary>单轮预算与异常保护；等待不占线程，未收尾请求继续持有互斥。</summary>
public sealed class SingleRunEngine
{
    private readonly object _gate = new();
    private readonly IRunRequestExecutor _executor;
    private readonly RunProtectionOptions _protection;
    private readonly TimeProvider _time;
    private RunSnapshot _snapshot = RunSnapshot.Idle;
    private Session? _session;

    public SingleRunEngine(IRunRequestExecutor executor, RunProtectionOptions? protection = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _protection = protection ?? new();
        _protection.Validate();
        _executor = executor;
        _time = timeProvider ?? TimeProvider.System;
    }

    public RunSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    // 同一 Core 内的调度接缝：异常立即唤醒计划，不靠循环轮询，也不暴露原始异常。
    internal Task ProtectionTriggered
    {
        get { lock (_gate) return _session?.ProtectionSignal.Task ?? Task.CompletedTask; }
    }

    public Task<RunSnapshot> RunAsync(RunParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        _executor.ValidateParameters(parameters);
        Session session;
        lock (_gate)
        {
            if (_session is { Completion.Task.IsCompleted: false })
                throw new InvalidOperationException("已有运行尚未结束。");
            if (_snapshot.ReviewRequired)
                throw new InvalidOperationException("上一轮仍需人工核查，确认后才能显式开始新轮。");
            session = new();
            _session = session;
            _snapshot = new(RunState.Running, parameters, new(0, 0, 0), 0, 0, 0, null);
            if (cancellationToken.IsCancellationRequested || parameters.TargetTokens == 0)
            {
                FinishLocked(session, cancellationToken.IsCancellationRequested
                    ? RunEndReason.LifecycleCancellation : RunEndReason.TargetReached);
                session.AbortRequests.Dispose();
                return session.Completion.Task;
            }
        }
        _ = Task.Run(() => ExecuteRunAsync(parameters, session, cancellationToken));
        return session.Completion.Task;
    }

    public bool Pause()
    {
        lock (_gate)
        {
            if (_snapshot.State != RunState.Running) return false;
            _session!.ResumeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _snapshot = _snapshot with { State = RunState.Paused };
            return true;
        }
    }

    public bool Resume()
    {
        lock (_gate)
        {
            // 保护暂停不可由普通继续绕过：先停止、核查，再显式新开。
            if (_snapshot.State != RunState.Paused || _snapshot.ReviewRequired) return false;
            _snapshot = _snapshot with { State = RunState.Running };
            _session!.ResumeSignal!.TrySetResult();
            return true;
        }
    }

    /// <summary>只确认核查完成，不清除历史未知计数，不自动开始。</summary>
    public bool AcknowledgeReview()
    {
        lock (_gate)
        {
            if (_session is { Completion.Task.IsCompleted: false } || !_snapshot.ReviewRequired) return false;
            _snapshot = _snapshot with { ReviewRequired = false };
            return true;
        }
    }

    /// <summary>停止新增并有限等待；超时请求取消，仍在执行时返回 Stopping 而不释放互斥。</summary>
    public async Task<RunSnapshot> StopAsync(CancellationToken waitCancellation = default)
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            if (session is null) return _snapshot;
            if (_snapshot.State is RunState.Running or RunState.Paused or RunState.AwaitingReview)
            {
                _snapshot = _snapshot with { State = RunState.Stopping, EndReason = RunEndReason.UserStop };
                session.RetryPending = false;
                session.ResumeSignal?.TrySetResult();
            }
            session.StopSignal.TrySetResult();
        }
        try
        {
            return await session.Completion.Task.WaitAsync(_protection.StopGracePeriod, _time, waitCancellation).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            RunSnapshot snapshot;
            lock (_gate)
            {
                if (session.Completion.Task.IsCompleted) return session.Completion.Task.Result;
                MarkFailureLocked(RunFailureKind.StopTimeout);
                _snapshot = _snapshot with { StopWaitExpired = true, ReviewRequired = true };
                snapshot = _snapshot;
            }
            // 取消回调在锁外执行，晚到的有效 usage 仍汇总。
            _ = CancelRequestsAsync(session);
            return snapshot;
        }
    }

    private async Task CancelRequestsAsync(Session session)
    {
        try { await session.AbortRequests.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* 本轮恰好已完成。 */ }
        catch (AggregateException)
        {
            // 执行器的取消回调出错也不回显正文、不丢失仍在执行的请求。
            lock (_gate)
            {
                if (session.Completion.Task.IsCompleted) return;
                MarkFailureLocked(RunFailureKind.RequestFailed);
                _snapshot = _snapshot with { ReviewRequired = true };
            }
        }
    }

    private async Task ExecuteRunAsync(RunParameters parameters, Session session, CancellationToken cancellationToken)
    {
        using var lifecycle = cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (session.Completion.Task.IsCompleted) return;
                ObserveLifecycleStopLocked(session);
            }
        });
        try
        {
            var pending = new List<Task<RequestOutcome>>(parameters.MaximumConcurrency);
            while (true)
            {
                Task? resume = null;
                TimeSpan? retryDelay = null;
                // 先结算已完成结果，再决定补发，故障不得被新请求越过。
                foreach (var task in pending.Where(task => task.IsCompleted).ToArray())
                {
                    var outcome = await task.ConfigureAwait(false);
                    pending.Remove(task);
                    lock (_gate) SettleLocked(outcome, parameters, session);
                }
                while (true)
                {
                    bool dispatch;
                    lock (_gate)
                    {
                        // WaitAsync 的取消回调可能先于注册回调运行；不能等回调才停止，否则同步取消会空转。
                        if (cancellationToken.IsCancellationRequested) ObserveLifecycleStopLocked(session);
                        dispatch = _snapshot.State == RunState.Running && !session.RetryPending
                            && !cancellationToken.IsCancellationRequested && CanDispatchLocked(parameters);
                        if (dispatch)
                            _snapshot = _snapshot with
                            {
                                InFlightRequests = _snapshot.InFlightRequests + 1,
                                ReservedTokens = _snapshot.ReservedTokens + parameters.RequestTokenReservation
                            };
                    }
                    if (!dispatch) break;
                    pending.Add(ExecuteRequestAsync(parameters, session, cancellationToken));
                    if (pending.Any(task => task.IsCompleted)) break;
                }
                lock (_gate)
                {
                    if (pending.Count == 0)
                    {
                        if (_snapshot.State == RunState.Stopping)
                        {
                            FinishLocked(session, _snapshot.EndReason ?? RunEndReason.UserStop);
                            return;
                        }
                        if (_snapshot.State == RunState.AwaitingReview) resume = session.ResumeSignal!.Task;
                        else if (_snapshot.ConfirmedUsage.TotalTokens >= parameters.TargetTokens)
                        {
                            FinishLocked(session, RunEndReason.TargetReached);
                            return;
                        }
                        else if (_snapshot.State == RunState.Paused) resume = session.ResumeSignal!.Task;
                        else if (!CanDispatchLocked(parameters))
                        {
                            FinishLocked(session, RunEndReason.BudgetRemainder);
                            return;
                        }
                        else if (session.RetryPending)
                        {
                            retryDelay = GetRetryDelayLocked(session);
                            if (retryDelay is null)
                            {
                                MarkFailureLocked(RunFailureKind.RetryLimitReached);
                                FinishLocked(session, RunEndReason.Failure);
                                return;
                            }
                            _snapshot = _snapshot with
                            {
                                IsBackingOff = true,
                                RetryAttempts = _snapshot.RetryAttempts + 1,
                                TotalRetryDelay = _snapshot.TotalRetryDelay + retryDelay.Value
                            };
                        }
                    }
                }
                if (resume is not null)
                {
                    try { await resume.WaitAsync(cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                }
                else if (retryDelay is { } delay)
                {
                    using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var wait = Task.Delay(delay, _time, delayCancellation.Token);
                    if (await Task.WhenAny(wait, session.StopSignal.Task).ConfigureAwait(false) != wait)
                        await delayCancellation.CancelAsync().ConfigureAwait(false);
                    try { await wait.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested) { }
                    lock (_gate)
                    {
                        session.RetryPending = false;
                        session.RetryAfter = null;
                        _snapshot = _snapshot with { IsBackingOff = false };
                    }
                }
                else if (pending.Count > 0)
                    await Task.WhenAny(pending).ConfigureAwait(false);
            }
        }
        finally { session.AbortRequests.Dispose(); }
    }

    private bool CanDispatchLocked(RunParameters parameters)
    {
        var remaining = parameters.TargetTokens - _snapshot.ConfirmedUsage.TotalTokens;
        if (remaining <= 0) return false;
        var reserve = parameters.RequestTokenReservation;
        var limit = reserve > 0 && remaining <= reserve * parameters.MaximumConcurrency ? 1 : parameters.MaximumConcurrency;
        return _snapshot.InFlightRequests < limit && remaining - _snapshot.ReservedTokens >= reserve;
    }

    private void ObserveLifecycleStopLocked(Session session)
    {
        if (_snapshot.Failure is null or RunFailureKind.RateOrQuotaLimit)
            _snapshot = _snapshot with { State = RunState.Stopping, EndReason = RunEndReason.LifecycleCancellation };
        session.RetryPending = false;
        session.ResumeSignal?.TrySetResult();
        session.StopSignal.TrySetResult();
    }

    private TimeSpan? GetRetryDelayLocked(Session session)
    {
        if (_snapshot.RetryAttempts >= _protection.MaximumRetryAttempts) return null;
        var exponential = TimeSpan.FromTicks(_protection.InitialRetryDelay.Ticks * (1L << _snapshot.RetryAttempts));
        var delay = exponential < _protection.MaximumRetryDelay ? exponential : _protection.MaximumRetryDelay;
        if (session.RetryAfter is { } hint && hint > delay) delay = hint;
        if (delay > _protection.MaximumRetryDelay || _snapshot.TotalRetryDelay + delay > _protection.MaximumTotalRetryDelay)
            return null; // 不缩短服务端要求，也不放大本地等待上限。
        return delay;
    }

    private async Task<RequestOutcome> ExecuteRequestAsync(RunParameters parameters, Session session, CancellationToken lifecycleToken)
    {
        using var deadline = new CancellationTokenSource(_protection.RequestTimeout, _time);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifecycleToken, session.AbortRequests.Token, deadline.Token);
        var expired = false;
        try
        {
            var request = _executor.ExecuteAsync(parameters, requestCancellation.Token);
            try { return new(await request.WaitAsync(_protection.RequestTimeout, _time).ConfigureAwait(false), null); }
            catch (TimeoutException) when (!request.IsCompleted)
            {
                expired = true;
                lock (_gate)
                {
                    MarkFailureLocked(RunFailureKind.RequestTimeout);
                    _snapshot = _snapshot with { ReviewRequired = true };
                    session.RetryPending = false;
                    session.ResumeSignal?.TrySetResult();
                }
                // WaitAsync 超时不等于原请求结束，继续观察并保持预留与互斥。
                return new(await request.ConfigureAwait(false), null);
            }
        }
        catch (SenseNovaApiException exception)
        { return new(null, MapFailure(exception.Kind), exception.Delivery, exception.RetryAfter); }
        catch (OperationCanceledException) when (expired || deadline.IsCancellationRequested)
        { return new(null, RunFailureKind.RequestTimeout); }
        catch (OperationCanceledException) when (lifecycleToken.IsCancellationRequested)
        { return new(null, RunFailureKind.Canceled); }
        catch (OperationCanceledException) when (session.AbortRequests.IsCancellationRequested)
        { return new(null, RunFailureKind.StopTimeout); }
        catch (Exception) { return new(null, RunFailureKind.RequestFailed); }
    }

    private static RunFailureKind MapFailure(ApiFailureKind kind) => kind switch
    {
        ApiFailureKind.Authentication => RunFailureKind.Authentication,
        ApiFailureKind.InvalidRequest => RunFailureKind.InvalidRequest,
        ApiFailureKind.ModelUnavailable => RunFailureKind.ModelUnavailable,
        ApiFailureKind.RateOrQuotaLimit => RunFailureKind.RateOrQuotaLimit,
        ApiFailureKind.Transient => RunFailureKind.Transient,
        ApiFailureKind.Protocol => RunFailureKind.Protocol,
        _ => RunFailureKind.Network
    };

    private void SettleLocked(RequestOutcome outcome, RunParameters parameters, Session session)
    {
        _snapshot = _snapshot with
        {
            InFlightRequests = _snapshot.InFlightRequests - 1,
            ReservedTokens = _snapshot.ReservedTokens - parameters.RequestTokenReservation,
            NotSentRequests = _snapshot.NotSentRequests + (outcome.Delivery == RequestDelivery.NotSent ? 1 : 0),
            RejectedRequests = _snapshot.RejectedRequests + (outcome.Delivery == RequestDelivery.Rejected ? 1 : 0)
        };
        var failure = outcome.Failure ?? AcceptUsageLocked(outcome.Usage);
        var unknown = failure is not null and not RunFailureKind.NoProgress && outcome.Delivery == RequestDelivery.Unknown;
        if (failure is null && parameters.RequestTokenReservation > 0 && outcome.Usage!.TotalTokens > parameters.RequestTokenReservation)
        {
            failure = RunFailureKind.BudgetEstimateExceeded;
            unknown = false;
        }
        if (failure is null) return;
        _snapshot = _snapshot with
        {
            LastFailure = failure,
            UnknownUsageRequests = _snapshot.UnknownUsageRequests + (unknown ? 1 : 0),
            ReviewRequired = _snapshot.ReviewRequired || unknown || failure == RunFailureKind.RateOrQuotaLimit
        };
        if (outcome.Delivery == RequestDelivery.NotSent && failure is RunFailureKind.Network or RunFailureKind.Transient)
        {
            if (_snapshot.State is RunState.Running or RunState.Paused && !_snapshot.ReviewRequired)
            {
                session.RetryPending = true;
                if (outcome.RetryAfter is { } hint && (session.RetryAfter is null || hint > session.RetryAfter))
                    session.RetryAfter = hint;
            }
            return;
        }
        session.RetryPending = false;
        if (failure == RunFailureKind.RateOrQuotaLimit && _snapshot.State is RunState.Running or RunState.Paused or RunState.AwaitingReview)
        {
            if (_snapshot.State != RunState.AwaitingReview)
                session.ResumeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _snapshot = _snapshot with { State = RunState.AwaitingReview, Failure = failure };
            _session!.ProtectionSignal.TrySetResult();
            return;
        }
        MarkFailureLocked(failure.Value);
        session.ResumeSignal?.TrySetResult();
    }

    private void MarkFailureLocked(RunFailureKind failure)
    {
        var effective = _snapshot.Failure is null or RunFailureKind.Canceled or RunFailureKind.RateOrQuotaLimit or RunFailureKind.StopTimeout
            ? failure : _snapshot.Failure;
        _snapshot = _snapshot with
        {
            State = RunState.Stopping,
            Failure = effective,
            LastFailure = failure,
            EndReason = effective == RunFailureKind.Canceled ? RunEndReason.LifecycleCancellation : RunEndReason.Failure
        };
        _session!.ProtectionSignal.TrySetResult();
    }

    private RunFailureKind? AcceptUsageLocked(TokenUsage? usage)
    {
        if (usage is null) return RunFailureKind.UsageUnavailable;
        try
        {
            if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.TotalTokens < 0
                || checked(usage.InputTokens + usage.OutputTokens) != usage.TotalTokens)
                return RunFailureKind.InvalidUsage;
        }
        catch (OverflowException) { return RunFailureKind.InvalidUsage; }
        TokenUsage totals;
        long count;
        try
        {
            totals = new(checked(_snapshot.ConfirmedUsage.InputTokens + usage.InputTokens),
                checked(_snapshot.ConfirmedUsage.OutputTokens + usage.OutputTokens), checked(_snapshot.ConfirmedUsage.TotalTokens + usage.TotalTokens));
            count = checked(_snapshot.CompletedRequests + 1);
        }
        catch (OverflowException) { return RunFailureKind.UsageOverflow; }
        _snapshot = _snapshot with { ConfirmedUsage = totals, CompletedRequests = count };
        return usage.TotalTokens == 0 ? RunFailureKind.NoProgress : null;
    }

    private void FinishLocked(Session session, RunEndReason reason)
    {
        var state = reason switch
        {
            RunEndReason.Failure => RunState.Faulted,
            RunEndReason.UserStop or RunEndReason.LifecycleCancellation => RunState.Stopped,
            _ => RunState.Completed
        };
        _snapshot = _snapshot with { State = state, EndReason = reason, IsBackingOff = false };
        session.Completion.TrySetResult(_snapshot);
    }

    private sealed class Session
    {
        public TaskCompletionSource<RunSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProtectionSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ResumeSignal { get; set; }
        public CancellationTokenSource AbortRequests { get; } = new();
        public bool RetryPending { get; set; }
        public TimeSpan? RetryAfter { get; set; }
    }

    private sealed record RequestOutcome(TokenUsage? Usage, RunFailureKind? Failure,
        RequestDelivery Delivery = RequestDelivery.Unknown, TimeSpan? RetryAfter = null);
}
