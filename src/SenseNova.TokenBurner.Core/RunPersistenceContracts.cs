namespace SenseNova.TokenBurner.Core;

public interface IRunStateStore
{
    Task<RunStateDocument?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RunStateDocument document, CancellationToken cancellationToken = default);
}

/// <summary>引擎的内部接缝：只有成功保存预留后才能执行请求。</summary>
internal interface IRunCheckpoint
{
    void EnsureCanStart();
    Task BeginAsync(RunSnapshot snapshot);
    Task SaveAsync(RunSnapshot snapshot);
}

public enum RecoveryState { NotInitialized, Ready, AwaitingConfirmation, NeedsReview, Corrupt, StorageFailed }

public sealed record RecoverySnapshot(RecoveryState State, RunStateDocument? Document)
{
    public int UnresolvedRequests => Document?.CurrentRun is { } run
        ? checked(run.Snapshot.InFlightRequests + run.Snapshot.UnknownUsageRequests) : 0;
}

/// <summary>无凭据、输入、回答和原始异常；历史包含异常退出的原始证据。</summary>
public sealed record RunRecord(Guid Id, DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc,
    RunSnapshot Snapshot, DateTimeOffset? ReviewedUtc = null);

public sealed record RunStateDocument(int SchemaVersion, RunRecord? CurrentRun,
    RunRecord[] History, ScheduleConfiguration? Plan, bool PlanRequested)
{
    public const int MaximumHistory = 20;
    public static RunStateDocument Empty => new(1, null, [], null, false);

    public void Validate()
    {
        if (SchemaVersion != 1 || History is null || History.Length > MaximumHistory
            || (PlanRequested && Plan is null)) throw new ArgumentException("运行记录结构无效。");
        Plan?.Validate();
        var ids = new HashSet<Guid>();
        foreach (var record in History.Concat(CurrentRun is null ? [] : new[] { CurrentRun }))
        {
            if (record is null || record.Id == Guid.Empty || !ids.Add(record.Id)
                || record.StartedUtc.Offset != TimeSpan.Zero || record.UpdatedUtc.Offset != TimeSpan.Zero
                || record.UpdatedUtc < record.StartedUtc
                || (record.ReviewedUtc is { } reviewed && (reviewed.Offset != TimeSpan.Zero || reviewed < record.StartedUtc)))
                throw new ArgumentException("运行记录时间或标识无效。");
            var run = record.Snapshot;
            if (run is null || run.Parameters is null || run.ConfirmedUsage is null)
                throw new ArgumentException("运行快照缺失。");
            run.Parameters.Validate();
            if (!Enum.IsDefined(run.State) || run.State == RunState.Idle
                || (run.Failure is { } failure && !Enum.IsDefined(failure))
                || (run.LastFailure is { } last && !Enum.IsDefined(last))
                || (run.EndReason is { } reason && !Enum.IsDefined(reason))
                || run.CompletedRequests < 0 || run.InFlightRequests < 0 || run.InFlightRequests > run.Parameters.MaximumConcurrency
                || run.UnknownUsageRequests < 0 || run.UnknownUsageRequests > int.MaxValue - run.InFlightRequests
                || run.ReservedTokens != run.InFlightRequests * run.Parameters.RequestTokenReservation
                || run.NotSentRequests < 0 || run.RejectedRequests < 0 || run.RetryAttempts < 0 || run.TotalRetryDelay < TimeSpan.Zero
                || run.ConfirmedUsage.InputTokens < 0 || run.ConfirmedUsage.OutputTokens < 0
                || run.ConfirmedUsage.TotalTokens < 0
                || run.ConfirmedUsage.InputTokens > long.MaxValue - run.ConfirmedUsage.OutputTokens
                || run.ConfirmedUsage.InputTokens + run.ConfirmedUsage.OutputTokens != run.ConfirmedUsage.TotalTokens
                || (IsTerminal(run.State) && run.InFlightRequests != 0))
                throw new ArgumentException("运行快照数值无效。");
        }
    }

    internal static bool IsTerminal(RunState state) => state is RunState.Completed or RunState.Stopped or RunState.Faulted;
}
