namespace SenseNova.TokenBurner.Core;

/// <summary>本轮不可变参数；不保存凭据，运行中配置变更不影响该快照。</summary>
public sealed record RunParameters(string ModelId, long TargetTokens)
{
    public int MaximumConcurrency { get; init; } = 1;
    public long RequestTokenReservation { get; init; }

    public static RunParameters FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return new(settings.ModelId, ConsumptionPolicy.GetTargetTokens(settings.Percentage));
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 200)
            throw new ArgumentException("运行模型标识无效。", nameof(ModelId));
        ArgumentOutOfRangeException.ThrowIfNegative(TargetTokens);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TargetTokens,
            ConsumptionPolicy.GetTargetTokens(ConsumptionPolicy.MaximumPercentage));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumConcurrency, RequestBaseline.Concurrency);
        ArgumentOutOfRangeException.ThrowIfNegative(RequestTokenReservation);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RequestTokenReservation,
            ConsumptionPolicy.GetTargetTokens(ConsumptionPolicy.MaximumPercentage));
        if (MaximumConcurrency > 1 && RequestTokenReservation == 0)
            throw new ArgumentException("并发运行必须提供每请求 token 预留量。");
    }
}

/// <summary>一个请求的执行接缝；实现必须响应生命周期取消并返回最终 usage。</summary>
public interface IRunRequestExecutor
{
    void ValidateParameters(RunParameters parameters) { }
    Task<TokenUsage> ExecuteAsync(RunParameters parameters, CancellationToken cancellationToken);
}

public enum RunState { Idle, Running, Paused, Stopping, Completed, Stopped, Faulted, AwaitingReview }

// 只保留安全分类，不保留执行器异常正文或服务端响应。
public enum RunFailureKind
{
    RequestFailed, Canceled, UsageUnavailable, InvalidUsage, UsageOverflow, NoProgress, BudgetEstimateExceeded,
    Authentication, InvalidRequest, ModelUnavailable, RateOrQuotaLimit, Transient, Protocol, Network,
    RequestTimeout, StopTimeout, RetryLimitReached, StorageFailure
}

public enum RunEndReason { TargetReached, BudgetRemainder, UserStop, LifecycleCancellation, Failure }

public sealed record RunSnapshot(
    RunState State,
    RunParameters? Parameters,
    TokenUsage ConfirmedUsage,
    long CompletedRequests,
    int InFlightRequests,
    int UnknownUsageRequests,
    RunFailureKind? Failure)
{
    public long ReservedTokens { get; init; }
    public RunEndReason? EndReason { get; init; }
    public RunFailureKind? LastFailure { get; init; }
    public bool ReviewRequired { get; init; }
    public bool StopWaitExpired { get; init; }
    public bool IsBackingOff { get; init; }
    public int RetryAttempts { get; init; }
    public long NotSentRequests { get; init; }
    public long RejectedRequests { get; init; }
    public TimeSpan TotalRetryDelay { get; init; }
    public static RunSnapshot Idle { get; } = new(RunState.Idle, null, new(0, 0, 0), 0, 0, 0, null);
}
