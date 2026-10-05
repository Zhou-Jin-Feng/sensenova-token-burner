using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class RunProtectionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static RunParameters Parameters(int concurrency = 1) => new("mock-model", 1000)
        { MaximumConcurrency = concurrency, RequestTokenReservation = 100 };
    private static SenseNovaApiException Failure(ApiFailureKind kind, RequestDelivery delivery = RequestDelivery.Unknown,
        TimeSpan? retryAfter = null) => new(kind, "private-response-with-credential", delivery, retryAfter);

    [TestMethod]
    [DataRow(ApiFailureKind.Authentication, RunFailureKind.Authentication)]
    [DataRow(ApiFailureKind.InvalidRequest, RunFailureKind.InvalidRequest)]
    [DataRow(ApiFailureKind.ModelUnavailable, RunFailureKind.ModelUnavailable)]
    public async Task RejectedPermanentErrorsStopWithoutRetryOrUnknown(ApiFailureKind apiKind, RunFailureKind runKind)
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(Parameters());
        (await requests.NextAsync()).Fail(Failure(apiKind, RequestDelivery.Rejected));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(runKind, result.Failure);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.AreEqual(1L, result.RejectedRequests);
        Assert.IsFalse(result.ReviewRequired);
        Assert.AreEqual(0, result.RetryAttempts);
        Assert.IsFalse(result.ToString().Contains("private-response", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(ApiFailureKind.Network, RunFailureKind.Network)]
    [DataRow(ApiFailureKind.Transient, RunFailureKind.Transient)]
    [DataRow(ApiFailureKind.Protocol, RunFailureKind.Protocol)]
    public async Task UnknownDeliveryStopsAndBlocksNewRoundUntilReview(ApiFailureKind apiKind, RunFailureKind runKind)
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(Parameters());
        (await requests.NextAsync()).Fail(Failure(apiKind));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(runKind, result.Failure);
        Assert.AreEqual(1, result.UnknownUsageRequests);
        Assert.IsTrue(result.ReviewRequired);
        Assert.AreEqual(1, requests.StartedCount);
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.RunAsync(Parameters()));
        Assert.IsTrue(engine.AcknowledgeReview());
        Assert.AreEqual(1, engine.Snapshot.UnknownUsageRequests);
        Assert.AreEqual(result, await run); // 历史结果不被确认操作修改。
        Assert.IsFalse(engine.AcknowledgeReview());
    }

    [TestMethod]
    public async Task LimitAfterManualPauseResumeRequiresReviewEvenWithRetryHeaderAndDrainsOtherRequests()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(Parameters(3));
        var first = await requests.NextAsync();
        var second = await requests.NextAsync();
        var third = await requests.NextAsync();
        Assert.IsTrue(engine.Pause());
        Assert.IsTrue(engine.Resume());
        first.Fail(Failure(ApiFailureKind.RateOrQuotaLimit, retryAfter: TimeSpan.FromSeconds(1)));
        await UntilAsync(() => engine.Snapshot.State == RunState.AwaitingReview);
        Assert.IsFalse(engine.Pause());
        Assert.IsFalse(engine.Resume());
        Assert.IsFalse(engine.AcknowledgeReview());
        second.Complete(new(80, 10, 90));
        third.Complete(new(70, 10, 80));
        await UntilAsync(() => engine.Snapshot.InFlightRequests == 0);
        Assert.IsFalse(run.IsCompleted);
        Assert.AreEqual(RunState.AwaitingReview, engine.Snapshot.State);
        Assert.AreEqual(new TokenUsage(150, 20, 170), engine.Snapshot.ConfirmedUsage);
        Assert.AreEqual(1, engine.Snapshot.UnknownUsageRequests);
        Assert.AreEqual(0, engine.Snapshot.RetryAttempts);
        Assert.AreEqual(3, requests.StartedCount);
        var stopped = await engine.StopAsync().WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, stopped.State);
        Assert.AreEqual(RunFailureKind.RateOrQuotaLimit, stopped.Failure);
        Assert.IsTrue(stopped.ReviewRequired);
        Assert.AreEqual(stopped, await run);
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.RunAsync(Parameters()));
        Assert.IsTrue(engine.AcknowledgeReview());
    }

    [TestMethod]
    public async Task RealFailureOverridesProtectivePauseAndStillCollectsFinalUsage()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(Parameters(3));
        var first = await requests.NextAsync();
        var second = await requests.NextAsync();
        var third = await requests.NextAsync();
        first.Fail(Failure(ApiFailureKind.RateOrQuotaLimit));
        await UntilAsync(() => engine.Snapshot.State == RunState.AwaitingReview);
        second.Fail(Failure(ApiFailureKind.Authentication, RequestDelivery.Rejected));
        third.Complete(new(90, 10, 100));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(RunFailureKind.Authentication, result.Failure);
        Assert.AreEqual(100L, result.ConfirmedUsage.TotalTokens);
        Assert.AreEqual(1, result.UnknownUsageRequests);
    }

    [TestMethod]
    public async Task KnownNotSentTransientFailureRetriesOnlyAfterBoundedDelay()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var run = engine.RunAsync(Parameters());
        (await requests.NextAsync()).Fail(Failure(ApiFailureKind.Network, RequestDelivery.NotSent));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromSeconds(1)) && engine.Snapshot.IsBackingOff);
        Assert.AreEqual(1, requests.StartedCount);
        Assert.AreEqual(0L, engine.Snapshot.ReservedTokens);
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.AreEqual(1, requests.StartedCount);
        time.Advance(TimeSpan.FromMilliseconds(1));
        var retry = await requests.NextAsync();
        var stopping = engine.StopAsync();
        retry.Complete(new(90, 10, 100));
        var result = await stopping.WaitAsync(Deadline);
        Assert.AreEqual(result, await run);
        Assert.AreEqual(1, result.RetryAttempts);
        Assert.AreEqual(TimeSpan.FromSeconds(1), result.TotalRetryDelay);
        Assert.AreEqual(1L, result.NotSentRequests);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.IsFalse(result.ReviewRequired);
        Assert.AreEqual(RunFailureKind.Network, result.LastFailure);
    }

    [TestMethod]
    public async Task RetryBudgetIsPerRoundAndDoesNotResetAfterSuccessfulRequests()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var run = engine.RunAsync(Parameters());
        for (var retry = 0; retry < 3; retry++)
        {
            (await requests.NextAsync()).Fail(Failure(ApiFailureKind.Transient, RequestDelivery.NotSent));
            var delay = TimeSpan.FromSeconds(1 << retry);
            await UntilAsync(() => time.HasTimer(delay) && engine.Snapshot.RetryAttempts == retry + 1);
            time.Advance(delay);
            (await requests.NextAsync()).Complete(new(90, 10, 100));
        }
        (await requests.NextAsync()).Fail(Failure(ApiFailureKind.Transient, RequestDelivery.NotSent));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunFailureKind.RetryLimitReached, result.Failure);
        Assert.AreEqual(3, result.RetryAttempts);
        Assert.AreEqual(TimeSpan.FromSeconds(7), result.TotalRetryDelay);
        Assert.AreEqual(4L, result.NotSentRequests);
        Assert.AreEqual(300L, result.ConfirmedUsage.TotalTokens);
        Assert.AreEqual(7, requests.StartedCount);
    }

    [TestMethod]
    public async Task TotalWaitLimitRejectsNextBackoffRatherThanShorteningIt()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, new() { MaximumTotalRetryDelay = TimeSpan.FromSeconds(2) }, time);
        var run = engine.RunAsync(Parameters());
        (await requests.NextAsync()).Fail(Failure(ApiFailureKind.Transient, RequestDelivery.NotSent));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(1));
        (await requests.NextAsync()).Fail(Failure(ApiFailureKind.Transient, RequestDelivery.NotSent));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunFailureKind.RetryLimitReached, result.Failure);
        Assert.AreEqual(1, result.RetryAttempts);
        Assert.AreEqual(2, requests.StartedCount);
    }

    [TestMethod]
    [DataRow(3, true)]
    [DataRow(9, false)]
    public async Task RetryHintIsHonoredWithinLocalLimitAndCannotExtendIt(int seconds, bool retryAllowed)
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var run = engine.RunAsync(Parameters());
        (await requests.NextAsync()).Fail(Failure(ApiFailureKind.Transient, RequestDelivery.NotSent, TimeSpan.FromSeconds(seconds)));
        if (retryAllowed)
        {
            await UntilAsync(() => time.HasTimer(TimeSpan.FromSeconds(seconds)));
            Assert.AreEqual(1, requests.StartedCount);
            time.Advance(TimeSpan.FromSeconds(seconds));
            var next = await requests.NextAsync();
            var stop = engine.StopAsync();
            next.Complete(new(90, 10, 100));
            Assert.AreEqual(1, (await stop.WaitAsync(Deadline)).RetryAttempts);
        }
        else Assert.AreEqual(RunFailureKind.RetryLimitReached, (await run.WaitAsync(Deadline)).Failure);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopOrLifecycleCancellationInterruptsBackoffWithoutAnotherRequest(bool cancelLifecycle)
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(Parameters(), cancellation.Token);
        (await requests.NextAsync()).Fail(Failure(ApiFailureKind.Transient, RequestDelivery.NotSent));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromSeconds(1)));
        if (cancelLifecycle) cancellation.Cancel();
        else await engine.StopAsync().WaitAsync(Deadline);
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, result.State);
        Assert.AreEqual(1, requests.StartedCount);
        Assert.IsFalse(result.IsBackingOff);
        Assert.AreEqual(0, result.UnknownUsageRequests);
    }

    [TestMethod]
    public async Task PauseDuringBackoffPreventsRetryUntilExplicitResume()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var run = engine.RunAsync(Parameters());
        (await requests.NextAsync()).Fail(Failure(ApiFailureKind.Transient, RequestDelivery.NotSent));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(engine.Pause());
        time.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => !engine.Snapshot.IsBackingOff);
        Assert.AreEqual(1, requests.StartedCount);
        Assert.IsTrue(engine.Resume());
        var retry = await requests.NextAsync();
        var stop = engine.StopAsync();
        retry.Complete(new(90, 10, 100));
        Assert.AreEqual(await run.WaitAsync(Deadline), await stop.WaitAsync(Deadline));
    }

    [TestMethod]
    public async Task RetryWaitStartsAfterAllOtherRequestsHaveSettled()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var run = engine.RunAsync(Parameters(3));
        var first = await requests.NextAsync();
        var second = await requests.NextAsync();
        var third = await requests.NextAsync();
        first.Fail(Failure(ApiFailureKind.Transient, RequestDelivery.NotSent));
        await UntilAsync(() => engine.Snapshot.NotSentRequests == 1);
        Assert.IsFalse(engine.Snapshot.IsBackingOff);
        Assert.AreEqual(3, requests.StartedCount);
        second.Complete(new(90, 10, 100));
        third.Complete(new(90, 10, 100));
        await UntilAsync(() => time.HasTimer(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(200L, engine.Snapshot.ConfirmedUsage.TotalTokens);
        await engine.StopAsync().WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, (await run).State);
    }

    [TestMethod]
    public async Task CooperativeRequestTimeoutRetainsUnknownUsageAndDoesNotRetry()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var run = engine.RunAsync(Parameters());
        await requests.NextAsync();
        await UntilAsync(() => time.HasTimer(TimeSpan.FromMinutes(2)));
        time.Advance(TimeSpan.FromMinutes(2));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunFailureKind.RequestTimeout, result.Failure);
        Assert.AreEqual(1, result.UnknownUsageRequests);
        Assert.AreEqual(1, requests.StartedCount);
        Assert.IsTrue(result.ReviewRequired);
    }

    [TestMethod]
    public async Task UnresponsiveRequestTimeoutKeepsMutexAndCountsLateValidUsage()
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor(honorCancellation: false);
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var run = engine.RunAsync(Parameters());
        var pending = await requests.NextAsync();
        await UntilAsync(() => time.HasTimer(TimeSpan.FromMinutes(2)));
        time.Advance(TimeSpan.FromMinutes(2));
        await UntilAsync(() => engine.Snapshot.Failure == RunFailureKind.RequestTimeout);
        Assert.IsFalse(run.IsCompleted);
        Assert.AreEqual(1, engine.Snapshot.InFlightRequests);
        Assert.AreEqual(100L, engine.Snapshot.ReservedTokens);
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.RunAsync(Parameters()));
        Assert.IsFalse(engine.AcknowledgeReview());
        pending.Complete(new(90, 10, 100));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(RunFailureKind.RequestTimeout, result.Failure);
        Assert.AreEqual(100L, result.ConfirmedUsage.TotalTokens);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.IsTrue(result.ReviewRequired);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopGraceReturnsStoppingAndOnlyReleasesMutexWhenRequestActuallyEnds(bool honorCancellation)
    {
        var time = new ManualRunTimeProvider();
        var requests = new ControlledRunExecutor(honorCancellation);
        var engine = new SingleRunEngine(requests, timeProvider: time);
        var run = engine.RunAsync(Parameters());
        var pending = await requests.NextAsync();
        var stop = engine.StopAsync();
        await UntilAsync(() => time.HasTimer(TimeSpan.FromSeconds(15)));
        time.Advance(TimeSpan.FromSeconds(15));
        var snapshot = await stop.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopping, snapshot.State);
        Assert.IsTrue(snapshot.StopWaitExpired);
        Assert.IsTrue(snapshot.ReviewRequired);
        if (!honorCancellation)
        {
            Assert.IsFalse(run.IsCompleted);
            Assert.ThrowsExactly<InvalidOperationException>(() => engine.RunAsync(Parameters()));
            pending.Complete(new(90, 10, 100));
        }
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(RunFailureKind.StopTimeout, result.Failure);
        Assert.AreEqual(honorCancellation ? 1 : 0, result.UnknownUsageRequests);
        Assert.AreEqual(honorCancellation ? 0L : 100L, result.ConfirmedUsage.TotalTokens);
    }

    [TestMethod]
    public async Task LifecycleCancellationWhileAwaitingReviewStopsWithoutClearingReview()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(Parameters(), cancellation.Token);
        (await requests.NextAsync()).Fail(Failure(ApiFailureKind.RateOrQuotaLimit));
        await UntilAsync(() => engine.Snapshot.State == RunState.AwaitingReview);
        cancellation.Cancel();
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, result.State);
        Assert.AreEqual(RunEndReason.LifecycleCancellation, result.EndReason);
        Assert.IsTrue(result.ReviewRequired);
        Assert.AreEqual(1, result.UnknownUsageRequests);
    }

    [TestMethod]
    public async Task DisabledRetryPolicyStopsKnownNotSentFaultWithoutWaiting()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests, new() { MaximumRetryAttempts = 0 });
        var run = engine.RunAsync(Parameters());
        (await requests.NextAsync()).Fail(Failure(ApiFailureKind.Transient, RequestDelivery.NotSent));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunFailureKind.RetryLimitReached, result.Failure);
        Assert.AreEqual(0, result.RetryAttempts);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.AreEqual(1, requests.StartedCount);
    }

    [TestMethod]
    public async Task StopDeadlineDoesNotWaitForSlowExecutorCancellationCallback()
    {
        var time = new ManualRunTimeProvider();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestFinished = new TaskCompletionSource<TokenUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new SingleRunEngine(new CallbackExecutor(async token =>
        {
            using var registration = token.Register(() =>
            {
                callbackStarted.TrySetResult();
                callbackRelease.Task.GetAwaiter().GetResult();
            });
            requestStarted.TrySetResult();
            return await requestFinished.Task;
        }), timeProvider: time);
        var run = engine.RunAsync(Parameters());
        await requestStarted.Task.WaitAsync(Deadline);
        var stop = engine.StopAsync();
        await UntilAsync(() => time.HasTimer(TimeSpan.FromSeconds(15)));
        time.Advance(TimeSpan.FromSeconds(15));
        try
        {
            await callbackStarted.Task.WaitAsync(Deadline);
            var snapshot = await stop.WaitAsync(Deadline);
            Assert.AreEqual(RunState.Stopping, snapshot.State);
            Assert.IsFalse(run.IsCompleted);
        }
        finally { callbackRelease.TrySetResult(); requestFinished.TrySetResult(new(90, 10, 100)); }
        Assert.AreEqual(100L, (await run.WaitAsync(Deadline)).ConfirmedUsage.TotalTokens);
    }

    private sealed class CallbackExecutor(Func<CancellationToken, Task<TokenUsage>> execute) : IRunRequestExecutor
    {
        public Task<TokenUsage> ExecuteAsync(RunParameters parameters, CancellationToken cancellationToken) => execute(cancellationToken);
    }

    [TestMethod]
    public void InvalidProtectionOptionsAreRejectedBeforeRequests()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SingleRunEngine(new ControlledRunExecutor(), new() { RequestTimeout = TimeSpan.Zero }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SingleRunEngine(new ControlledRunExecutor(), new() { MaximumRetryAttempts = 11 }));
        Assert.ThrowsExactly<ArgumentException>(() => new SingleRunEngine(new ControlledRunExecutor(), new() { InitialRetryDelay = TimeSpan.FromSeconds(9) }));
    }

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
