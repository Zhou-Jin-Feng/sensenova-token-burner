using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;

namespace SenseNova.TokenBurner.Desktop.Ui;

/// <summary>列表一行所需的只读快照，每次刷新重新生成，不持有任务对象。</summary>
internal sealed record TaskRowView(
    Guid Id, string Name, string Group, string Status, TaskStatusKind Kind, long Confirmed, long Target, long Reserved,
    bool Enabled, string? NextRun, string? Issue)
{
    public double Fraction => Target <= 0 ? 0 : Math.Clamp((double)Confirmed / Target, 0, 1);
    public double ReservedFraction => Target <= 0 ? 0 : Math.Clamp((double)Reserved / Target, 0, 1);

    public static TaskRowView From(TaskRuntime row)
    {
        var run = row.Snapshot;
        var target = run.Parameters?.TargetTokens ?? row.Configuration.TargetTokens;
        var kind = row.StatusKind;
        var status = row.ShortStatus;
        if (!row.Configuration.Enabled && !row.Active) { kind = TaskStatusKind.Idle; status = "已停用"; }
        string? next = row.Session.Schedule.NextRunUtc is { } nextRun ? Format.When(nextRun)
            : row.Configuration.PlanRequested ? "待启用" : null;
        string? issue = !string.IsNullOrEmpty(row.Message) ? row.Message
            : run.Failure is { } failure ? FailureText(failure)
            : row.Session.Recovery.State == RecoveryState.NeedsReview ? "有请求结果未知，需要先核查官方用量。" : null;
        return new(row.Configuration.Id, row.Configuration.DisplayName, row.Configuration.QuotaGroup, status, kind,
            run.ConfirmedUsage.TotalTokens, target, run.ReservedTokens, row.Configuration.Enabled, next, issue);
    }

    public static string FailureText(RunFailureKind failure) => failure switch
    {
        RunFailureKind.Authentication => "认证失败：请检查 API key 是否有效。",
        RunFailureKind.RateOrQuotaLimit => "触发限流或额度限制，已停止并等待核查。",
        RunFailureKind.ModelUnavailable => "活动模型不可用，请核查模型权限。",
        RunFailureKind.InvalidRequest => "请求参数被拒绝。",
        RunFailureKind.Network => "网络连接失败。",
        RunFailureKind.RequestTimeout => "请求超时。",
        RunFailureKind.UsageUnavailable => "有请求没有返回用量，需要核查。",
        RunFailureKind.StorageFailure => "本地记录无法保存，已阻止继续。",
        RunFailureKind.RetryLimitReached => "重试次数已用完。",
        RunFailureKind.BudgetEstimateExceeded => "单笔用量超出本地预算估计，已停止。",
        _ => $"运行异常：{failure}"
    };
}

/// <summary>任务的确认速率采样（仅内存，最近 30 分钟），用于速率、预计完成和每分钟柱状图。</summary>
internal sealed class BurnTracker
{
    private readonly Dictionary<Guid, List<(DateTimeOffset Time, long Tokens)>> _samples = [];
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    public void Observe(Guid id, long confirmed, bool burning, DateTimeOffset now)
    {
        if (!_samples.TryGetValue(id, out var list)) _samples[id] = list = [];
        if (list.Count > 0 && confirmed < list[^1].Tokens) list.Clear(); // 重置后从头记录
        if (!burning && list.Count > 0 && list[^1].Tokens == confirmed && now - list[^1].Time < TimeSpan.FromSeconds(30)) return;
        if (list.Count == 0 || list[^1].Tokens != confirmed || now - list[^1].Time >= TimeSpan.FromSeconds(5))
            list.Add((now, confirmed));
        while (list.Count > 2 && now - list[0].Time > Window) list.RemoveAt(0);
    }

    public void Forget(Guid id) => _samples.Remove(id);

    /// <summary>最近 5 分钟（不足则用已有样本）的平均确认速率，tokens/分钟；样本不足返回 0。</summary>
    public double Rate(Guid id, DateTimeOffset now)
    {
        if (!_samples.TryGetValue(id, out var list) || list.Count < 2) return 0;
        var recent = list.Where(sample => now - sample.Time <= TimeSpan.FromMinutes(5)).ToList();
        if (recent.Count < 2) recent = list.TakeLast(2).ToList();
        var span = recent[^1].Time - recent[0].Time;
        var delta = recent[^1].Tokens - recent[0].Tokens;
        if (span < TimeSpan.FromSeconds(20) || delta <= 0) return 0;
        // 最后一次增长之后若已长时间没有新确认，速率按到当前时间摊薄。
        var elapsed = Math.Max(span.TotalMinutes, (now - recent[0].Time).TotalMinutes);
        return delta / elapsed;
    }

    /// <summary>最近 buckets 分钟每分钟新增确认 tokens（最后一个为当前分钟）。</summary>
    public long[] PerMinute(Guid id, DateTimeOffset now, int buckets = 30)
    {
        var result = new long[buckets];
        if (!_samples.TryGetValue(id, out var list) || list.Count < 2) return result;
        var currentMinute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        for (var i = 1; i < list.Count; i++)
        {
            var delta = list[i].Tokens - list[i - 1].Tokens;
            if (delta <= 0) continue;
            var minutesAgo = (int)Math.Floor((currentMinute - new DateTimeOffset(list[i].Time.Year, list[i].Time.Month, list[i].Time.Day,
                list[i].Time.Hour, list[i].Time.Minute, 0, list[i].Time.Offset)).TotalMinutes);
            var index = buckets - 1 - minutesAgo;
            if (index >= 0 && index < buckets) result[index] += delta;
        }
        return result;
    }
}
