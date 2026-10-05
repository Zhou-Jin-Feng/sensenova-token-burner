using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class SingleRunEngineTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly RunParameters SmallRun = new("mock-model", 200);

    [TestMethod]
    public async Task ConstructionAndIdleControlsNeverStartRequests()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        Assert.AreEqual(RunSnapshot.Idle, engine.Snapshot);
        Assert.IsFalse(engine.Pause());
        Assert.IsFalse(engine.Resume());
        Assert.AreEqual(RunSnapshot.Idle, await engine.StopAsync());
        Assert.AreEqual(0, requests.StartedCount);
    }

    [TestMethod]
    public void SettingsSnapshotUsesCalibrationAndRemainsUnchanged()
    {
        var settings = new AppSettings { Percentage = 50, ModelId = "first-model" };
        var parameters = RunParameters.FromSettings(settings);
        settings = settings with { Percentage = 95, ModelId = "next-model" };
        Assert.AreEqual(new RunParameters("first-model", 60_000_000), parameters);
        Assert.AreEqual(new RunParameters("next-model", 120_000_000), RunParameters.FromSettings(settings));
    }

    [TestMethod]
    public async Task ZeroPercentCompletesWithoutAnyRequest()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var result = await engine.RunAsync(RunParameters.FromSettings(new AppSettings { Percentage = 0 }));
        Assert.AreEqual(RunState.Completed, result.State);
        Assert.AreEqual(new TokenUsage(0, 0, 0), result.ConfirmedUsage);
        Assert.AreEqual(0L, result.CompletedRequests);
        Assert.AreEqual(0, requests.StartedCount);
    }

    [TestMethod]
    [DataRow("", 200L)]
    [DataRow("  ", 200L)]
    [DataRow("model", -1L)]
    [DataRow("model", 120_000_001L)]
    public void InvalidParametersDoNotChangeIdleState(string model, long target)
    {
        var engine = new SingleRunEngine(new ControlledRunExecutor());
        Assert.Throws<ArgumentException>(() => engine.RunAsync(new(model, target)));
        Assert.AreEqual(RunSnapshot.Idle, engine.Snapshot);
    }

    [TestMethod]
    public async Task PreCanceledRunDoesNotDispatch()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await engine.RunAsync(SmallRun, cancellation.Token);
        Assert.AreEqual(RunState.Stopped, result.State);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.AreEqual(0, requests.StartedCount);
    }

    [TestMethod]
    public async Task PauseDrainsCurrentRequestAndResumeRetainsTotalsAndParameters()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var parameters = SmallRun;
        var run = engine.RunAsync(parameters);
        var first = await requests.NextAsync();
        Assert.IsTrue(engine.Pause());
        Assert.IsFalse(engine.Pause());
        Assert.AreEqual(1, engine.Snapshot.InFlightRequests);
        parameters = parameters with { ModelId = "next-model", TargetTokens = 500 };
        first.Complete(new(80, 20, 100));
        await UntilAsync(() => engine.Snapshot.CompletedRequests == 1 && engine.Snapshot.InFlightRequests == 0);
        Assert.AreEqual(RunState.Paused, engine.Snapshot.State);
        Assert.AreEqual(1, requests.StartedCount);
        Assert.AreEqual(SmallRun, engine.Snapshot.Parameters);
        Assert.IsTrue(engine.Resume());
        Assert.IsFalse(engine.Resume());
        var second = await requests.NextAsync();
        Assert.AreEqual(SmallRun, second.Parameters);
        second.Complete(new(120, 30, 150));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Completed, result.State);
        Assert.AreEqual(new TokenUsage(200, 50, 250), result.ConfirmedUsage);
        Assert.AreEqual(2L, result.CompletedRequests);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.AreEqual(2, requests.StartedCount);
        Assert.AreEqual(500L, parameters.TargetTokens);
    }

    [TestMethod]
    public async Task StopWaitsForInFlightUsageAndBlocksRestartUntilSettled()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(SmallRun);
        var current = await requests.NextAsync();
        var stopping = engine.StopAsync();
        Assert.AreEqual(RunState.Stopping, engine.Snapshot.State);
        Assert.IsFalse(stopping.IsCompleted);
        Assert.IsFalse(engine.Pause());
        Assert.IsFalse(engine.Resume());
        Assert.Throws<InvalidOperationException>(() => engine.RunAsync(SmallRun));
        current.Complete(new(80, 20, 100));
        var result = await stopping.WaitAsync(Deadline);
        Assert.AreEqual(result, await run.WaitAsync(Deadline));
        Assert.AreEqual(RunState.Stopped, result.State);
        Assert.AreEqual(new TokenUsage(80, 20, 100), result.ConfirmedUsage);
        Assert.AreEqual(0, result.InFlightRequests);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.AreEqual(1, requests.StartedCount);
        Assert.AreEqual(result, await engine.StopAsync());
    }

    [TestMethod]
    public async Task CancelingStopWaitDoesNotReleaseLeaseOrCancelRequest()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(SmallRun);
        var current = await requests.NextAsync();
        using var waitCancellation = new CancellationTokenSource();
        var stopping = engine.StopAsync(waitCancellation.Token);
        waitCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => stopping);
        Assert.AreEqual(RunState.Stopping, engine.Snapshot.State);
        Assert.AreEqual(1, engine.Snapshot.InFlightRequests);
        Assert.IsFalse(run.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => engine.RunAsync(SmallRun));
        current.Complete(new(80, 20, 100));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, result.State);
        Assert.AreEqual(0, result.UnknownUsageRequests);
    }

    [TestMethod]
    public async Task StopWhilePausedWithoutInFlightWakesLoopWithoutNewRequest()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(SmallRun);
        var current = await requests.NextAsync();
        engine.Pause();
        current.Complete(new(80, 20, 100));
        await UntilAsync(() => engine.Snapshot.CompletedRequests == 1);
        var result = await engine.StopAsync().WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, result.State);
        Assert.AreEqual(1, requests.StartedCount);
        Assert.AreEqual(result, await run.WaitAsync(Deadline));
    }

    [TestMethod]
    public async Task RepeatedPauseAndResumeDoesNotLoseWakeups()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(SmallRun);
        var current = await requests.NextAsync();
        for (var index = 0; index < 20; index++)
        {
            Assert.IsTrue(engine.Pause());
            Assert.IsTrue(engine.Resume());
        }
        engine.Pause();
        current.Complete(new(80, 20, 100));
        await UntilAsync(() => engine.Snapshot.CompletedRequests == 1);
        Assert.IsTrue(engine.Resume());
        var next = await requests.NextAsync();
        next.Complete(new(80, 20, 100));
        Assert.AreEqual(RunState.Completed, (await run.WaitAsync(Deadline)).State);
        Assert.AreEqual(2, requests.StartedCount);
    }

    [TestMethod]
    public async Task PauseAndResumeAcrossSeveralSettledRequestsRetainsEveryUsage()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(new("mock-model", 800));
        for (var index = 1; index <= 8; index++)
        {
            var current = await requests.NextAsync();
            Assert.IsTrue(engine.Pause());
            current.Complete(new(80, 20, 100));
            var expected = index;
            await UntilAsync(() => engine.Snapshot.CompletedRequests == expected);
            if (index < 8)
            {
                Assert.AreEqual(RunState.Paused, engine.Snapshot.State);
                Assert.AreEqual(index, requests.StartedCount);
                Assert.IsTrue(engine.Resume());
            }
        }
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Completed, result.State);
        Assert.AreEqual(new TokenUsage(640, 160, 800), result.ConfirmedUsage);
        Assert.AreEqual(8L, result.CompletedRequests);
    }

    [TestMethod]
    public async Task ConcurrentStartsAcceptExactlyOneRun()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run<Task<RunSnapshot>?>(() =>
        {
            try { return engine.RunAsync(SmallRun); }
            catch (InvalidOperationException) { return null; }
        })).ToArray();
        // 显式指定TResult，避免Task.Run(Func<Task<T>>)自动拆包为最终结果。
        var handles = await Task.WhenAll(attempts).WaitAsync(Deadline);
        Assert.AreEqual(1, handles.Count(handle => handle is not null));
        var current = await requests.NextAsync();
        current.Complete(new(160, 40, 200));
        Assert.AreEqual(RunState.Completed, (await handles.Single(handle => handle is not null)!.WaitAsync(Deadline)).State);
        Assert.AreEqual(1, requests.StartedCount);
    }

    [TestMethod]
    public async Task CancellationInFlightPreservesUnknownUsageAndStops()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(SmallRun, cancellation.Token);
        await requests.NextAsync();
        cancellation.Cancel();
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, result.State);
        Assert.AreEqual(RunFailureKind.Canceled, result.Failure);
        Assert.AreEqual(1, result.UnknownUsageRequests);
        Assert.AreEqual(0L, result.CompletedRequests);
        Assert.AreEqual(new TokenUsage(0, 0, 0), result.ConfirmedUsage);
        Assert.AreEqual(1, requests.StartedCount);
    }

    [TestMethod]
    public async Task CancellationWhilePausedKeepsConfirmedUsageWithoutUnknownRequest()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(SmallRun, cancellation.Token);
        var current = await requests.NextAsync();
        engine.Pause();
        current.Complete(new(80, 20, 100));
        await UntilAsync(() => engine.Snapshot.CompletedRequests == 1);
        cancellation.Cancel();
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, result.State);
        Assert.AreEqual(new TokenUsage(80, 20, 100), result.ConfirmedUsage);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.AreEqual(1, requests.StartedCount);
    }

    [TestMethod]
    public async Task FinalUsageReturnedAfterCancellationIsStillCounted()
    {
        var requests = new ControlledRunExecutor(honorCancellation: false);
        var engine = new SingleRunEngine(requests);
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(SmallRun, cancellation.Token);
        var current = await requests.NextAsync();
        cancellation.Cancel();
        current.Complete(new(80, 20, 100));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, result.State);
        Assert.AreEqual(new TokenUsage(80, 20, 100), result.ConfirmedUsage);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.AreEqual(1, requests.StartedCount);
    }

    [TestMethod]
    public async Task ExecutorCancellationWithoutLifecycleCancellationIsUnknownFailure()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(SmallRun);
        (await requests.NextAsync()).Fail(new OperationCanceledException("private-timeout-body"));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(RunFailureKind.RequestFailed, result.Failure);
        Assert.AreEqual(1, result.UnknownUsageRequests);
        Assert.AreEqual(1, requests.StartedCount);
    }

    [TestMethod]
    public async Task CompletedRoundCanRestartAndPriorSnapshotRemainsStable()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var firstRun = engine.RunAsync(SmallRun);
        (await requests.NextAsync()).Complete(new(160, 40, 200));
        var first = await firstRun.WaitAsync(Deadline);
        var nextParameters = new RunParameters("next-model", 50);
        var nextRun = engine.RunAsync(nextParameters);
        var next = await requests.NextAsync();
        Assert.AreEqual(nextParameters, next.Parameters);
        Assert.AreEqual(0L, engine.Snapshot.CompletedRequests);
        next.Complete(new(40, 10, 50));
        var second = await nextRun.WaitAsync(Deadline);
        Assert.AreEqual(new TokenUsage(40, 10, 50), second.ConfirmedUsage);
        Assert.AreEqual(new TokenUsage(160, 40, 200), first.ConfirmedUsage);
        Assert.AreEqual(SmallRun, first.Parameters);
    }

    [TestMethod]
    public async Task RequestFailureDoesNotRetryOrExposeExceptionTextAndRequiresReviewBeforeRestart()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(SmallRun);
        (await requests.NextAsync()).Fail(new InvalidOperationException("private-response-body"));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(RunFailureKind.RequestFailed, result.Failure);
        Assert.AreEqual(1, result.UnknownUsageRequests);
        Assert.IsFalse(result.ToString().Contains("private-response-body", StringComparison.Ordinal));
        Assert.AreEqual(1, requests.StartedCount);
        Assert.IsTrue(result.ReviewRequired);
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.RunAsync(SmallRun));
        Assert.IsTrue(engine.AcknowledgeReview());
        Assert.AreEqual(1, engine.Snapshot.UnknownUsageRequests);
        var retry = engine.RunAsync(SmallRun);
        (await requests.NextAsync()).Complete(new(160, 40, 200));
        var restarted = await retry.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Completed, restarted.State);
        Assert.AreEqual(0, restarted.UnknownUsageRequests);
    }

    [TestMethod]
    [DataRow(-1L, 1L, 0L)]
    [DataRow(1L, -1L, 0L)]
    [DataRow(1L, 1L, -1L)]
    [DataRow(1L, 1L, 3L)]
    [DataRow(long.MaxValue, 1L, long.MaxValue)]
    public async Task InvalidUsageStopsWithoutCountingIt(long input, long output, long total)
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(SmallRun);
        (await requests.NextAsync()).Complete(new(input, output, total));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(RunFailureKind.InvalidUsage, result.Failure);
        Assert.AreEqual(1, result.UnknownUsageRequests);
        Assert.AreEqual(0L, result.CompletedRequests);
        Assert.AreEqual(new TokenUsage(0, 0, 0), result.ConfirmedUsage);
        Assert.AreEqual(1, requests.StartedCount);
    }

    [TestMethod]
    public async Task MissingUsageIsUnknownAndIsNotRetried()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(SmallRun);
        (await requests.NextAsync()).Complete(null!);
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunFailureKind.UsageUnavailable, result.Failure);
        Assert.AreEqual(1, result.UnknownUsageRequests);
        Assert.AreEqual(0L, result.CompletedRequests);
        Assert.AreEqual(1, requests.StartedCount);
    }

    [TestMethod]
    public async Task ZeroUsageStopsInsteadOfSpinningAndIsKnown()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(SmallRun);
        (await requests.NextAsync()).Complete(new(0, 0, 0));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(RunFailureKind.NoProgress, result.Failure);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.AreEqual(1L, result.CompletedRequests);
        Assert.AreEqual(1, requests.StartedCount);
    }

    [TestMethod]
    public async Task AccumulationOverflowKeepsPreviousTotalsAndDoesNotRetry()
    {
        var requests = new ControlledRunExecutor();
        var engine = new SingleRunEngine(requests);
        var run = engine.RunAsync(SmallRun);
        (await requests.NextAsync()).Complete(new(1, 0, 1));
        (await requests.NextAsync()).Complete(new(long.MaxValue, 0, long.MaxValue));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.AreEqual(RunFailureKind.UsageOverflow, result.Failure);
        Assert.AreEqual(new TokenUsage(1, 0, 1), result.ConfirmedUsage);
        Assert.AreEqual(1L, result.CompletedRequests);
        Assert.AreEqual(1, result.UnknownUsageRequests);
        Assert.AreEqual(2, requests.StartedCount);
    }

    [TestMethod]
    public async Task ConcreteMockCompletesScaledRoundAndRetainsActualOvershoot()
    {
        var engine = new SingleRunEngine(new MockRunRequestExecutor(TimeSpan.Zero));
        var result = await engine.RunAsync(SmallRun).WaitAsync(Deadline);
        Assert.AreEqual(RunState.Completed, result.State);
        Assert.AreEqual(new TokenUsage(256, 16, 272), result.ConfirmedUsage);
        Assert.AreEqual(2L, result.CompletedRequests);
        Assert.AreEqual(0, result.InFlightRequests);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.IsLessThan(Deadline, stopwatch.Elapsed, "未在期限内达到预期状态。");
            await Task.Delay(1);
        }
    }
}

internal sealed class ControlledRunExecutor(bool honorCancellation = true) : IRunRequestExecutor
{
    private readonly Channel<PendingRunRequest> _requests = Channel.CreateUnbounded<PendingRunRequest>();
    private int _startedCount;
    public int StartedCount => Volatile.Read(ref _startedCount);

    public async Task<TokenUsage> ExecuteAsync(RunParameters parameters, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startedCount);
        var request = new PendingRunRequest(parameters);
        await _requests.Writer.WriteAsync(request, cancellationToken);
        return honorCancellation
            ? await request.Completion.Task.WaitAsync(cancellationToken)
            : await request.Completion.Task;
    }

    public Task<PendingRunRequest> NextAsync() => _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
}

internal sealed class PendingRunRequest(RunParameters parameters)
{
    public RunParameters Parameters { get; } = parameters;
    public TaskCompletionSource<TokenUsage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Complete(TokenUsage usage) => Completion.TrySetResult(usage);
    public void Fail(Exception error) => Completion.TrySetException(error);
}
