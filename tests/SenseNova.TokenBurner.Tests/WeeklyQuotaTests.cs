using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class WeeklyQuotaTests
{
    private static readonly TimeZoneInfo BeijingTz = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    [TestMethod]
    public void CycleWindowCalculatesCorrectlyForMondayReset()
    {
        // 假设参考时间是 2026-10-08 14:00 (周四)
        var refTimeUtc = new DateTimeOffset(2026, 10, 8, 6, 0, 0, TimeSpan.Zero); // 北京时间 14:00
        var (startUtc, nextUtc) = WeeklyQuotaCalculator.GetCycleWindow(DayOfWeek.Monday, TimeSpan.Zero, refTimeUtc, BeijingTz);

        // 周一 00:00 北京时间 = 周日 16:00 UTC (2026-10-05 00:00:00 +08:00)
        var expectedStartUtc = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        var expectedNextUtc = expectedStartUtc.AddDays(7);

        Assert.AreEqual(expectedStartUtc, startUtc);
        Assert.AreEqual(expectedNextUtc, nextUtc);
    }

    [TestMethod]
    public void CycleWindowRollsOverExactlyAtResetTime()
    {
        // 周一 00:00:00 整点
        var exactMondayUtc = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        var (startUtc, nextUtc) = WeeklyQuotaCalculator.GetCycleWindow(DayOfWeek.Monday, TimeSpan.Zero, exactMondayUtc, BeijingTz);
        Assert.AreEqual(exactMondayUtc, startUtc);
        Assert.AreEqual(exactMondayUtc.AddDays(7), nextUtc);

        // 周日 23:59:59 (重置前一秒)
        var beforeResetUtc = exactMondayUtc.AddSeconds(-1);
        var (prevStartUtc, prevNextUtc) = WeeklyQuotaCalculator.GetCycleWindow(DayOfWeek.Monday, TimeSpan.Zero, beforeResetUtc, BeijingTz);
        Assert.AreEqual(exactMondayUtc.AddDays(-7), prevStartUtc);
        Assert.AreEqual(exactMondayUtc, prevNextUtc);
    }

    [TestMethod]
    public void CycleWindowHandlesSundayReset()
    {
        // 重置设为周日 12:00
        var sundayResetTime = TimeSpan.FromHours(12);
        // 参考时间为周日 10:00 (尚未到重置时间)
        var sundayBeforeUtc = new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        var (startBefore, nextBefore) = WeeklyQuotaCalculator.GetCycleWindow(DayOfWeek.Sunday, sundayResetTime, sundayBeforeUtc, BeijingTz);
        var expectedPriorSundayUtc = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        Assert.AreEqual(expectedPriorSundayUtc, startBefore);
        Assert.AreEqual(expectedPriorSundayUtc.AddDays(7), nextBefore);

        // 参考时间为周日 13:00 (已过重置时间)
        var sundayAfterUtc = new DateTimeOffset(2026, 10, 11, 13, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        var (startAfter, nextAfter) = WeeklyQuotaCalculator.GetCycleWindow(DayOfWeek.Sunday, sundayResetTime, sundayAfterUtc, BeijingTz);
        Assert.AreEqual(expectedPriorSundayUtc.AddDays(7), startAfter);
        Assert.AreEqual(expectedPriorSundayUtc.AddDays(14), nextAfter);
    }

    [TestMethod]
    public void CalculateStatusAggregatesOnlyCurrentCycleAndAppliesManualAdjustment()
    {
        var config = new TaskConfiguration
        {
            WeeklyResetDay = DayOfWeek.Monday,
            WeeklyResetTime = TimeSpan.Zero,
            WeeklyQuotaPoints = 600_000,
            WeeklyQuotaManualAdjustment = 10_000
        };

        var nowUtc = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var (cycleStartUtc, _) = WeeklyQuotaCalculator.GetCycleWindow(DayOfWeek.Monday, TimeSpan.Zero, nowUtc, BeijingTz);

        // 历史记录 1：上周的记录 (不应计入)
        var lastWeekSnapshot = new RunSnapshot(RunState.Completed, new("sensenova-6.8-flash-lite", 120_000_000), new(100_000_000, 1_000, 100_001_000), 10, 0, 0, null);
        var lastWeekRecord = new RunRecord(Guid.NewGuid(), cycleStartUtc.AddDays(-2), cycleStartUtc.AddDays(-2).AddHours(1), lastWeekSnapshot);

        // 历史记录 2：本周已完成的记录 (应计入 1.2 亿 tokens = 6 万分)
        var thisWeekSnapshot = new RunSnapshot(RunState.Completed, new("sensenova-6.8-flash-lite", 120_000_000), new(120_000_000, 1_000, 120_001_000), 10, 0, 0, null);
        var thisWeekRecord = new RunRecord(Guid.NewGuid(), cycleStartUtc.AddHours(2), cycleStartUtc.AddHours(3), thisWeekSnapshot);

        // 活动记录：当前正在跑的轮次 (应计入 6000 万 tokens = 3 万分)
        var activeSnapshot = new RunSnapshot(RunState.Running, new("sensenova-6.8-flash-lite", 60_000_000), new(60_000_000, 1_000, 60_001_000), 5, 0, 0, null);
        var activeStartedUtc = cycleStartUtc.AddHours(10);

        var status = WeeklyQuotaCalculator.CalculateStatus(
            "测试组",
            config,
            [lastWeekRecord, thisWeekRecord],
            activeSnapshot,
            activeStartedUtc,
            nowUtc,
            BeijingTz);

        // 本周 tokens = 120,001,000 + 60,001,000 = 180,002,000
        Assert.AreEqual(180_002_000L, status.ConsumedTokens);
        // points = 180,002,000 / 2,000 = 90,001; 加上手动调整 10,000 = 100,001 分
        Assert.AreEqual(90_001L + 10_000L, status.ConsumedPoints);
        Assert.AreEqual(600_000L, status.QuotaPoints);
        Assert.AreEqual(600_000L - 100_001L, status.RemainingPoints);
        Assert.IsFalse(status.IsQuotaExceeded);
        Assert.IsTrue(status.WouldExceedWith(1_000_000_000L)); // 10 亿 tokens 相当于 50 万分，加起来超过 60 万分
    }

    [TestMethod]
    public void SchedulePageAndOverviewPageSupportWeeklyQuotaControls()
    {
        var config = new TaskConfiguration
        {
            WeeklyResetDay = DayOfWeek.Tuesday,
            WeeklyResetTime = TimeSpan.FromHours(8),
            WeeklyQuotaPoints = 500_000,
            WeeklyQuotaAutoStop = true,
            WeeklyQuotaManualAdjustment = 20_000
        };

        var nowUtc = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var status = WeeklyQuotaCalculator.CalculateStatus("测试组", config, [], null, null, nowUtc, BeijingTz);

        // 1. OverviewPage 卡片赋值
        using var overview = new SenseNova.TokenBurner.Desktop.Ui.OverviewPage();
        overview.WeeklyQuota.SetData(status, nowUtc);
        Assert.IsTrue(overview.WeeklyQuota.HeightForWidth(500) > 0);

        // 2. SchedulePage 加载与脏状态
        using var schedule = new SenseNova.TokenBurner.Desktop.Ui.SchedulePage();
        schedule.LoadWeekly(config, status, nowUtc);
        Assert.IsFalse(schedule.IsWeeklyDirty);
        Assert.IsFalse(schedule.SaveWeeklySettings.Enabled);
        Assert.AreEqual(1, schedule.WeeklyResetDay.SelectedIndex); // 周二 = 1
        Assert.AreEqual(8m, schedule.WeeklyResetHour.Value);
        Assert.AreEqual(500_000m, schedule.WeeklyQuotaLimit.Value);
        Assert.AreEqual(20_000m, schedule.WeeklyManualAdjustment.Value);
        Assert.IsTrue(schedule.WeeklyAutoStop.Checked);

        // 3. 修改后触发脏标记
        var dirtyFired = false;
        schedule.WeeklyDirtyChanged += () => dirtyFired = true;
        schedule.WeeklyQuotaLimit.Value = 800_000m;
        Assert.IsTrue(dirtyFired);
        Assert.IsTrue(schedule.IsWeeklyDirty);
        Assert.IsTrue(schedule.SaveWeeklySettings.Enabled);
    }

}
