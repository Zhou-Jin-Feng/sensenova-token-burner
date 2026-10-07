using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class RunSchedulerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static RunParameters Run(long target = 100, string model = "mock-model")
        => new(model, target) { RequestTokenReservation = 100 };
    private static ScheduleConfiguration Plan(decimal hours = 5m, long target = 100, string model = "mock-model") => new(Run(target, model), hours);

    [TestMethod]
    public async Task ConstructionAndConfigurationNeverStartSchedule()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.UpdateConfiguration(Plan());
        time.Advance(TimeSpan.FromHours(20));
        Assert.AreEqual(ScheduleState.Disabled, scheduler.Snapshot.State);
        Assert.IsNull(scheduler.Snapshot.NextRunUtc);
        Assert.AreEqual(0, requests.StartedCount);
        Assert.AreEqual(0, time.ActiveTimers);
        await scheduler.DisableAsync();
    }

    [TestMethod]
    public void ConfigurationUsesDefaultFiveHoursAndSettingsCalibration()
    {
        var defaults = new ScheduleConfiguration(Run());
        Assert.AreEqual(TimeSpan.FromHours(5), defaults.Interval);
        var plan = ScheduleConfiguration.FromSettings(new() { Percentage = 50, IntervalHours = 2.5m });
        Assert.AreEqual(60_000_000L, plan.Run.TargetTokens);
        Assert.AreEqual(TimeSpan.FromMinutes(150), plan.Interval);
        Assert.AreEqual(TimeSpan.FromMilliseconds(3600), Plan(.001m).Interval);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("0.000000001")]
    [DataRow("79228162514264337593543950335")]
    public void InvalidIntervalsNeverStartRequests(string hours)
    {
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests));
        var parsed = decimal.Parse(hours, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => scheduler.Enable(Plan(parsed)));
        Assert.AreEqual(0, requests.StartedCount);
        Assert.AreEqual(ScheduleState.Disabled, scheduler.Snapshot.State);
    }

    [TestMethod]
    public void ZeroPercentDoesNotStartRunOrTimer()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        Assert.IsFalse(scheduler.Enable(Plan(target: 0)));
        Assert.AreEqual(0, requests.StartedCount);
        Assert.AreEqual(0L, scheduler.Snapshot.StartedRuns);
        Assert.IsNull(scheduler.Snapshot.NextRunUtc);
        Assert.AreEqual(0, time.ActiveTimers);
    }

    [TestMethod]
    public async Task CanceledEnablePropagatesCancellationToImmediateRun()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.IsTrue(scheduler.Enable(Plan(), cancellation.Token));
        await UntilAsync(() => scheduler.Snapshot.CurrentRun.State == RunState.Stopped);
        Assert.AreEqual(0, requests.StartedCount, "首轮收到取消后不得进入执行器。");
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task DefaultScheduleStartsImmediatelyThenAtAnchoredFiveHours()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        Assert.IsTrue(scheduler.Enable(new(Run())));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        Assert.AreEqual(DateTimeOffset.UnixEpoch, scheduler.Snapshot.AnchorUtc);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(5), scheduler.Snapshot.NextRunUtc);
        time.Advance(TimeSpan.FromHours(4));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromMinutes(1)));
        Assert.AreEqual(1, requests.StartedCount);
        time.Advance(TimeSpan.FromHours(1));
        var next = await requests.NextAsync();
        Assert.AreEqual(2L, scheduler.Snapshot.StartedRuns);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(10), scheduler.Snapshot.NextRunUtc);
        next.Complete(new(90, 10, 100));
        await UntilAsync(() => scheduler.Snapshot.LastRun?.State == RunState.Completed);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task CustomFractionalHourScheduleKeepsAnchorInsteadOfCompletionTime()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan(2.5m));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        time.Advance(TimeSpan.FromMinutes(150));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(5), scheduler.Snapshot.NextRunUtc);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task LateWakeSkipsAllMissedCyclesAndRunsOnlyAtFutureBoundary()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan());
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        time.Advance(TimeSpan.FromHours(16));
        await UntilAsync(() => scheduler.Snapshot.SkippedCycles == 3 && time.HasTimer(TimeSpan.FromMinutes(1)));
        Assert.AreEqual(1, requests.StartedCount);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(20), scheduler.Snapshot.NextRunUtc);
        time.Advance(TimeSpan.FromHours(4));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        Assert.AreEqual(2, requests.StartedCount);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    [DataRow(5, true)]
    [DataRow(31, false)]
    public async Task OrdinaryTimerLatenessHasBoundedTolerance(int seconds, bool shouldRun)
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan());
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        time.Advance(TimeSpan.FromHours(5) + TimeSpan.FromSeconds(seconds));
        if (shouldRun) (await requests.NextAsync()).Complete(new(90, 10, 100));
        else await UntilAsync(() => scheduler.Snapshot.SkippedCycles == 1);
        Assert.AreEqual(shouldRun ? 2 : 1, requests.StartedCount);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task ActivePausedRoundSkipsBoundaryAndDoesNotReplayAfterResume()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var scheduler = new RunScheduler(engine, time);
        scheduler.Enable(Plan(target: 200));
        var first = await requests.NextAsync();
        Assert.IsTrue(engine.Pause());
        first.Complete(new(90, 10, 100));
        await UntilAsync(() => engine.Snapshot.InFlightRequests == 0 && time.HasTimer(TimeSpan.FromMinutes(1)));
        time.Advance(TimeSpan.FromHours(5));
        await UntilAsync(() => scheduler.Snapshot.SkippedCycles == 1 && time.HasTimer(TimeSpan.FromMinutes(1)));
        Assert.AreEqual(1L, scheduler.Snapshot.StartedRuns);
        Assert.AreEqual(RunState.Paused, scheduler.Snapshot.CurrentRun.State);
        Assert.IsTrue(engine.Resume());
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await UntilAsync(() => scheduler.Snapshot.LastRun?.State == RunState.Completed);
        Assert.AreEqual(1L, scheduler.Snapshot.StartedRuns);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(10), scheduler.Snapshot.NextRunUtc);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task DisablePlanDoesNotCancelInFlightRunAndReenableIsBlockedUntilSettled()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var scheduler = new RunScheduler(engine, time);
        scheduler.Enable(Plan());
        var pending = await requests.NextAsync();
        var disabled = await scheduler.DisableAsync().WaitAsync(Deadline);
        Assert.AreEqual(ScheduleState.Disabled, disabled.State);
        Assert.IsNull(disabled.NextRunUtc);
        Assert.AreEqual(RunState.Running, engine.Snapshot.State);
        Assert.AreEqual(1, engine.Snapshot.InFlightRequests);
        Assert.ThrowsExactly<InvalidOperationException>(() => scheduler.Enable(Plan()));
        pending.Complete(new(90, 10, 100));
        await UntilAsync(() => engine.Snapshot.State == RunState.Completed);
        Assert.AreEqual(100L, scheduler.Snapshot.LastRun?.ConfirmedUsage.TotalTokens);
        Assert.AreEqual(0, time.ActiveTimers);
        time.Advance(TimeSpan.FromHours(12));
        Assert.AreEqual(1, requests.StartedCount);
        Assert.IsTrue(scheduler.Enable(Plan()));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(12), scheduler.Snapshot.AnchorUtc);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task StopCurrentRunKeepsPlanAndNextCycleStillRuns()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan());
        var pending = await requests.NextAsync();
        var stop = scheduler.StopCurrentRunAsync();
        pending.Complete(new(40, 10, 50));
        var result = await stop.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, result.State);
        await WaitingAsync(scheduler, time, expectedState: RunState.Stopped);
        Assert.AreEqual(ScheduleState.Enabled, scheduler.Snapshot.State);
        time.Advance(TimeSpan.FromHours(5));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await scheduler.DisableAsync().WaitAsync(Deadline);
        Assert.AreEqual(2, requests.StartedCount);
    }

    [TestMethod]
    public async Task UpdatedRunConfigurationAppliesOnlyToNextRound()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan());
        var old = await requests.NextAsync();
        scheduler.UpdateConfiguration(Plan(target: 200, model: "new-model"));
        Assert.AreEqual("mock-model", old.Parameters.ModelId);
        Assert.AreEqual(100L, scheduler.Snapshot.CurrentRun.Parameters?.TargetTokens);
        old.Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        time.Advance(TimeSpan.FromHours(5));
        var next = await requests.NextAsync();
        Assert.AreEqual("new-model", next.Parameters.ModelId);
        Assert.AreEqual(200L, next.Parameters.TargetTokens);
        var stopping = scheduler.StopCurrentRunAsync();
        next.Complete(new(90, 10, 100));
        await stopping.WaitAsync(Deadline);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task IntervalEditRecalculatesFromOriginalAnchorWithoutImmediateRun()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan());
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        time.Advance(TimeSpan.FromHours(3));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromMinutes(1)));
        scheduler.UpdateConfiguration(Plan(2));
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(4), scheduler.Snapshot.NextRunUtc);
        Assert.AreEqual(DateTimeOffset.UnixEpoch, scheduler.Snapshot.AnchorUtc);
        Assert.AreEqual(1, requests.StartedCount);
        await UntilAsync(() => time.HasTimer(TimeSpan.FromMinutes(1)));
        time.Advance(TimeSpan.FromHours(1));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(6), scheduler.Snapshot.NextRunUtc);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task ChangingTargetToZeroDisablesPlanButDoesNotCancelCurrentRound()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var scheduler = new RunScheduler(engine, time);
        scheduler.Enable(Plan());
        var pending = await requests.NextAsync();
        scheduler.UpdateConfiguration(Plan(target: 0));
        await scheduler.DisableAsync().WaitAsync(Deadline);
        Assert.AreEqual(RunState.Running, engine.Snapshot.State);
        pending.Complete(new(90, 10, 100));
        await UntilAsync(() => engine.Snapshot.State == RunState.Completed);
        Assert.AreEqual(0, time.ActiveTimers);
    }

    [TestMethod]
    public async Task ClockForwardJumpSkipsDueCyclesInsteadOfStartingImmediately()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan());
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        time.ShiftUtc(TimeSpan.FromHours(13));
        time.Advance(TimeSpan.FromMinutes(1));
        await UntilAsync(() => scheduler.Snapshot.SkippedCycles == 2);
        Assert.AreEqual(1, requests.StartedCount);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(15), scheduler.Snapshot.NextRunUtc);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task ClockRollbackNeverRecreatesAnAlreadyHandledCycle()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan());
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        time.Advance(TimeSpan.FromHours(5));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time, startedRuns: 2);
        time.ShiftUtc(TimeSpan.FromHours(-4));
        time.Advance(TimeSpan.FromMinutes(1));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromMinutes(1)));
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(10), scheduler.Snapshot.NextRunUtc);
        Assert.AreEqual(2, requests.StartedCount);
        time.Advance(TimeSpan.FromHours(4));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromMinutes(1)));
        Assert.AreEqual(2, requests.StartedCount);
        time.Advance(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(59));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        Assert.AreEqual(3, requests.StartedCount);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task ChangingTimeZoneDoesNotChangeUtcSchedule()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan());
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        time.SetLocalZone(TimeZoneInfo.CreateCustomTimeZone("Test+8", TimeSpan.FromHours(8), "Test+8", "Test+8"));
        time.Advance(TimeSpan.FromMinutes(1));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromMinutes(1)));
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(5), scheduler.Snapshot.NextRunUtc);
        time.SetLocalZone(TimeZoneInfo.CreateCustomTimeZone("Test-7", TimeSpan.FromHours(-7), "Test-7", "Test-7"));
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(5), scheduler.Snapshot.NextRunUtc);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task RateOrQuotaProtectionImmediatelyClosesScheduleWithoutWaitingNextTick()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var scheduler = new RunScheduler(engine, time);
        scheduler.Enable(Plan());
        (await requests.NextAsync()).Fail(new SenseNovaApiException(ApiFailureKind.RateOrQuotaLimit, "private-error"));
        await UntilAsync(() => scheduler.Snapshot.State == ScheduleState.Blocked);
        Assert.AreEqual(ScheduleStopReason.RunProtection, scheduler.Snapshot.StopReason);
        Assert.IsNull(scheduler.Snapshot.NextRunUtc);
        Assert.AreEqual(RunState.AwaitingReview, engine.Snapshot.State);
        Assert.IsFalse(engine.Resume());
        time.Advance(TimeSpan.FromHours(20));
        Assert.AreEqual(1, requests.StartedCount);
        await scheduler.StopCurrentRunAsync().WaitAsync(Deadline);
        await scheduler.DisableAsync().WaitAsync(Deadline);
        Assert.ThrowsExactly<InvalidOperationException>(() => scheduler.Enable(Plan()));
        Assert.IsTrue(engine.AcknowledgeReview());
        Assert.AreEqual(ScheduleState.Blocked, scheduler.Snapshot.State);
        Assert.IsTrue(scheduler.Enable(Plan()));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task KnownFailureClosesPlanWhileOtherInFlightRequestsAreStillSettling()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var scheduler = new RunScheduler(engine, time);
        scheduler.Enable(new(new("mock-model", 1000) { MaximumConcurrency = 3, RequestTokenReservation = 100 }));
        var first = await requests.NextAsync();
        var second = await requests.NextAsync();
        var third = await requests.NextAsync();
        first.Fail(new SenseNovaApiException(ApiFailureKind.Authentication, "private-error", RequestDelivery.Rejected));
        await UntilAsync(() => scheduler.Snapshot.State == ScheduleState.Blocked);
        Assert.AreEqual(ScheduleStopReason.RunFailure, scheduler.Snapshot.StopReason);
        Assert.AreEqual(2, engine.Snapshot.InFlightRequests);
        second.Complete(new(90, 10, 100));
        third.Complete(new(90, 10, 100));
        await UntilAsync(() => engine.Snapshot.State == RunState.Faulted);
        Assert.AreEqual(200L, scheduler.Snapshot.LastRun?.ConfirmedUsage.TotalTokens);
        await scheduler.DisableAsync().WaitAsync(Deadline);
        Assert.AreEqual(3, requests.StartedCount);
    }

    [TestMethod]
    public async Task StopTimeoutClosesPlanAndDoesNotAllowAnotherRun()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor(honorCancellation: false);
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var scheduler = new RunScheduler(engine, time);
        scheduler.Enable(Plan());
        var pending = await requests.NextAsync();
        var stopping = scheduler.StopCurrentRunAsync();
        await UntilAsync(() => time.HasTimer(TimeSpan.FromSeconds(15)));
        time.Advance(TimeSpan.FromSeconds(15));
        Assert.AreEqual(RunState.Stopping, (await stopping.WaitAsync(Deadline)).State);
        await UntilAsync(() => scheduler.Snapshot.State == ScheduleState.Blocked);
        Assert.ThrowsExactly<InvalidOperationException>(() => scheduler.Enable(Plan()));
        pending.Complete(new(90, 10, 100));
        await UntilAsync(() => engine.Snapshot.State == RunState.Faulted);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task ConcurrentEnablesAcceptExactlyOnePlan()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            try { return scheduler.Enable(Plan()); }
            catch (InvalidOperationException) { return false; }
        }));
        var accepted = await Task.WhenAll(attempts).WaitAsync(Deadline);
        Assert.AreEqual(1, accepted.Count(value => value));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await scheduler.DisableAsync().WaitAsync(Deadline);
        Assert.AreEqual(1, requests.StartedCount);
    }

    [TestMethod]
    public async Task ExistingEngineRunPreventsScheduleEnableWithoutChangingEngine()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(Run());
        var pending = await requests.NextAsync();
        var scheduler = new RunScheduler(engine);
        Assert.ThrowsExactly<InvalidOperationException>(() => scheduler.Enable(Plan()));
        Assert.AreEqual(ScheduleState.Disabled, scheduler.Snapshot.State);
        Assert.AreEqual(1, requests.StartedCount);
        pending.Complete(new(90, 10, 100));
        await run.WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task FutureParameterPrecheckFailureBlocksInsteadOfThrowingRawErrorFromBackgroundTask()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ModelRestrictedExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan());
        (await requests.Requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        scheduler.UpdateConfiguration(Plan(model: "unavailable-model"));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromMinutes(1)));
        time.Advance(TimeSpan.FromHours(5));
        await UntilAsync(() => scheduler.Snapshot.State == ScheduleState.Blocked);
        Assert.AreEqual(ScheduleStopReason.StartRejected, scheduler.Snapshot.StopReason);
        Assert.IsFalse(scheduler.Snapshot.ToString().Contains("private-model-error", StringComparison.Ordinal));
        Assert.AreEqual(1, requests.Requests.StartedCount);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task ShutdownClosesTimerAndStopsRunButDoesNotPretendUnresponsiveRequestHasFinished()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor(honorCancellation: false);
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var scheduler = new RunScheduler(engine, time);
        scheduler.Enable(Plan());
        var pending = await requests.NextAsync();
        var shutdown = scheduler.ShutdownAsync();
        await UntilAsync(() => time.HasTimer(TimeSpan.FromSeconds(15)));
        time.Advance(TimeSpan.FromSeconds(15));
        var result = await shutdown.WaitAsync(Deadline);
        Assert.AreEqual(ScheduleState.Shutdown, scheduler.Snapshot.State);
        Assert.AreEqual(RunState.Stopping, result.State);
        Assert.AreEqual(1, result.InFlightRequests);
        Assert.ThrowsExactly<InvalidOperationException>(() => scheduler.Enable(Plan()));
        pending.Complete(new(90, 10, 100));
        await UntilAsync(() => engine.Snapshot.State == RunState.Faulted);
        await UntilAsync(() => time.ActiveTimers == 0);
    }

    [TestMethod]
    public async Task VeryLongIntervalUsesBoundedTimerAndDisableLeavesNoScheduleTimers()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan(2400));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddDays(100), scheduler.Snapshot.NextRunUtc);
        await scheduler.DisableAsync().WaitAsync(Deadline);
        Assert.AreEqual(0, time.ActiveTimers);
    }

    [TestMethod]
    public async Task DateRangeExhaustionBlocksWithoutLaunchingAdditionalRequest()
    {
        var time = new ManualRunTimeProvider();
        time.ShiftUtc(DateTimeOffset.MaxValue - DateTimeOffset.UnixEpoch - TimeSpan.FromHours(6));
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan());
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        time.Advance(TimeSpan.FromHours(5));
        await UntilAsync(() => scheduler.Snapshot.State == ScheduleState.Blocked);
        Assert.AreEqual(ScheduleStopReason.ClockRangeExceeded, scheduler.Snapshot.StopReason);
        Assert.AreEqual(1, requests.StartedCount);
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public void InvalidFutureDateNeverStartsFirstRound()
    {
        var time = new ManualRunTimeProvider();
        time.ShiftUtc(DateTimeOffset.MaxValue - DateTimeOffset.UnixEpoch - TimeSpan.FromHours(4));
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => scheduler.Enable(Plan()));
        Assert.AreEqual(0, requests.StartedCount);
        Assert.AreEqual(ScheduleState.Disabled, scheduler.Snapshot.State);
    }

    [TestMethod]
    public async Task UnexpectedExecutorPrecheckErrorStopsScheduleWithoutExposingText()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ModelRestrictedExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        scheduler.Enable(Plan());
        (await requests.Requests.NextAsync()).Complete(new(90, 10, 100));
        await WaitingAsync(scheduler, time);
        scheduler.UpdateConfiguration(Plan(model: "internal-error"));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromMinutes(1)));
        time.Advance(TimeSpan.FromHours(5));
        await UntilAsync(() => scheduler.Snapshot.State == ScheduleState.Blocked);
        Assert.AreEqual(ScheduleStopReason.StartRejected, scheduler.Snapshot.StopReason);
        Assert.AreEqual(1, requests.Requests.StartedCount);
        Assert.IsFalse(scheduler.Snapshot.ToString().Contains("private-executor-error", StringComparison.Ordinal));
        await scheduler.DisableAsync().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task OldDisabledPlanCannotOverwriteReenabledPlan()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var scheduler = new RunScheduler(new(requests, timeProvider: time), time);
        for (var cycle = 0; cycle < 8; cycle++)
        {
            scheduler.Enable(Plan(model: "model-" + cycle));
            var pending = await requests.NextAsync();
            Assert.AreEqual("model-" + cycle, pending.Parameters.ModelId);
            pending.Complete(new(90, 10, 100));
            await WaitingAsync(scheduler, time);
            await scheduler.DisableAsync().WaitAsync(Deadline);
            time.Advance(TimeSpan.FromHours(1));
            Assert.AreEqual(ScheduleState.Disabled, scheduler.Snapshot.State);
            Assert.AreEqual(0, time.ActiveTimers);
        }
        Assert.AreEqual(8, requests.StartedCount);
    }

    [TestMethod]
    public async Task MockRequestsRunAcrossSeveralCyclesWithoutExtraProcessOrNetwork()
    {
        var time = new ManualRunTimeProvider();
        var scheduler = new RunScheduler(new(new MockRunRequestExecutor(TimeSpan.Zero), timeProvider: time), time);
        scheduler.Enable(new(new("mock-model", 200)));
        await WaitingAsync(scheduler, time);
        for (var cycle = 2; cycle <= 4; cycle++)
        {
            time.Advance(TimeSpan.FromHours(5));
            await WaitingAsync(scheduler, time, cycle);
            Assert.AreEqual(272L, scheduler.Snapshot.LastRun?.ConfirmedUsage.TotalTokens);
        }
        Assert.AreEqual(0L, scheduler.Snapshot.SkippedCycles);
        await scheduler.DisableAsync().WaitAsync(Deadline);
        Assert.AreEqual(0, time.ActiveTimers);
    }

    private sealed class ModelRestrictedExecutor : IRunRequestExecutor
    {
        public ControlledRunExecutor Requests { get; } = new();
        public void ValidateParameters(RunParameters parameters)
        {
            if (parameters.ModelId == "internal-error") throw new IOException("private-executor-error");
            if (parameters.ModelId != "mock-model") throw new ArgumentException("private-model-error");
        }
        public Task<TokenUsage> ExecuteAsync(RunParameters parameters, CancellationToken cancellationToken)
            => Requests.ExecuteAsync(parameters, cancellationToken);
    }

    private static Task WaitingAsync(RunScheduler scheduler, ManualRunTimeProvider time, int startedRuns = 1, RunState expectedState = RunState.Completed)
        => UntilAsync(() => scheduler.Snapshot.StartedRuns == startedRuns && scheduler.Snapshot.CurrentRun.State == expectedState
            && scheduler.Snapshot.LastRun?.State == expectedState && time.HasTimer(TimeSpan.FromMinutes(1)));

    private static async Task UntilAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.IsLessThan(Deadline, elapsed.Elapsed, "未在期限内达到预期状态。");
            await Task.Delay(1);
        }
    }
}
