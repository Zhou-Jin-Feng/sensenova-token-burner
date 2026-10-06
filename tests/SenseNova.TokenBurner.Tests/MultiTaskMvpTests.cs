using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class MultiTaskMvpTests
{
    private const string Key1 = "mock-only-not-a-real-key-1";
    private const string Key2 = "mock-only-not-a-real-key-2";
    private static TaskConfiguration Config(string name, long target = 100) => new() { DisplayName = name, CustomTargetTokens = target };
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static MultiTaskCoordinator Coordinator(TestFolder folder, FakeMultiApi api, TimeProvider? time = null)
        => new(new(new JsonTaskCatalogStore(folder.Path, StorageProfile.Mock)), api, api, true,
            _ => Task.FromResult(new CompletionRequest("mock input", 2, 8)), time);

    [TestMethod]
    public async Task TwoKeysPersistEncryptedWithRemarksAndIndependentConfigurationWithoutRequestsOnReopen()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi();
        await using (var first = Coordinator(folder, api))
        {
            await first.InitializeAsync();
            var a = await first.AddAsync(Key1, Config("key-1") with { Percentage = 50, IntervalHours = 2.5m, QuotaGroup = "shared" });
            await first.AddAsync(Key2, Config("key-2", 200) with { Percentage = 75, IntervalHours = 5, Enabled = false });
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => first.AddAsync(Key1));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => first.SaveAsync(a.Configuration with { QuotaGroup = Key2 }));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => first.AddAsync("mock-third", Config(Key1)));
        }
        await using var second = Coordinator(folder, api); await second.InitializeAsync();
        Assert.AreEqual(2, second.Tasks.Count); Assert.AreEqual("key-1", second.Tasks[0].Configuration.DisplayName);
        Assert.AreEqual(2.5m, second.Tasks[0].Configuration.IntervalHours); Assert.AreEqual(50, second.Tasks[0].Configuration.Percentage);
        Assert.AreEqual(200L, second.Tasks[1].Configuration.TargetTokens); Assert.IsFalse(second.Tasks[1].Configuration.Enabled);
        Assert.AreEqual(0, api.TotalCalls);
        foreach (var path in Directory.GetFiles(folder.Path, "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            Assert.IsFalse(text.Contains(Key1, StringComparison.Ordinal) || text.Contains(Key2, StringComparison.Ordinal));
        }
        Assert.AreEqual(Key1, await second.Workspace.Storage.Credentials(second.Tasks[0].Configuration).LoadAsync());
    }

    [TestMethod]
    public async Task LegacyUpgradeKeepsOriginalSettingsCiphertextAndHistoryBytes()
    {
        using var folder = new TestFolder(); var settings = new JsonUserSettingsStore(folder.Path);
        await settings.SaveAsync(new() { Percentage = 50, IntervalHours = 2.5m });
        await new DpapiCredentialStore(folder.Path).SaveAsync(Key1);
        await new JsonRunStateStore(folder.Path).SaveAsync(RunStateDocument.Empty);
        var original = Directory.GetFiles(folder.Path).ToDictionary(path => path, File.ReadAllBytes);
        var store = new JsonTaskCatalogStore(folder.Path, StorageProfile.Mock); var catalog = await store.LoadAsync();
        Assert.AreEqual(1, catalog.Tasks.Length); Assert.IsTrue(catalog.Tasks[0].UsesLegacyStorage);
        Assert.AreEqual(50, catalog.Tasks[0].Percentage); Assert.AreEqual(Key1, await store.Credentials(catalog.Tasks[0]).LoadAsync());
        foreach (var (path, bytes) in original) CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(catalog.Tasks[0].Id, (await store.LoadAsync()).Tasks[0].Id);
    }

    [TestMethod]
    [DataRow("Id")]
    [DataRow("UsesLegacyStorage")]
    [DataRow("CredentialSaved")]
    public async Task MissingStorageIdentityBlocksInsteadOfInventingTask(string field)
    {
        using var folder = new TestFolder(); var workspace = new TaskWorkspace(new(folder.Path, StorageProfile.Mock));
        await workspace.InitializeAsync(); await workspace.AddAsync(Key1);
        var path = Path.Combine(folder.Path, "tasks.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!; node["Tasks"]![0]!.AsObject().Remove(field);
        var invalid = node.ToJsonString(); await File.WriteAllTextAsync(path, invalid);
        await Assert.ThrowsExactlyAsync<LocalStorageException>(() => new JsonTaskCatalogStore(folder.Path, StorageProfile.Mock).LoadAsync());
        Assert.AreEqual(invalid, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task RunAllIsParallelWithGlobalCapAndIndependentTargetsAndSummary()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi();
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var a = await coordinator.AddAsync(Key1, Config("key-1", 100) with { QuotaGroup = "account" });
        var b = await coordinator.AddAsync(Key2, Config("key-2", 200) with { QuotaGroup = "account" });
        StringAssert.Contains(coordinator.RunAllPreview(), "300");
        await coordinator.RunAllAsync().WaitAsync(Deadline);
        Assert.AreEqual(3, api.MaximumActive); Assert.IsTrue(api.CrossKeyOverlap);
        Assert.AreEqual(100L, a.Snapshot.ConfirmedUsage.TotalTokens); Assert.AreEqual(200L, b.Snapshot.ConfirmedUsage.TotalTokens);
        Assert.AreEqual(0, coordinator.Limiter.ActiveRequests); Assert.AreEqual(1d, coordinator.OverallProgress);
        StringAssert.Contains(coordinator.BatchSummary, "完成2");
    }

    [TestMethod]
    public async Task IndependentAndAllPauseResumeStopDoNotOverlapSameTask()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { Delay = TimeSpan.FromMilliseconds(30) };
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var a = await coordinator.AddAsync(Key1, Config("key-1", 1_000_000));
        var b = await coordinator.AddAsync(Key2, Config("key-2", 1_000_000));
        var runA = coordinator.StartAsync(a.Configuration.Id); var runB = coordinator.StartAsync(b.Configuration.Id);
        await UntilAsync(() => api.CrossKeyOverlap);
        await coordinator.StartAsync(a.Configuration.Id); // 重复点击被拒绝，不产生第二个引擎。
        await coordinator.PauseAsync(a.Configuration.Id); await UntilAsync(() => a.Snapshot.InFlightRequests == 0);
        var before = a.Snapshot.ConfirmedUsage.TotalTokens;
        await UntilAsync(() => b.Snapshot.ConfirmedUsage.TotalTokens > before + 50);
        Assert.AreEqual(before, a.Snapshot.ConfirmedUsage.TotalTokens);
        Assert.IsTrue(coordinator.Resume(a.Configuration.Id));
        await coordinator.PauseAllAsync(); await UntilAsync(() => a.Snapshot.InFlightRequests + b.Snapshot.InFlightRequests == 0);
        Assert.AreEqual(RunState.Paused, a.Snapshot.State); Assert.AreEqual(RunState.Paused, b.Snapshot.State);
        Assert.AreEqual(2, coordinator.ResumeAll());
        await coordinator.StopAllAsync().WaitAsync(Deadline); await Task.WhenAll(runA, runB).WaitAsync(Deadline);
        Assert.AreEqual(RunState.Stopped, a.Snapshot.State); Assert.AreEqual(RunState.Stopped, b.Snapshot.State);
        Assert.IsTrue(api.MaximumActive <= 3);
    }

    [TestMethod]
    public async Task OneAuthenticationFailureDoesNotStopOtherTask()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { FailKey = Key1 };
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var a = await coordinator.AddAsync(Key1, Config("key-1")); var b = await coordinator.AddAsync(Key2, Config("key-2"));
        await coordinator.RunAllAsync().WaitAsync(Deadline);
        Assert.AreEqual(RunState.Faulted, a.Snapshot.State); Assert.AreEqual(RunState.Completed, b.Snapshot.State);
        StringAssert.Contains(coordinator.BatchSummary, "完成1");
    }

    [TestMethod]
    public async Task InterruptedProgressRequiresReviewAndContinuesRemainingWithoutReplayingUnknown()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi(); Guid id;
        var parameters = new RunParameters(SenseNovaDefaults.RebateModel, 100) { RequestTokenReservation = 9 };
        var interrupted = new RunSnapshot(RunState.Running, parameters, new(36, 4, 40), 4, 1, 0, null) { ReservedTokens = 9 };
        var record = new RunRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, interrupted);
        await using (var first = Coordinator(folder, api))
        {
            await first.InitializeAsync(); var row = await first.AddAsync(Key1, Config("key-1")); id = row.Configuration.Id;
            await first.Workspace.Storage.RunState(row.Configuration).SaveAsync(RunStateDocument.Empty with { CurrentRun = record });
        }
        await using var second = Coordinator(folder, api); await second.InitializeAsync(); var task = second.Tasks[0];
        Assert.AreEqual(40L, task.Snapshot.ConfirmedUsage.TotalTokens); Assert.AreEqual(0, api.TotalCalls);
        await second.StartAsync(id, continueRemaining: true); Assert.AreEqual(0, api.TotalCalls);
        Assert.AreEqual(RecoveryState.NeedsReview, task.Session.Recovery.State);
        await task.Session.ConfirmRecoveryAsync(true); Assert.AreEqual(0, api.TotalCalls);
        await second.StartAsync(id, continueRemaining: true).WaitAsync(Deadline);
        Assert.AreEqual(100L, task.Snapshot.ConfirmedUsage.TotalTokens); Assert.AreEqual(6, api.CompletionCalls);
        Assert.AreEqual(1, task.Snapshot.UnknownUsageRequests);
        var history = task.Session.Recovery.Document!.History.Single(); Assert.AreEqual(interrupted, history.Snapshot);
        Assert.AreEqual(10L, task.Snapshot.Parameters!.RequestTokenReservation); // 旧预留更新，目标/确认进度不变。
    }

    [TestMethod]
    public async Task IndependentPlansKeepIntervalsAndPauseAllClosesFutureSchedules()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi(); var time = new ManualRunTimeProvider();
        await using var coordinator = Coordinator(folder, api, time); await coordinator.InitializeAsync();
        var a = await coordinator.AddAsync(Key1, Config("key-1") with { IntervalHours = 2.5m });
        var b = await coordinator.AddAsync(Key2, Config("key-2") with { IntervalHours = 5 });
        await Task.WhenAll(coordinator.StartAsync(a.Configuration.Id, schedule: true), coordinator.StartAsync(b.Configuration.Id, schedule: true));
        Assert.AreEqual(time.GetUtcNow().AddHours(2.5), a.Session.Schedule.NextRunUtc);
        Assert.AreEqual(time.GetUtcNow().AddHours(5), b.Session.Schedule.NextRunUtc);
        await UntilAsync(() => a.Snapshot.State == RunState.Completed && b.Snapshot.State == RunState.Completed);
        await coordinator.RefreshAsync(); Assert.IsTrue(a.Configuration.PlanRequested && b.Configuration.PlanRequested);
        await coordinator.PauseAllAsync(); Assert.AreEqual(ScheduleState.Disabled, a.Session.Schedule.State);
        Assert.AreEqual(ScheduleState.Disabled, b.Session.Schedule.State);
    }

    [TestMethod]
    public async Task QueuedTaskCanPauseAndStopWithoutSentRequestOrUnknownReservation()
    {
        var limiter = new RequestConcurrencyLimiter(1); using var held = limiter.TryAcquire();
        var api = new SimpleExecutor(); var engine = new SingleRunEngine(api, limiter: limiter);
        var run = engine.RunAsync(new("mock", 100) { RequestTokenReservation = 10 });
        Assert.IsTrue(engine.Pause()); await engine.StopAsync().WaitAsync(Deadline);
        var result = await run.WaitAsync(Deadline); Assert.AreEqual(0, api.Calls);
        Assert.AreEqual(0, result.InFlightRequests); Assert.AreEqual(0, result.UnknownUsageRequests);
    }

    [TestMethod]
    public void CustomTargetsKeepPercentageRangeAndUseCautiousRiskText()
    {
        Assert.AreEqual(120_000_000L, new TaskConfiguration().TargetTokens);
        var custom = new TaskConfiguration { CustomTargetTokens = 130_000_000 }; custom.Validate();
        StringAssert.Contains(TargetRisk.Describe(custom.TargetTokens), "通用积分");
        StringAssert.Contains(TargetRisk.Describe(60_000_000), "没有用满");
    }

    [TestMethod]
    public async Task ReopenKeepsSavedPlanWhileCorruptOtherKeyCannotCloseGoodPlan()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi();
        await using (var first = Coordinator(folder, api))
        {
            await first.InitializeAsync(); var a = await first.AddAsync(Key1, Config("key-1"));
            var b = await first.AddAsync(Key2, Config("key-2"));
            await first.SaveAsync(b.Configuration with { PlanRequested = true, NextRunUtc = DateTimeOffset.UtcNow.AddHours(5) });
            await File.WriteAllBytesAsync(Path.Combine(first.Workspace.Storage.TaskDirectory(a.Configuration), "mock-credential.dat"), [1, 2, 3]);
        }
        await using var second = Coordinator(folder, api); await second.InitializeAsync(); await second.RefreshAsync();
        var good = second.Tasks[1]; Assert.IsTrue(good.Configuration.PlanRequested); Assert.IsNotNull(good.Configuration.NextRunUtc);
        Assert.AreEqual(0, api.TotalCalls);
        await second.StartAsync(good.Configuration.Id, schedule: true);
        Assert.AreEqual(ScheduleState.Enabled, good.Session.Schedule.State);
        await second.DisablePlanAsync(good.Configuration.Id);
    }

    [TestMethod]
    public async Task RunAllWaitsForAlreadyRunningTaskRatherThanPrematureCompletion()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { Delay = TimeSpan.FromMilliseconds(20) };
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var a = await coordinator.AddAsync(Key1, Config("key-1", 1_000_000));
        var b = await coordinator.AddAsync(Key2, Config("key-2", 10));
        var individual = coordinator.StartAsync(a.Configuration.Id);
        await UntilAsync(() => a.Snapshot.InFlightRequests > 0);
        var all = coordinator.RunAllAsync(); await UntilAsync(() => b.Snapshot.State == RunState.Completed);
        Assert.IsFalse(all.IsCompleted); Assert.IsTrue(coordinator.BatchActive);
        await coordinator.StopAsync(a.Configuration.Id); await Task.WhenAll(individual, all).WaitAsync(Deadline);
        StringAssert.Contains(coordinator.BatchSummary, "停止1");
    }

    [TestMethod]
    public async Task PlanOnlyWritesCannotRevertLoweredTargetIntervalOrDisableChoice()
    {
        using var folder = new TestFolder(); var workspace = new TaskWorkspace(new(folder.Path, StorageProfile.Mock));
        await workspace.InitializeAsync(); var row = await workspace.AddAsync(Key1, Config("key-1", 120_000_000));
        var lowered = row with { Percentage = 25, CustomTargetTokens = 30_000_000, IntervalHours = 2.5m, Enabled = false };
        await workspace.SaveTaskAsync(lowered);
        await workspace.UpdatePlanStateAsync(row.Id, false, null);
        var saved = (await new JsonTaskCatalogStore(folder.Path, StorageProfile.Mock).LoadAsync()).Tasks[0];
        Assert.AreEqual(30_000_000L, saved.TargetTokens); Assert.AreEqual(2.5m, saved.IntervalHours); Assert.IsFalse(saved.Enabled);
    }

    [TestMethod]
    public async Task PanelUsesMaskedKeysRemarksAndKeepsCrossKeyStartButtonAndTrayNotice()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var folder = new TestFolder(); var api = new FakeMultiApi { Delay = TimeSpan.FromMilliseconds(50) };
            var coordinator = Coordinator(folder, api);
            using var form = new SenseNova.TokenBurner.Desktop.MultiTaskForm(coordinator);
            Exception? error = null;
            form.FormClosed += (_, _) => { if (error is null) finished.TrySetResult(); else finished.TrySetException(error); };
            async Task VerifyAsync()
            {
                    await coordinator.InitializeAsync();
                    T Field<T>(string name) => (T)typeof(SenseNova.TokenBurner.Desktop.MultiTaskForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
                    Assert.IsTrue(Field<TextBox>("_key").UseSystemPasswordChar);
                    var a = await coordinator.AddAsync(Key1, Config("key-1", 100));
                    var b = await coordinator.AddAsync(Key2, Config("key-2", 100));
                    await Task.Delay(1100); // 等一次正常面板刷新，非长期等待。
                    var grid = Field<DataGridView>("_grid");
                    Assert.AreEqual(2, grid.Rows.Count); StringAssert.Contains((string)grid.Rows[0].Cells[0].Value!, "key-1");
                    static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
                    var start = Descendants(form).OfType<Button>().Single(button => button.Text == "运行所选");
                    grid.Rows[0].Selected = true; start.PerformClick();
                    await UntilAsync(() => a.Active); Assert.IsTrue(start.Enabled);
                    grid.Rows[1].Selected = true; start.PerformClick();
                    await UntilAsync(() => api.CrossKeyOverlap);
                    form.Hide();
                    await UntilAsync(() => form.TrayNotificationCount > 0);
                    Assert.IsTrue(form.NoticeText.Contains("key-", StringComparison.Ordinal));
                    Assert.IsFalse(Field<CheckBox>("_review").Checked);
                    await coordinator.StopAllAsync();
            }
            form.Shown += async (_, _) =>
            {
                try { await VerifyAsync(); }
                catch (Exception exception) { error = exception; }
                finally { form.RequestExit(); }
            };
            Application.Run(form);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > Deadline) Assert.Fail("新MVP行为超时。"); await Task.Delay(5); }
    }
    private sealed class SimpleExecutor : IRunRequestExecutor
    {
        public int Calls;
        public Task<TokenUsage> ExecuteAsync(RunParameters parameters, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new TokenUsage(9, 1, 10)); }
    }
    private sealed class FakeMultiApi : ISenseNovaClient, ISenseNovaCompletionClient
    {
        private readonly object _gate = new(); private readonly HashSet<string> _activeKeys = [];
        private int _active; private int _maximum; private int _total; private int _completion;
        public TimeSpan Delay = TimeSpan.FromMilliseconds(15);
        public string? FailKey; public bool CrossKeyOverlap;
        public int MaximumActive => _maximum; public int TotalCalls => _total; public int CompletionCalls => _completion;
        public Task<TokenUsage> ProbeAsync(string credential, ModelInfo model, CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string credential, CancellationToken token = default)
        {
            Interlocked.Increment(ref _total);
            return Task.FromResult<IReadOnlyList<ModelInfo>>([new(SenseNovaDefaults.RebateModel, 262144, 65536, true)]);
        }
        public async Task<TokenUsage> CompleteAsync(string credential, ModelInfo model, CompletionRequest request, CancellationToken token = default)
        {
            Interlocked.Increment(ref _total); Interlocked.Increment(ref _completion);
            lock (_gate) { _active++; _maximum = Math.Max(_maximum, _active); _activeKeys.Add(credential); if (_activeKeys.Count > 1) CrossKeyOverlap = true; }
            try
            {
                await Task.Delay(Delay, token);
                if (credential == FailKey) throw new SenseNovaApiException(ApiFailureKind.Authentication, "virtual failure", RequestDelivery.Rejected);
                return new(9, 1, 10);
            }
            finally { lock (_gate) { _active--; _activeKeys.Remove(credential); } }
        }
    }
}
