namespace SenseNova.TokenBurner.Core;

public sealed record WeeklyQuotaStatus(
    string QuotaGroup,
    DateTimeOffset CycleStartUtc,
    DateTimeOffset NextResetUtc,
    long ConsumedTokens,
    long ConsumedPoints,
    long QuotaPoints,
    long RemainingPoints,
    bool AutoStopEnabled,
    bool IsQuotaExceeded,
    long TargetTokens)
{
    public long QuotaTokens => ConsumptionPolicy.PointsToTokens(QuotaPoints);
    public long RemainingTokens => Math.Max(0, QuotaTokens - ConsumedTokens);
    public double ProgressRatio => QuotaPoints <= 0 ? 0 : Math.Clamp((double)ConsumedPoints / QuotaPoints, 0, 1);
    public bool WouldExceedWith(long additionalTokens) => ConsumedPoints + ConsumptionPolicy.TokensToPoints(additionalTokens) > QuotaPoints;
    public TimeSpan TimeUntilReset(DateTimeOffset nowUtc) => NextResetUtc > nowUtc ? NextResetUtc - nowUtc : TimeSpan.Zero;

    public string FormatResetCountdown(DateTimeOffset nowUtc)
    {
        var diff = TimeUntilReset(nowUtc);
        if (diff.TotalHours < 1) return $"约 {Math.Max(1, (int)diff.TotalMinutes)} 分钟后";
        if (diff.TotalDays < 1) return $"约 {(int)diff.TotalHours} 小时后";
        return $"约 {(int)diff.TotalDays} 天 {diff.Hours} 小时后";
    }
}

public static class WeeklyQuotaCalculator
{
    /// <summary>
    /// 计算指定参考时间所处的周周期起止时间（UTC）。
    /// 用户配置的周几与具体时间按本地时区解释。
    /// </summary>
    public static (DateTimeOffset CycleStartUtc, DateTimeOffset NextResetUtc) GetCycleWindow(
        DayOfWeek resetDay,
        TimeSpan resetTime,
        DateTimeOffset referenceTimeUtc,
        TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTime(referenceTimeUtc, tz);

        var currentDay = localNow.DayOfWeek;
        var diff = ((int)currentDay - (int)resetDay + 7) % 7;
        if (diff == 0 && localNow.TimeOfDay < resetTime)
        {
            diff = 7;
        }

        var cycleStartLocal = localNow.Date.AddDays(-diff).Add(resetTime);
        var nextResetLocal = cycleStartLocal.AddDays(7);

        var cycleStartUtc = TimeZoneInfo.ConvertTimeToUtc(cycleStartLocal, tz);
        var nextResetUtc = TimeZoneInfo.ConvertTimeToUtc(nextResetLocal, tz);
        return (cycleStartUtc, nextResetUtc);
    }

    /// <summary>
    /// 计算指定额度组在当前周期内的累计消耗与状态。
    /// </summary>
    public static WeeklyQuotaStatus CalculateStatus(
        string quotaGroup,
        TaskConfiguration configuration,
        IEnumerable<RunRecord> historicalRecords,
        RunSnapshot? activeSnapshot,
        DateTimeOffset? activeStartedUtc,
        DateTimeOffset nowUtc,
        TimeZoneInfo? timeZone = null)
    {
        var (cycleStartUtc, nextResetUtc) = GetCycleWindow(
            configuration.WeeklyResetDay,
            configuration.WeeklyResetTime,
            nowUtc,
            timeZone);

        long tokens = 0;
        if (historicalRecords is not null)
        {
            foreach (var record in historicalRecords)
            {
                if (record is not null && record.UpdatedUtc >= cycleStartUtc && record.Snapshot?.ConfirmedUsage is { } usage)
                {
                    tokens += usage.TotalTokens;
                }
            }
        }

        if (activeSnapshot?.ConfirmedUsage is { } activeUsage && activeStartedUtc is { } started && started >= cycleStartUtc)
        {
            tokens += activeUsage.TotalTokens;
        }

        var basePoints = ConsumptionPolicy.TokensToPoints(tokens);
        var totalPoints = Math.Max(0, basePoints + configuration.WeeklyQuotaManualAdjustment);
        var limitPoints = configuration.WeeklyQuotaPoints;
        var remainingPoints = Math.Max(0, limitPoints - totalPoints);
        var isExceeded = totalPoints >= limitPoints;

        return new WeeklyQuotaStatus(
            quotaGroup,
            cycleStartUtc,
            nextResetUtc,
            tokens,
            totalPoints,
            limitPoints,
            remainingPoints,
            configuration.WeeklyQuotaAutoStop,
            isExceeded,
            configuration.TargetTokens);
    }
}
