using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class ManualPanelRunTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static Task<CompletionRequest> Input(CancellationToken token) => Task.FromResult(new CompletionRequest("开发验证文本", 1024, 200_000));

    [TestMethod]
    public async Task UserPathWithHttpMockCompletesPersistentRoundAndReopenRequiresConfirmation()
    {
        using var handler = new MockSenseNovaHandler(delay: TimeSpan.Zero);
        using var http = new HttpClient(handler) { BaseAddress = new(SenseNovaDefaults.BaseUrl + "/") };
        var client = new SenseNovaHttpClient(http);
        var store = new MemoryRunStateStore();
        await using (var vm = new MainWindowViewModel(client, mockMode: false, runStore: store)
            { ApiKey = MockCredential.Value, Percentage = 1 })
        {
            await vm.InitializeAsync();
            Assert.AreEqual(0, handler.RequestCount);
            Assert.IsFalse(vm.StartRunCommand.CanExecute(null));
            await vm.ReadModelsAsync();
            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsTrue(vm.StartRunCommand.CanExecute(null));
            await vm.StartRunAsync().WaitAsync(Deadline);
            Assert.AreEqual(RunState.Completed, vm.Run.State);
            Assert.AreEqual(RunEndReason.BudgetRemainder, vm.Run.EndReason);
            Assert.IsTrue(vm.Run.ConfirmedUsage.TotalTokens > 0);
            Assert.IsTrue(vm.Run.ConfirmedUsage.TotalTokens < vm.TargetTokens);
            Assert.AreEqual(vm.Run.CompletedRequests, handler.SuccessfulCompletionCount);
            Assert.AreEqual(vm.Run, store.Document!.CurrentRun!.Snapshot);
            Assert.AreEqual(0, vm.Run.InFlightRequests);
        }
        var before = handler.RequestCount;
        await using var reopened = new MainWindowViewModel(client, mockMode: false, runStore: store)
            { ApiKey = MockCredential.Value, Percentage = 1 };
        await reopened.InitializeAsync();
        Assert.AreEqual(RecoveryState.AwaitingConfirmation, reopened.Recovery.State);
        Assert.IsFalse(reopened.StartRunCommand.CanExecute(null));
        await reopened.ConfirmRecoveryAsync();
        Assert.AreEqual(RecoveryState.Ready, reopened.Recovery.State);
        Assert.AreEqual(before, handler.RequestCount);
        Assert.IsFalse(reopened.StartRunCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task RunningAndPausedStatusNeverClaimConsumptionHasNotStarted()
    {
        var inputReady = new TaskCompletionSource<CompletionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new PanelClient();
        await using var vm = new MainWindowViewModel(client, mockMode: false,
            runStore: new MemoryRunStateStore(), createInput: token => inputReady.Task.WaitAsync(token))
            { ApiKey = MockCredential.Value, Percentage = 1 };
        await vm.InitializeAsync();
        await vm.ReadModelsAsync();
        var pending = vm.StartRunAsync();
        Assert.IsTrue(vm.StatusText.Contains("尚未开始消耗", StringComparison.Ordinal));
        inputReady.SetResult(await Input(CancellationToken.None));
        await UntilAsync(() => client.Pending.Count == 3 && vm.Run.State == RunState.Running);
        Assert.IsFalse(vm.StatusText.Contains("尚未开始消耗", StringComparison.Ordinal),
            "请求开始后不能继续展示准备期提示。");
        vm.PauseRun();
        Assert.IsTrue(vm.StatusText.Contains("暂停", StringComparison.Ordinal));
        foreach (var request in client.Pending) request.TrySetResult(new(170_000, 8, 170_008));
        await UntilAsync(() => vm.Run.InFlightRequests == 0);
        vm.ResumeRun();
        Assert.IsTrue(vm.StatusText.Contains("运行中", StringComparison.Ordinal));
        await UntilAsync(() => client.Pending.Count > 3);
        var stopping = vm.StopRunAsync();
        foreach (var request in client.Pending) request.TrySetResult(new(170_000, 8, 170_008));
        await stopping.WaitAsync(Deadline);
        await pending.WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, vm.Run.State);
        Assert.IsFalse(vm.StatusText.Contains("尚未开始消耗", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PauseResumeStopAndRepeatStartKeepOneRoundAndPersistBeforeDispatch()
    {
        var client = new PanelClient();
        var store = new MemoryRunStateStore();
        client.BeforeDispatch = () => Assert.IsTrue(store.Document!.CurrentRun!.Snapshot.InFlightRequests > 0);
        await using var vm = new MainWindowViewModel(client, mockMode: false, runStore: store, createInput: Input)
            { ApiKey = MockCredential.Value, Percentage = 1 };
        await vm.InitializeAsync();
        await vm.ReadModelsAsync();
        var pending = vm.StartRunAsync();
        await UntilAsync(() => client.Pending.Count == 3);
        await vm.StartRunAsync();
        Assert.AreEqual(3, client.Pending.Count);
        vm.ApiKey = "cannot-replace-active-key";
        Assert.AreEqual(MockCredential.Value, vm.ApiKey);
        vm.PauseRun();
        Assert.IsTrue(vm.CanResume);
        foreach (var request in client.Pending) request.TrySetResult(new(170_000, 8, 170_008));
        await UntilAsync(() => vm.Run.InFlightRequests == 0);
        Assert.AreEqual(RunState.Paused, vm.Run.State);
        Assert.AreEqual(3, client.Pending.Count);
        vm.ResumeRun();
        await UntilAsync(() => client.Pending.Count > 3);
        var stopping = vm.StopRunAsync();
        foreach (var request in client.Pending) request.TrySetResult(new(170_000, 8, 170_008));
        await stopping.WaitAsync(Deadline);
        await pending.WaitAsync(Deadline);
        Assert.AreEqual(0, vm.Run.InFlightRequests);
        Assert.IsFalse(vm.Run.ReviewRequired);
        Assert.AreEqual(RunState.Stopped, vm.Run.State);
        Assert.AreEqual(vm.Run, store.Document!.CurrentRun!.Snapshot);
        Assert.IsTrue(vm.StartRunCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task UnknownUsageRequiresExplicitReviewAndConfirmationSendsNothing()
    {
        var client = new PanelClient { Failure = new(ApiFailureKind.RateOrQuotaLimit, "untrusted secret") };
        var store = new MemoryRunStateStore();
        await using var vm = new MainWindowViewModel(client, mockMode: false, runStore: store, createInput: Input)
            { ApiKey = MockCredential.Value, Percentage = 1 };
        await vm.InitializeAsync();
        await vm.ReadModelsAsync();
        var pending = vm.StartRunAsync();
        await UntilAsync(() => vm.Run.State == RunState.AwaitingReview);
        Assert.IsFalse(vm.CanResume);
        await vm.StopRunAsync().WaitAsync(Deadline);
        await pending.WaitAsync(Deadline);
        Assert.AreEqual(RecoveryState.NeedsReview, vm.Recovery.State);
        Assert.IsTrue(vm.Run.UnknownUsageRequests > 0);
        Assert.IsFalse(vm.ConfirmRecoveryCommand.CanExecute(null));
        await vm.ConfirmRecoveryAsync();
        Assert.AreEqual(RecoveryState.NeedsReview, vm.Recovery.State);
        var before = client.Calls;
        vm.ReviewCompleted = true;
        await vm.ConfirmRecoveryAsync();
        Assert.AreEqual(RecoveryState.Ready, vm.Recovery.State);
        Assert.AreEqual(before, client.Calls);
        Assert.IsTrue(store.Document!.CurrentRun!.Snapshot.UnknownUsageRequests > 0);
        Assert.IsFalse(vm.StatusText.Contains("untrusted secret"));
    }

    [TestMethod]
    public async Task StorageFailureStaysBlockedAfterKeyChangeAndRevalidation()
    {
        var client = new PanelClient();
        var store = new MemoryRunStateStore { FailAt = 2 };
        await using var vm = new MainWindowViewModel(client, mockMode: false, runStore: store, createInput: Input)
            { ApiKey = MockCredential.Value, Percentage = 1 };
        await vm.InitializeAsync();
        await vm.ReadModelsAsync();
        await vm.StartRunAsync().WaitAsync(Deadline);
        Assert.AreEqual(0, client.Calls);
        Assert.AreEqual(RecoveryState.StorageFailed, vm.Recovery.State);
        vm.ApiKey = "development-only-changed-key";
        await vm.ReadModelsAsync();
        Assert.IsFalse(vm.StartRunCommand.CanExecute(null));
        vm.ReviewCompleted = true;
        await vm.ConfirmRecoveryAsync();
        Assert.AreEqual(RecoveryState.StorageFailed, vm.Recovery.State);
        Assert.AreEqual(0, client.Calls);
    }

    [TestMethod]
    public async Task DisposalCancelsRunWaitsFinalCheckpointAndRejectsFurtherCalls()
    {
        var client = new PanelClient();
        var store = new MemoryRunStateStore();
        var vm = new MainWindowViewModel(client, mockMode: false, runStore: store, createInput: Input)
            { ApiKey = MockCredential.Value, Percentage = 1 };
        await vm.InitializeAsync();
        await vm.ReadModelsAsync();
        var pending = vm.StartRunAsync();
        await UntilAsync(() => client.Calls > 0);
        await vm.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.IsTrue(pending.IsCompleted);
        Assert.AreEqual(0, store.Document!.CurrentRun!.Snapshot.InFlightRequests);
        Assert.IsTrue(store.Document.CurrentRun.Snapshot.ReviewRequired);
        Assert.AreEqual("", vm.ApiKey);
        Assert.IsFalse(vm.StartRunCommand.CanExecute(null));
        var before = client.Calls;
        await vm.StartRunAsync();
        Assert.AreEqual(before, client.Calls);
    }

    [TestMethod]
    public async Task StopDuringInputPreparationDoesNotDispatchOrCreateRun()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new PanelClient();
        var store = new MemoryRunStateStore();
        await using var vm = new MainWindowViewModel(client, mockMode: false, runStore: store,
            createInput: async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return await Input(token); })
            { ApiKey = MockCredential.Value, Percentage = 1 };
        await vm.InitializeAsync();
        await vm.ReadModelsAsync();
        var pending = vm.StartRunAsync();
        await entered.Task.WaitAsync(Deadline);
        await vm.StopRunAsync();
        await pending.WaitAsync(Deadline);
        Assert.AreEqual(0, client.Calls);
        Assert.IsNull(store.Document);
        Assert.IsFalse(vm.IsRunActive);
    }

    [TestMethod]
    public async Task ZeroMissingCapabilitiesAndUnconfirmedModelsNeverPrepareOrConsume()
    {
        var client = new PanelClient();
        var prepared = 0;
        await using var vm = new MainWindowViewModel(client, mockMode: false, runStore: new MemoryRunStateStore(),
            createInput: token => { prepared++; return Input(token); }) { ApiKey = MockCredential.Value };
        await vm.InitializeAsync();
        await vm.ReadModelsAsync();
        vm.Percentage = 0;
        await vm.StartRunAsync();
        vm.Percentage = 1;
        vm.SelectedModel = new(SenseNovaDefaults.RebateModel, null, null, true);
        await vm.StartRunAsync();
        vm.SelectedModel = new("unconfirmed", 262144, 65536, false);
        await vm.StartRunAsync();
        Assert.AreEqual(0, prepared + client.Calls);
    }

    [TestMethod]
    public void GeneratedInputIsBoundedCancelableAndHasSeparateEstimate()
    {
        var input = CompletionInput.Create();
        Assert.AreEqual(340_000, input.Input.Length);
        Assert.AreEqual(240_000L, input.EstimatedInputTokens);
        input.Validate(new(SenseNovaDefaults.RebateModel, 262144, 65536, true));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => CompletionInput.Create(canceled.Token));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class PanelClient : ISenseNovaClient, ISenseNovaCompletionClient
    {
        public readonly System.Collections.Concurrent.ConcurrentBag<TaskCompletionSource<TokenUsage>> Pending = [];
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Action? BeforeDispatch { get; set; }
        public SenseNovaApiException? Failure { get; init; }
        public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string key, CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([new(SenseNovaDefaults.RebateModel, 262144, 65536, true)]);
        public Task<TokenUsage> ProbeAsync(string key, ModelInfo model, CancellationToken token = default) => throw new AssertFailedException("不应执行短探针");
        public async Task<TokenUsage> CompleteAsync(string key, ModelInfo model, CompletionRequest request, CancellationToken token = default)
        {
            BeforeDispatch?.Invoke();
            Interlocked.Increment(ref _calls);
            if (Failure is not null) throw Failure;
            var completion = new TaskCompletionSource<TokenUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(completion);
            return await completion.Task.WaitAsync(token);
        }
    }
}
