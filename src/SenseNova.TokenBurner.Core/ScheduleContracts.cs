namespace SenseNova.TokenBurner.Core;

/// <summary>计划配置不包含凭据，运行配置由下一轮取不可变快照。</summary>
public sealed record ScheduleConfiguration(RunParameters Run, decimal IntervalHours = RunDefaults.IntervalHours)
{
    public TimeSpan Interval
    {
        get
        {
            // decimal 保持小时输入精度；只接受可表达且不小于一秒的间隔，避免忙循环。
            if (IntervalHours <= 0 || IntervalHours > (decimal)TimeSpan.MaxValue.TotalHours)
                throw new ArgumentException("计划间隔必须为有效的正数小时。");
            var ticks = decimal.Round(IntervalHours * TimeSpan.TicksPerHour, 0, MidpointRounding.AwayFromZero);
            if (ticks < TimeSpan.TicksPerSecond || ticks > long.MaxValue)
                throw new ArgumentException("计划间隔必须至少为一秒且可表示。");
            return TimeSpan.FromTicks((long)ticks);
        }
    }

    public static ScheduleConfiguration FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return new(RunParameters.FromSettings(settings), settings.IntervalHours);
    }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Run);
        Run.Validate();
        _ = Interval;
    }
}

public enum ScheduleState { Disabled, Enabled, Blocked, Shutdown }
public enum ScheduleStopReason { UserDisabled, RunProtection, RunFailure, StartRejected, ClockRangeExceeded, Shutdown }

public sealed record ScheduleSnapshot(
    ScheduleState State,
    ScheduleConfiguration? Configuration,
    DateTimeOffset? AnchorUtc,
    DateTimeOffset? NextRunUtc,
    long StartedRuns,
    long SkippedCycles,
    RunSnapshot? LastRun,
    ScheduleStopReason? StopReason)
{
    public RunSnapshot CurrentRun { get; init; } = RunSnapshot.Idle;
    public static ScheduleSnapshot Disabled { get; } = new(ScheduleState.Disabled, null, null, null, 0, 0, null, null);
}
