using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class RunBudgetTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static RunParameters Parameters(long target = 1000, long reserve = 100, int concurrency = 3)
        => new("mock-model", target) { MaximumConcurrency = concurrency, RequestTokenReservation = reserve };

    [TestMethod]
    [DataRow(0, 100L)]
    [DataRow(4, 100L)]
    [DataRow(3, 0L)]
    [DataRow(1, -1L)]
    [DataRow(1, 120_000_001L)]
    public void InvalidConcurrencyOrReservationDoesNotStart(int concurrency, long reserve)
    {
        var engine = new SingleRunEngine(new ControlledRunExecutor());
        Assert.Throws<ArgumentException>(() => engine.RunAsync(Parameters(reserve: reserve, concurrency: concurrency)));
        Assert.AreEqual(RunSnapshot.Idle, engine.Snapshot);
    }

    [TestMethod]
    public async Task InsufficientInitialBudgetSendsNothingAndReportsRemainder()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var result = await engine.RunAsync(Parameters(target: 99)).WaitAsync(Deadline);
        Assert.AreEqual(RunState.Completed, result.State);
        Assert.AreEqual(RunEndReason.BudgetRemainder, result.EndReason);
        Assert.AreEqual(0, requests.StartedCount);
        Assert.AreEqual(0L, result.ReservedTokens);
    }

    [TestMethod]
    public async Task ThreeRequestsAreReservedAndPausedOutOfOrderUsageIsCountedOnce()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(Parameters());
        var first = await requests.NextAsync();
        var second = await requests.NextAsync();
        var third = await requests.NextAsync();
        Assert.AreEqual(3, engine.Snapshot.InFlightRequests);
        Assert.AreEqual(300L, engine.Snapshot.ReservedTokens);
        engine.Pause();
        third.Complete(new(80, 10, 90));
        await UntilAsync(() => engine.Snapshot.CompletedRequests == 1);
        Assert.AreEqual(200L, engine.Snapshot.ReservedTokens);
        second.Complete(new(60, 10, 70));
        first.Complete(new(70, 10, 80));
        await UntilAsync(() => engine.Snapshot.CompletedRequests == 3);
        Assert.AreEqual(new TokenUsage(210, 30, 240), engine.Snapshot.ConfirmedUsage);
        Assert.AreEqual(0L, engine.Snapshot.ReservedTokens);
        Assert.AreEqual(RunState.Paused, engine.Snapshot.State);
        Assert.AreEqual(3, requests.StartedCount);
        Assert.IsTrue(engine.Resume());
        var next = new[] { await requests.NextAsync(), await requests.NextAsync(), await requests.NextAsync() };
        var stopping = engine.StopAsync();
        foreach (var request in next) request.Complete(new(90, 10, 100));
        var result = await stopping.WaitAsync(Deadline);
        Assert.AreEqual(result, await run.WaitAsync(Deadline));
        Assert.AreEqual(new TokenUsage(480, 60, 540), result.ConfirmedUsage);
        Assert.AreEqual(6L, result.CompletedRequests);
        Assert.AreEqual(6, requests.StartedCount);
    }

    [TestMethod]
    public async Task NearTargetDrainsThenRunsOnlyOneAndStopsBeforeUnreservedTail()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(Parameters(target: 400));
        var initial = new[] { await requests.NextAsync(), await requests.NextAsync(), await requests.NextAsync() };
        initial[0].Complete(new(90, 10, 100));
        await UntilAsync(() => engine.Snapshot.CompletedRequests == 1);
        Assert.AreEqual(3, requests.StartedCount);
        Assert.AreEqual(2, engine.Snapshot.InFlightRequests);
        initial[1].Complete(new(90, 10, 100));
        await UntilAsync(() => engine.Snapshot.CompletedRequests == 2);
        Assert.AreEqual(3, requests.StartedCount);
        initial[2].Complete(new(90, 10, 100));
        var tail = await requests.NextAsync();
        Assert.AreEqual(1, engine.Snapshot.InFlightRequests);
        Assert.AreEqual(100L, engine.Snapshot.ReservedTokens);
        tail.Complete(new(80, 10, 90));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunEndReason.BudgetRemainder, result.EndReason);
        Assert.AreEqual(new TokenUsage(350, 40, 390), result.ConfirmedUsage);
        Assert.AreEqual(4, requests.StartedCount);
        Assert.AreEqual(0L, result.ReservedTokens);
    }

    [TestMethod]
    public async Task SmallRemainingTargetStartsWithOneRequest()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(Parameters(target: 200));
        var first = await requests.NextAsync();
        Assert.AreEqual(1, engine.Snapshot.InFlightRequests);
        Assert.AreEqual(1, requests.StartedCount);
        first.Complete(new(90, 10, 100));
        var second = await requests.NextAsync();
        Assert.AreEqual(1, engine.Snapshot.InFlightRequests);
        second.Complete(new(90, 10, 100));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunEndReason.TargetReached, result.EndReason);
        Assert.AreEqual(200L, result.ConfirmedUsage.TotalTokens);
    }

    [TestMethod]
    public async Task ConcurrentCompletionAfterStopCountsAllUsageWithoutRefill()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(Parameters());
        var current = new[] { await requests.NextAsync(), await requests.NextAsync(), await requests.NextAsync() };
        var stopping = engine.StopAsync();
        await Task.WhenAll(current.Select(request => Task.Run(() => request.Complete(new(70, 10, 80)))));
        var result = await stopping.WaitAsync(Deadline);
        Assert.AreEqual(result, await run.WaitAsync(Deadline));
        Assert.AreEqual(RunState.Stopped, result.State);
        Assert.AreEqual(new TokenUsage(210, 30, 240), result.ConfirmedUsage);
        Assert.AreEqual(3L, result.CompletedRequests);
        Assert.AreEqual(0L, result.ReservedTokens);
        Assert.AreEqual(0, result.InFlightRequests);
        Assert.AreEqual(3, requests.StartedCount);
    }

    [TestMethod]
    public async Task OneFailureDrainsOtherRequestsAndHoldsLeaseUntilLastResult()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(Parameters());
        var current = new[] { await requests.NextAsync(), await requests.NextAsync(), await requests.NextAsync() };
        current[0].Fail(new InvalidOperationException("private-server-body"));
        await UntilAsync(() => engine.Snapshot.UnknownUsageRequests == 1);
        Assert.AreEqual(RunState.Stopping, engine.Snapshot.State);
        Assert.IsFalse(run.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => engine.RunAsync(Parameters()));
        current[1].Complete(new(70, 10, 80));
        current[2].Complete(new(80, 10, 90));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(RunFailureKind.RequestFailed, result.Failure);
        Assert.AreEqual(new TokenUsage(150, 20, 170), result.ConfirmedUsage);
        Assert.AreEqual(2L, result.CompletedRequests);
        Assert.AreEqual(1, result.UnknownUsageRequests);
        Assert.AreEqual(0L, result.ReservedTokens);
        Assert.AreEqual(3, requests.StartedCount);
        Assert.IsFalse(result.ToString().Contains("private-server-body", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnderestimatedRequestRecordsActualUsageAndStopsAllNewRequests()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(Parameters());
        var current = new[] { await requests.NextAsync(), await requests.NextAsync(), await requests.NextAsync() };
        current[0].Complete(new(140, 10, 150));
        await UntilAsync(() => engine.Snapshot.Failure == RunFailureKind.BudgetEstimateExceeded);
        Assert.AreEqual(RunState.Stopping, engine.Snapshot.State);
        current[1].Complete(new(70, 10, 80));
        current[2].Complete(new(70, 10, 80));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(new TokenUsage(280, 30, 310), result.ConfirmedUsage);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.AreEqual(3, requests.StartedCount);
    }

    [TestMethod]
    public async Task CancellationOfThreeInFlightRequestsPreservesThreeUnknowns()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(Parameters(), cancellation.Token);
        await requests.NextAsync();
        await requests.NextAsync();
        await requests.NextAsync();
        cancellation.Cancel();
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, result.State);
        Assert.AreEqual(RunEndReason.LifecycleCancellation, result.EndReason);
        Assert.AreEqual(3, result.UnknownUsageRequests);
        Assert.AreEqual(0L, result.ReservedTokens);
        Assert.AreEqual(0, result.InFlightRequests);
        Assert.AreEqual(3, requests.StartedCount);
    }

    [TestMethod]
    public async Task RealFailureAfterCancellationRemainsFailureAndPreservesOtherFinalUsage()
    {
        var requests = new ControlledRunExecutor(honorCancellation: false);
        var engine = new SingleRunEngine(requests);
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(Parameters(), cancellation.Token);
        var current = new[] { await requests.NextAsync(), await requests.NextAsync(), await requests.NextAsync() };
        cancellation.Cancel();
        current[0].Fail(new OperationCanceledException());
        await UntilAsync(() => engine.Snapshot.Failure == RunFailureKind.Canceled);
        current[1].Fail(new InvalidOperationException("private-response"));
        await UntilAsync(() => engine.Snapshot.Failure == RunFailureKind.RequestFailed);
        current[2].Complete(new(70, 10, 80));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(RunEndReason.Failure, result.EndReason);
        Assert.AreEqual(RunFailureKind.RequestFailed, result.Failure);
        Assert.AreEqual(2, result.UnknownUsageRequests);
        Assert.AreEqual(new TokenUsage(70, 10, 80), result.ConfirmedUsage);
        Assert.AreEqual(0L, result.ReservedTokens);
        Assert.AreEqual(3, requests.StartedCount);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.IsLessThan(Deadline, stopwatch.Elapsed, "预算状态未在期限内达到。");
            await Task.Delay(1);
        }
    }
}
