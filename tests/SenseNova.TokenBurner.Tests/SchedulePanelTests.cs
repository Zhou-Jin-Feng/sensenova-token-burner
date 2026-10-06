using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class SchedulePanelTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    private static Task<CompletionRequest> Input(CancellationToken token)
        => Task.FromResult(new CompletionRequest("计划行为验证文本", 1024, 200_000));

    [TestMethod]
    public async Task EnableStartsNowPersistsIntentAndWaitsAtDefaultFiveHourBoundary()
    {
        var time = new ManualRunTimeProvider();
        var client = new PlanClient();
        var store = new MemoryRunStateStore();
        var settings = new PlanSettings();
        await using var vm = Create(client, store, time, settings);
        await Ready(vm);
        await vm.EnableScheduleAsync();
        await Until(() => vm.Schedule.LastRun?.State == RunState.Completed && vm.IsIdle);
        Assert.IsTrue(vm.ScheduleEnabled);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(5), vm.Schedule.NextRunUtc);
        Assert.AreEqual(1L, vm.Schedule.StartedRuns);
        Assert.IsTrue(store.Document!.PlanRequested);
        Assert.AreEqual(5m, settings.Value!.IntervalHours);
        Assert.IsFalse(vm.StartRunCommand.CanExecute(null));
        StringAssert.Contains(vm.NextRunLabel, "本地时间");
        var calls = client.Calls;
        var saved = store.Document;
        await Task.Delay(100);
        Assert.AreEqual(calls, client.Calls);
        Assert.AreSame(saved, store.Document);
        await Until(() => time.HasTimer(TimeSpan.FromMinutes(1)));
        time.Advance(TimeSpan.FromHours(5));
        await Until(() => vm.Schedule.StartedRuns == 2 && client.Calls > calls
            && vm.Schedule.CurrentRun.State == RunState.Completed);
        Assert.IsTrue(client.Calls > calls);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(10), vm.Schedule.NextRunUtc);
        await vm.DisableScheduleAsync();
        await Until(() => vm.CanEditCredential);
        Assert.IsFalse(store.Document!.PlanRequested);
        Assert.IsNull(vm.Schedule.NextRunUtc);
    }

    [TestMethod]
    public async Task DisableDuringPausedRoundKeepsRoundAndCredentialLockUntilStopped()
    {
        var client = new PlanClient { Hold = true };
        await using var vm = Create(client, new MemoryRunStateStore(), new());
        await Ready(vm);
        await vm.EnableScheduleAsync();
        await Until(() => client.Pending.Count == 3 && vm.CanPause);
        vm.PauseRun();
        client.Release();
        await Until(() => vm.Run.InFlightRequests == 0);
        await vm.DisableScheduleAsync();
        Assert.IsFalse(vm.ScheduleEnabled);
        Assert.AreEqual(RunState.Paused, vm.Schedule.CurrentRun.State);
        Assert.IsFalse(vm.CanEditCredential);
        Assert.IsTrue(vm.CanResume);
        vm.ApiKey = "replacement-not-allowed";
        Assert.AreEqual(MockCredential.Value, vm.ApiKey);
        await vm.StopRunAsync();
        await Until(() => vm.CanEditCredential);
        Assert.AreEqual(RunState.Stopped, vm.Run.State);
    }

    [TestMethod]
    public async Task StopRoundKeepsPlanAndSkippingPausedBoundaryNeverStartsOverlap()
    {
        var time = new ManualRunTimeProvider();
        var client = new PlanClient { Hold = true };
        await using var vm = Create(client, new MemoryRunStateStore(), time);
        await Ready(vm);
        await vm.EnableScheduleAsync();
        await Until(() => client.Pending.Count == 3 && vm.CanPause);
        vm.PauseRun();
        client.Release();
        await Until(() => vm.Schedule.CurrentRun.InFlightRequests == 0 && time.HasTimer(TimeSpan.FromMinutes(1)));
        time.Advance(TimeSpan.FromHours(5));
        await Until(() => vm.Schedule.SkippedCycles == 1);
        Assert.AreEqual(3, client.Calls);
        await vm.StopRunAsync();
        await Until(() => vm.IsIdle);
        Assert.IsTrue(vm.ScheduleEnabled);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(10), vm.Schedule.NextRunUtc);
        Assert.IsFalse(vm.StartRunCommand.CanExecute(null));
        await vm.DisableScheduleAsync();
    }

    [TestMethod]
    public async Task SavedChangesApplyNextRoundAndZeroClosesOnlyFuturePlan()
    {
        var time = new ManualRunTimeProvider();
        var client = new PlanClient();
        var settings = new PlanSettings();
        var store = new MemoryRunStateStore();
        await using var vm = Create(client, store, time, settings);
        await Ready(vm);
        await vm.EnableScheduleAsync();
        await Until(() => vm.IsIdle && vm.Schedule.LastRun is not null);
        vm.Percentage = 2;
        vm.IntervalHoursText = "2.5";
        var before = client.Calls;
        await vm.SaveConfigurationAsync();
        Assert.AreEqual(ConsumptionPolicy.GetTargetTokens(2), vm.Schedule.Configuration!.Run.TargetTokens);
        Assert.AreEqual(ConsumptionPolicy.GetTargetTokens(1), vm.Schedule.LastRun!.Parameters!.TargetTokens);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddHours(2.5), vm.Schedule.NextRunUtc);
        Assert.AreEqual(before, client.Calls);
        Assert.AreEqual(2, settings.Value!.Percentage);
        await Until(() => time.HasTimer(TimeSpan.FromMinutes(1)));
        time.Advance(TimeSpan.FromHours(2.5));
        await Until(() => vm.Schedule.StartedRuns == 2 && vm.Schedule.CurrentRun.State == RunState.Completed && vm.IsIdle);
        Assert.AreEqual(ConsumptionPolicy.GetTargetTokens(2), vm.Schedule.CurrentRun.Parameters!.TargetTokens);
        vm.Percentage = 0;
        await vm.SaveConfigurationAsync();
        await Until(() => vm.CanEditCredential);
        Assert.IsFalse(vm.ScheduleEnabled);
        Assert.IsFalse(store.Document!.PlanRequested);
        Assert.AreEqual(RunState.Completed, vm.Run.State);
    }

    [TestMethod]
    public async Task ReopenShowsPlanButConfirmAndConnectionDoNotEnableOrConsume()
    {
        var client = new PlanClient();
        var store = new MemoryRunStateStore();
        var settings = new PlanSettings();
        await using (var vm = Create(client, store, new(), settings))
        {
            await Ready(vm);
            vm.IntervalHoursText = "2.5";
            await vm.EnableScheduleAsync();
            await Until(() => vm.IsIdle && vm.Schedule.LastRun is not null);
        }
        var before = client.Calls;
        await using var reopened = Create(client, store, new(), settings);
        await reopened.InitializeAsync();
        Assert.IsFalse(reopened.ScheduleEnabled);
        Assert.AreEqual("2.5", reopened.IntervalHoursText);
        StringAssert.Contains(reopened.SavedPlanLabel, "2.5");
        Assert.IsFalse(reopened.EnableScheduleCommand.CanExecute(null));
        await reopened.ConfirmRecoveryAsync();
        Assert.AreEqual(before, client.Calls);
        await reopened.ReadModelsAsync();
        Assert.IsFalse(reopened.ScheduleEnabled);
        Assert.AreEqual(before, client.Calls);
        Assert.IsTrue(reopened.EnableScheduleCommand.CanExecute(null));
        await reopened.EnableScheduleAsync();
        await Until(() => client.Calls > before);
    }

    [TestMethod]
    public async Task LimitProtectionClosesFuturePlanAndReviewNeverRestartsIt()
    {
        var client = new PlanClient { Failure = new(ApiFailureKind.RateOrQuotaLimit, "private-response") };
        var store = new MemoryRunStateStore();
        await using var vm = Create(client, store, new());
        await Ready(vm);
        await vm.EnableScheduleAsync();
        await Until(() => vm.Schedule.State == ScheduleState.Blocked && vm.Run.State == RunState.AwaitingReview);
        Assert.IsNull(vm.Schedule.NextRunUtc);
        Assert.IsFalse(vm.CanResume);
        await vm.StopRunAsync();
        await Until(() => vm.IsIdle && vm.ConfirmRecoveryCommand.CanExecute(null) == false);
        Assert.AreEqual(RecoveryState.NeedsReview, vm.Recovery.State);
        vm.ReviewCompleted = true;
        await Until(() => vm.ConfirmRecoveryCommand.CanExecute(null));
        var before = client.Calls;
        await vm.ConfirmRecoveryAsync();
        Assert.AreEqual(before, client.Calls);
        Assert.IsFalse(vm.ScheduleEnabled);
        Assert.IsFalse(store.Document!.PlanRequested);
        Assert.IsFalse(vm.StatusText.Contains("private-response", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FailedPlanWriteDispatchesNothingAndKeyChangeCannotUnlockStorage()
    {
        var client = new PlanClient();
        var store = new MemoryRunStateStore { FailAt = 1 };
        await using var vm = Create(client, store, new());
        await Ready(vm);
        await vm.EnableScheduleAsync();
        Assert.AreEqual(0, client.Calls);
        Assert.AreEqual(RecoveryState.StorageFailed, vm.Recovery.State);
        Assert.IsFalse(vm.ScheduleEnabled);
        vm.ApiKey = "development-only-new-value";
        await vm.ReadModelsAsync();
        Assert.IsFalse(vm.EnableScheduleCommand.CanExecute(null));
        await vm.EnableScheduleAsync();
        Assert.AreEqual(0, client.Calls);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("bad")]
    [DataRow("0.000000001")]
    [DataRow("79228162514264337593543950335")]
    public async Task InvalidIntervalPreventsPreparationAndPlanWrites(string interval)
    {
        var client = new PlanClient();
        var store = new MemoryRunStateStore();
        await using var vm = Create(client, store, new());
        await Ready(vm);
        vm.IntervalHoursText = interval;
        Assert.IsFalse(vm.EnableScheduleCommand.CanExecute(null));
        await vm.EnableScheduleAsync();
        Assert.AreEqual(0, client.Calls);
        Assert.IsNull(store.Document);
    }

    [TestMethod]
    public async Task DisposeActivePlanStopsRequestsPersistsUnknownAndNeverRestarts()
    {
        var client = new PlanClient { Hold = true };
        var time = new ManualRunTimeProvider();
        var store = new MemoryRunStateStore();
        var vm = Create(client, store, time);
        await Ready(vm);
        await vm.EnableScheduleAsync();
        await Until(() => client.Calls > 0);
        var closing = vm.DisposeAsync().AsTask();
        await Until(() => time.HasTimer(TimeSpan.FromSeconds(15)));
        time.Advance(TimeSpan.FromSeconds(15));
        await closing.WaitAsync(Deadline);
        await Until(() => store.Document!.CurrentRun!.Snapshot.InFlightRequests == 0);
        Assert.AreEqual(ScheduleState.Shutdown, vm.Schedule.State);
        Assert.IsFalse(store.Document!.PlanRequested);
        Assert.AreEqual(0, store.Document.CurrentRun!.Snapshot.InFlightRequests);
        Assert.IsTrue(store.Document.CurrentRun.Snapshot.ReviewRequired);
        Assert.IsFalse(vm.EnableScheduleCommand.CanExecute(null));
        Assert.AreEqual("", vm.ApiKey);
        var before = client.Calls;
        time.Advance(TimeSpan.FromHours(20));
        Assert.AreEqual(before, client.Calls);
    }

    [TestMethod]
    public async Task RepeatedEnableAndWaitingKeyChangeNeverCreateAnotherPlanOrValidationCall()
    {
        var client = new PlanClient();
        await using var vm = Create(client, new MemoryRunStateStore(), new());
        await Ready(vm);
        await vm.EnableScheduleAsync();
        await Until(() => vm.IsIdle && vm.Schedule.LastRun is not null);
        var before = client.Calls;
        await vm.EnableScheduleAsync();
        await vm.StartRunAsync();
        vm.ApiKey = "cannot-replace-planned-credential";
        await vm.ReadModelsAsync();
        Assert.AreEqual(MockCredential.Value, vm.ApiKey);
        Assert.AreEqual(1, client.ModelCalls);
        Assert.AreEqual(before, client.Calls);
        Assert.AreEqual(1L, vm.Schedule.StartedRuns);
    }

    [TestMethod]
    public async Task InvalidSavedIntervalKeepsExistingSettingsAndPlanWithoutRequests()
    {
        var client = new PlanClient();
        var settings = new PlanSettings();
        await using var vm = Create(client, new MemoryRunStateStore(), new(), settings);
        await Ready(vm);
        await vm.EnableScheduleAsync();
        await Until(() => vm.IsIdle && vm.Schedule.LastRun is not null);
        var original = settings.Value;
        var calls = client.Calls;
        vm.IntervalHoursText = "0.000000001";
        await vm.SaveConfigurationAsync();
        Assert.AreSame(original, settings.Value);
        Assert.IsTrue(vm.ScheduleEnabled);
        Assert.AreEqual(5m, vm.Schedule.Configuration!.IntervalHours);
        Assert.AreEqual(calls, client.Calls);
        StringAssert.Contains(vm.StatusText, "配置无效");
    }

    [TestMethod]
    public async Task StopDuringPlanPreparationNeverPersistsIntentOrDispatches()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new PlanClient();
        var store = new MemoryRunStateStore();
        await using var vm = new MainWindowViewModel(client, mockMode: false, runStore: store,
            createInput: async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return await Input(token); })
            { ApiKey = MockCredential.Value, Percentage = 1 };
        await Ready(vm);
        var pending = vm.EnableScheduleAsync();
        await entered.Task.WaitAsync(Deadline);
        await vm.StopRunAsync();
        await pending.WaitAsync(Deadline);
        Assert.AreEqual(0, client.Calls);
        Assert.IsNull(store.Document);
        Assert.IsFalse(vm.ScheduleEnabled);
        Assert.IsTrue(vm.CanEditCredential);
    }

    [TestMethod]
    public async Task FailedUpdateClosesPlanAndRetainsOriginalSavedPlan()
    {
        var time = new ManualRunTimeProvider();
        var client = new PlanClient();
        var store = new SwitchablePlanStore();
        await using var vm = Create(client, store, time, new PlanSettings());
        await Ready(vm);
        await vm.EnableScheduleAsync();
        await Until(() => vm.IsIdle && vm.Schedule.LastRun is not null);
        var original = store.Document!.Plan;
        var calls = client.Calls;
        store.Fail = true;
        vm.Percentage = 2;
        await vm.SaveConfigurationAsync();
        Assert.IsFalse(vm.ScheduleEnabled);
        Assert.AreEqual(RecoveryState.StorageFailed, vm.Recovery.State);
        Assert.AreEqual(original, store.Document.Plan);
        Assert.IsFalse(vm.EnableScheduleCommand.CanExecute(null));
        time.Advance(TimeSpan.FromHours(5));
        Assert.AreEqual(calls, client.Calls);
    }

    private static MainWindowViewModel Create(PlanClient client, IRunStateStore store,
        ManualRunTimeProvider time, PlanSettings? settings = null)
        => new(client, settings, mockMode: false, runStore: store, createInput: Input, timeProvider: time)
            { ApiKey = MockCredential.Value, Percentage = 1 };
    private static async Task Ready(MainWindowViewModel vm) { await vm.InitializeAsync(); await vm.ReadModelsAsync(); }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private sealed class PlanSettings : IUserSettingsStore
    {
        public AppSettings? Value { get; private set; }
        public Task<AppSettings?> LoadAsync(CancellationToken token = default) => Task.FromResult(Value);
        public Task SaveAsync(AppSettings settings, CancellationToken token = default) { Value = settings; return Task.CompletedTask; }
    }
    private sealed class PlanClient : ISenseNovaClient, ISenseNovaCompletionClient
    {
        public ConcurrentBag<TaskCompletionSource<TokenUsage>> Pending { get; } = [];
        public bool Hold { get; init; }
        public SenseNovaApiException? Failure { get; init; }
        private int _calls;
        public int ModelCalls { get; private set; }
        public int Calls => Volatile.Read(ref _calls);
        public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string key, CancellationToken token = default)
        {
            ModelCalls++;
            return Task.FromResult<IReadOnlyList<ModelInfo>>([new(SenseNovaDefaults.RebateModel, 262144, 65536, true)]);
        }
        public Task<TokenUsage> ProbeAsync(string key, ModelInfo model, CancellationToken token = default)
            => throw new AssertFailedException("不应触发短探针");
        public async Task<TokenUsage> CompleteAsync(string key, ModelInfo model, CompletionRequest request, CancellationToken token = default)
        {
            Interlocked.Increment(ref _calls);
            if (Failure is not null) throw Failure;
            if (!Hold) return new(170_000, 8, 170_008);
            var completion = new TaskCompletionSource<TokenUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(completion);
            return await completion.Task.WaitAsync(token);
        }
        public void Release() { foreach (var pending in Pending) pending.TrySetResult(new(170_000, 8, 170_008)); }
    }
    private sealed class SwitchablePlanStore : IRunStateStore
    {
        public bool Fail { get; set; }
        public RunStateDocument? Document { get; private set; }
        public Task<RunStateDocument?> LoadAsync(CancellationToken token = default) => Task.FromResult(Document);
        public Task SaveAsync(RunStateDocument document, CancellationToken token = default)
        {
            if (Fail) throw new IOException("test-storage-failure");
            Document = document;
            return Task.CompletedTask;
        }
    }
}
