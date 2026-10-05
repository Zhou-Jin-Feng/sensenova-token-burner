using System.IO;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure.Api;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class RunRecoveryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static RunParameters Parameters(long target = 100) => new("mock-model", target) { RequestTokenReservation = 100 };

    [TestMethod]
    public async Task UninitializedSessionCannotRunOrEnableAndFreshInitializationSendsNothing()
    {
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, new MemoryRunStateStore());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RunOnceAsync(Parameters()));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.EnableScheduleAsync(new(Parameters())));
        await runtime.InitializeAsync();
        Assert.AreEqual(RecoveryState.Ready, runtime.Recovery.State);
        Assert.AreEqual(0, requests.StartedCount);
        Assert.AreEqual(ScheduleState.Disabled, runtime.Schedule.State);
    }

    [TestMethod]
    public async Task DispatchWaitsForInFlightCheckpointAndRunCompletionWaitsForFinalSave()
    {
        var store = new MemoryRunStateStore { BlockAt = 2 };
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, store);
        await runtime.InitializeAsync();
        var run = runtime.RunOnceAsync(Parameters());
        await store.Blocked.Task.WaitAsync(Deadline);
        Assert.AreEqual(0, requests.StartedCount);
        Assert.AreEqual(1, store.Attempted!.CurrentRun!.Snapshot.InFlightRequests);
        store.Release.TrySetResult();
        var request = await requests.NextAsync();
        Assert.AreEqual(1, store.Document!.CurrentRun!.Snapshot.InFlightRequests);
        Assert.AreEqual(100L, store.Document.CurrentRun.Snapshot.ReservedTokens);
        request.Complete(new(90, 10, 100));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Completed, store.Document.CurrentRun.Snapshot.State);
        Assert.AreEqual(result, store.Document.CurrentRun.Snapshot);
    }

    [TestMethod]
    public async Task FinalSaveBlocksRunCompletionAndRejectsEarlyRecoveryConfirmation()
    {
        var store = new MemoryRunStateStore { BlockAt = 4 };
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, store);
        await runtime.InitializeAsync();
        var run = runtime.RunOnceAsync(Parameters());
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await store.Blocked.Task.WaitAsync(Deadline);
        Assert.IsFalse(run.IsCompleted);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.ConfirmRecoveryAsync(true));
        store.Release.TrySetResult();
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(result, store.Document!.CurrentRun!.Snapshot);
    }

    [TestMethod]
    public async Task StopGraceExpiryDuringFinalSaveIsIncludedInFinalDurableRecord()
    {
        var time = new ManualRunTimeProvider();
        var store = new MemoryRunStateStore { BlockAt = 4 };
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, store, timeProvider: time);
        await runtime.InitializeAsync();
        var run = runtime.RunOnceAsync(Parameters());
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await store.Blocked.Task.WaitAsync(Deadline);
        var stop = runtime.StopAsync();
        await UntilAsync(() => time.HasTimer(TimeSpan.FromSeconds(15)));
        time.Advance(TimeSpan.FromSeconds(15));
        Assert.AreEqual(RunState.Stopping, (await stop.WaitAsync(Deadline)).State);
        store.Release.TrySetResult();
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, result.State);
        Assert.IsTrue(result.ReviewRequired);
        Assert.AreEqual(result, store.Document!.CurrentRun!.Snapshot);
        Assert.AreEqual(RecoveryState.NeedsReview, runtime.Recovery.State);
    }

    [TestMethod]
    public async Task StopWhileCheckpointIsBlockedPreventsReservedRequestFromBeingSent()
    {
        var store = new MemoryRunStateStore { BlockAt = 2 };
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, store);
        await runtime.InitializeAsync();
        var run = runtime.RunOnceAsync(Parameters());
        await store.Blocked.Task.WaitAsync(Deadline);
        var stop = runtime.StopAsync();
        store.Release.TrySetResult();
        await stop.WaitAsync(Deadline);
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(0, requests.StartedCount);
        Assert.AreEqual(0, result.InFlightRequests);
        Assert.AreEqual(1L, result.NotSentRequests);
        Assert.AreEqual(RunState.Stopped, store.Document!.CurrentRun!.Snapshot.State);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task BeginOrPreDispatchWriteFailureSendsNothingAndCannotBeAcknowledgedAway(int failAt)
    {
        var store = new MemoryRunStateStore { FailAt = failAt };
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, store);
        await runtime.InitializeAsync();
        var result = await runtime.RunOnceAsync(Parameters()).WaitAsync(Deadline);
        Assert.AreEqual(0, requests.StartedCount);
        Assert.AreEqual(RunFailureKind.StorageFailure, result.Failure);
        Assert.AreEqual(RecoveryState.StorageFailed, runtime.Recovery.State);
        Assert.IsFalse(runtime.Resume());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.ConfirmRecoveryAsync(true));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RunOnceAsync(Parameters()));
        Assert.IsFalse(result.ToString().Contains("private-write-error", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SettlementWriteFailureRetainsOldInFlightEvidenceAndClosesPlan()
    {
        var store = new MemoryRunStateStore { FailAt = 4 }; // 计划、开始、发前、结算。
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, store);
        await runtime.InitializeAsync();
        await runtime.EnableScheduleAsync(new(Parameters(1000)));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await UntilAsync(() => runtime.Schedule.State == ScheduleState.Blocked && runtime.CurrentRun.State == RunState.Faulted);
        Assert.AreEqual(1, requests.StartedCount);
        Assert.AreEqual(100L, runtime.CurrentRun.ConfirmedUsage.TotalTokens);
        Assert.AreEqual(1, store.Document!.CurrentRun!.Snapshot.InFlightRequests);
        var reopened = new PersistentRunSession(new ControlledRunExecutor(), store);
        await reopened.InitializeAsync();
        Assert.AreEqual(RecoveryState.NeedsReview, reopened.Recovery.State);
        Assert.AreEqual(1, reopened.Recovery.UnresolvedRequests);
    }

    [TestMethod]
    public async Task WriteFailureWithOtherRequestsInFlightRetainsMutualExclusionAndCollectsTheirUsage()
    {
        var store = new MemoryRunStateStore { FailAt = 5 };
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, store);
        await runtime.InitializeAsync();
        var run = runtime.RunOnceAsync(Parameters(1000) with { MaximumConcurrency = 3 });
        var first = await requests.NextAsync();
        var second = await requests.NextAsync();
        var third = await requests.NextAsync();
        first.Complete(new(90, 10, 100));
        await UntilAsync(() => runtime.Recovery.State == RecoveryState.StorageFailed);
        Assert.IsFalse(run.IsCompleted);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RunOnceAsync(Parameters()));
        second.Complete(new(90, 10, 100));
        third.Complete(new(90, 10, 100));
        var result = await run.WaitAsync(Deadline);
        Assert.AreEqual(300L, result.ConfirmedUsage.TotalTokens);
        Assert.AreEqual(3, requests.StartedCount);
        Assert.AreEqual(0, result.InFlightRequests);
    }

    [TestMethod]
    public async Task ReopenInterruptedRunRequiresReviewKeepsEvidenceAndConfirmationStartsNothing()
    {
        using var folder = new TestFolder();
        var requests = new ControlledRunExecutor();
        var store = new JsonRunStateStore(folder.Path);
        var runtime = new PersistentRunSession(requests, store);
        await runtime.InitializeAsync();
        var run = runtime.RunOnceAsync(Parameters());
        var pending = await requests.NextAsync();
        var captured = (await store.LoadAsync())!;
        var restartStore = new MemoryRunStateStore { Document = captured };
        var nextRequests = new ControlledRunExecutor();
        var reopened = new PersistentRunSession(nextRequests, restartStore);
        await reopened.InitializeAsync();
        Assert.AreEqual(RecoveryState.NeedsReview, reopened.Recovery.State);
        Assert.AreEqual(1, reopened.Recovery.UnresolvedRequests);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ConfirmRecoveryAsync());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.EnableScheduleAsync(new(Parameters())));
        await reopened.ConfirmRecoveryAsync(true);
        Assert.AreEqual(0, nextRequests.StartedCount);
        Assert.AreEqual(1, reopened.Recovery.UnresolvedRequests);
        Assert.IsNotNull(reopened.Recovery.Document!.CurrentRun!.ReviewedUtc);
        var next = reopened.RunOnceAsync(Parameters());
        (await nextRequests.NextAsync()).Complete(new(90, 10, 100));
        await next.WaitAsync(Deadline);
        Assert.AreEqual(captured.CurrentRun!.Snapshot, reopened.Recovery.Document!.History.Single().Snapshot);
        pending.Complete(new(90, 10, 100));
        await run.WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task CleanReopenWaitsForConfirmationAndNeverEnablesSavedPlanAutomatically()
    {
        var time = new ManualRunTimeProvider();
        var store = new MemoryRunStateStore();
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, store, timeProvider: time);
        await runtime.InitializeAsync();
        await runtime.EnableScheduleAsync(new(Parameters(), 5));
        (await requests.NextAsync()).Complete(new(90, 10, 100));
        await UntilAsync(() => store.Document!.CurrentRun!.Snapshot.State == RunState.Completed);
        var saved = store.Document;
        await runtime.DisableScheduleAsync();
        var reopenedRequests = new ControlledRunExecutor();
        var reopened = new PersistentRunSession(reopenedRequests, new MemoryRunStateStore { Document = saved }, timeProvider: time);
        await reopened.InitializeAsync();
        time.Advance(TimeSpan.FromHours(20));
        Assert.AreEqual(RecoveryState.AwaitingConfirmation, reopened.Recovery.State);
        Assert.AreEqual(ScheduleState.Disabled, reopened.Schedule.State);
        Assert.AreEqual(0, reopenedRequests.StartedCount);
        await reopened.ConfirmRecoveryAsync();
        Assert.AreEqual(0, reopenedRequests.StartedCount);
        Assert.IsFalse(reopened.Recovery.Document!.PlanRequested);
        await reopened.EnableScheduleAsync(saved!.Plan!);
        (await reopenedRequests.NextAsync()).Complete(new(90, 10, 100));
        await UntilAsync(() => reopened.CurrentRun.State == RunState.Completed);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(20), reopened.Schedule.AnchorUtc);
        await reopened.DisableScheduleAsync();
    }

    [TestMethod]
    public async Task DamagedStateBlocksWithoutReplacingOriginalOrDispatching()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "run-state.json");
        await File.WriteAllTextAsync(path, "private-corrupt-content");
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, new JsonRunStateStore(folder.Path));
        await runtime.InitializeAsync();
        Assert.AreEqual(RecoveryState.Corrupt, runtime.Recovery.State);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.ConfirmRecoveryAsync(true));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RunOnceAsync(Parameters()));
        Assert.AreEqual("private-corrupt-content", await File.ReadAllTextAsync(path));
        Assert.AreEqual(0, requests.StartedCount);
    }

    [TestMethod]
    public async Task UnknownUsageReviewAfterStopIsDurableAndOrdinaryResumeCannotBypassIt()
    {
        var store = new MemoryRunStateStore();
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, store);
        await runtime.InitializeAsync();
        var run = runtime.RunOnceAsync(Parameters());
        (await requests.NextAsync()).Fail(new IOException("private-network-error"));
        await run.WaitAsync(Deadline);
        Assert.AreEqual(RecoveryState.NeedsReview, runtime.Recovery.State);
        Assert.IsFalse(runtime.Resume());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.ConfirmRecoveryAsync());
        await runtime.ConfirmRecoveryAsync(true);
        var reopened = new PersistentRunSession(new ControlledRunExecutor(), store);
        await reopened.InitializeAsync();
        Assert.AreEqual(RecoveryState.AwaitingConfirmation, reopened.Recovery.State);
        Assert.AreEqual(1, reopened.Recovery.UnresolvedRequests);
        Assert.IsNotNull(store.Document!.CurrentRun!.ReviewedUtc);
    }

    [TestMethod]
    public async Task HistoryIsBoundedAndContainsActualUsageAcrossManyRounds()
    {
        using var folder = new TestFolder();
        var runtime = new PersistentRunSession(new MockRunRequestExecutor(TimeSpan.Zero), new JsonRunStateStore(folder.Path));
        await runtime.InitializeAsync();
        for (var i = 0; i < 24; i++) await runtime.RunOnceAsync(new("mock-model", 100)).WaitAsync(Deadline);
        Assert.AreEqual(20, runtime.Recovery.Document!.History.Length);
        Assert.IsTrue(runtime.Recovery.Document.History.All(record => record.Snapshot.ConfirmedUsage.TotalTokens == 136));
        Assert.IsLessThanOrEqualTo(65_536L, new FileInfo(Path.Combine(folder.Path, "run-state.json")).Length);
        Assert.AreEqual(0, Directory.GetFiles(folder.Path, "*.tmp").Length);
        Assert.AreEqual(RecoveryState.Ready, runtime.Recovery.State);
    }

    [TestMethod]
    public async Task ZeroTargetProducesDurableCompletedRecordWithoutRequest()
    {
        var store = new MemoryRunStateStore();
        var requests = new ControlledRunExecutor();
        var runtime = new PersistentRunSession(requests, store);
        await runtime.InitializeAsync();
        var result = await runtime.RunOnceAsync(Parameters(0)).WaitAsync(Deadline);
        Assert.AreEqual(RunState.Completed, result.State);
        Assert.AreEqual(result, store.Document!.CurrentRun!.Snapshot);
        Assert.AreEqual(0, requests.StartedCount);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.IsLessThan(Deadline, elapsed.Elapsed);
            await Task.Delay(1);
        }
    }
}

internal sealed class MemoryRunStateStore : IRunStateStore
{
    public RunStateDocument? Document { get; set; }
    public RunStateDocument? Attempted { get; private set; }
    public int FailAt { get; init; }
    public int BlockAt { get; init; }
    private int _writes;
    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<RunStateDocument?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Document);
    public async Task SaveAsync(RunStateDocument document, CancellationToken cancellationToken = default)
    {
        document.Validate();
        Attempted = document;
        var count = Interlocked.Increment(ref _writes);
        if (count == FailAt) throw new IOException("private-write-error");
        if (count == BlockAt)
        {
            Blocked.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Document = document;
    }
}
