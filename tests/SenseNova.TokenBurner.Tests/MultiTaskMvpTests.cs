using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop;
using SenseNova.TokenBurner.Desktop.Ui;
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
    public async Task ConnectionValidationUsesGetOnlyAndDoesNotCreateGenerationInput()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi(); var inputCreations = 0;
        await using var coordinator = new MultiTaskCoordinator(new(new JsonTaskCatalogStore(folder.Path, StorageProfile.Mock)),
            api, api, true, _ => { inputCreations++; return Task.FromResult(new CompletionRequest("mock input", 2, 8)); });
        await coordinator.InitializeAsync();
        var row = await coordinator.AddAsync(Key1, Config("key-1"));
        var result = await coordinator.ValidateConnectionAsync(row.Configuration.Id);
        StringAssert.Contains(result, "仅调用GET /models");
        Assert.AreEqual(1, api.TotalCalls, "连接验证只应发出一次GET。");
        Assert.AreEqual(0, api.CompletionCalls, "连接验证不得调用completion。");
        Assert.AreEqual(0, inputCreations, "连接验证不得生成34万字符输入。");
        Assert.IsFalse(row.Active);
    }

    [TestMethod]
    public async Task ResetCancelsAndWaitsForConnectionValidationBeforeChangingTaskState()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { HoldModelList = true };
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var row = await coordinator.AddAsync(Key1, Config("key-1"));
        var validation = coordinator.ValidateConnectionAsync(row.Configuration.Id);
        await api.ModelListStarted.Task.WaitAsync(Deadline);
        await coordinator.ResetAsync(row.Configuration.Id).WaitAsync(Deadline);
        var validationResult = await validation.WaitAsync(Deadline);
        StringAssert.Contains(validationResult, "已取消");
        Assert.AreEqual(RunSnapshot.Idle, row.Snapshot);
        Assert.AreEqual(1, api.TotalCalls); Assert.AreEqual(0, api.CompletionCalls);
        Assert.IsFalse(row.Active);
    }

    [TestMethod]
    public async Task ConfirmedBusyTaskThatFinishesBeforeRunCallDoesNotStartAnotherRound()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { HoldFirstKey = true };
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var row = await coordinator.AddAsync(Key1, Config("key-1", 20));
        var original = coordinator.StartAsync(row.Configuration.Id);
        await UntilAsync(() => row.Snapshot.InFlightRequests > 0);
        var guard = coordinator.CaptureRunGuard(row.Configuration.Id);
        Assert.IsTrue(guard.WaitOnlyIfPreviouslyBusy);
        api.ReleaseFirstKey.TrySetResult();
        await original.WaitAsync(Deadline);
        var completions = api.CompletionCalls;

        await coordinator.RunOrContinueAsync(row.Configuration.Id,
            expectedLifecycleVersion: guard.LifecycleVersion,
            waitOnlyIfPreviouslyBusy: guard.WaitOnlyIfPreviouslyBusy);

        Assert.AreEqual(completions, api.CompletionCalls);
        StringAssert.Contains(row.Message, "原任务已收尾");
    }

    [TestMethod]
    [DataRow("pause")]
    [DataRow("stop")]
    [DataRow("reset")]
    public async Task CancellingValidationWhileRunWaitsNeverStartsGeneration(string control)
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { HoldModelList = true };
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var row = await coordinator.AddAsync(Key1, Config("key-1", 20));
        var validation = coordinator.ValidateConnectionAsync(row.Configuration.Id);
        await api.ModelListStarted.Task.WaitAsync(Deadline);
        var guard = coordinator.CaptureRunGuard(row.Configuration.Id);
        var run = coordinator.RunOrContinueAsync(row.Configuration.Id,
            expectedLifecycleVersion: guard.LifecycleVersion,
            waitOnlyIfPreviouslyBusy: guard.WaitOnlyIfPreviouslyBusy);

        switch (control)
        {
            case "pause": await coordinator.PauseAsync(row.Configuration.Id); break;
            case "stop": await coordinator.StopAsync(row.Configuration.Id); break;
            case "reset": await coordinator.ResetAsync(row.Configuration.Id); break;
            default: Assert.Fail($"未知控制：{control}"); break;
        }

        await Task.WhenAll(validation, run).WaitAsync(Deadline);
        Assert.AreEqual(0, api.CompletionCalls, $"{control}取消验证后不能发送生成请求。");
        Assert.AreEqual(1, api.TotalCalls, $"{control}取消验证后只能保留一次GET。");
    }

    [TestMethod]
    public async Task RunAllGuardPreventsNewRoundAfterOriginalTaskFinishes()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { HoldFirstKey = true };
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var row = await coordinator.AddAsync(Key1, Config("key-1", 20));
        var original = coordinator.StartAsync(row.Configuration.Id);
        await UntilAsync(() => row.Snapshot.InFlightRequests > 0);
        var guards = coordinator.CaptureRunGuards();
        api.ReleaseFirstKey.TrySetResult();
        await original.WaitAsync(Deadline);
        var completions = api.CompletionCalls;

        await coordinator.RunOrContinueAllAsync(guards).WaitAsync(Deadline);

        Assert.AreEqual(completions, api.CompletionCalls);
        StringAssert.Contains(row.Message, "原任务已收尾");
    }

    [TestMethod]
    public async Task SelectedBatchGuardCapturedBeforeConfirmPreventsNewRoundAfterTaskFinishes()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { HoldFirstKey = true };
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var row = await coordinator.AddAsync(Key1, Config("key-1", 20));
        var original = coordinator.StartAsync(row.Configuration.Id);
        await UntilAsync(() => row.Snapshot.InFlightRequests > 0);
        // 面板在显示批量确认之前捕获护栏；用户阅读确认框期间原任务跑完。
        var ids = new[] { row.Configuration.Id };
        var guards = coordinator.CaptureRunGuards(ids);
        _ = coordinator.PreviewSelected(ids, continueRemaining: true);
        api.ReleaseFirstKey.TrySetResult();
        await original.WaitAsync(Deadline);
        var completions = api.CompletionCalls;

        await coordinator.RunOrContinueSelectedAsync(ids, guards).WaitAsync(Deadline);

        Assert.AreEqual(completions, api.CompletionCalls, "确认期间已收尾的任务不能按配置目标重新开新一轮。");
        StringAssert.Contains(row.Message, "原任务已收尾");
    }

    [TestMethod]
    public async Task SelectedPreviewWarnsWhenGroupTargetExceedsReference()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi();
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var a = await coordinator.AddAsync(Key1, new TaskConfiguration { DisplayName = "key-1", QuotaGroup = "账户A" });
        var b = await coordinator.AddAsync(Key2, new TaskConfiguration { DisplayName = "key-2", QuotaGroup = "账户A" });
        var preview = coordinator.PreviewSelected([a.Configuration.Id, b.Configuration.Id], continueRemaining: true);
        StringAssert.Contains(preview.Risks[0], "超过同一账户1.2亿经验目标", "同组合计 2.4 亿时，第一条风险必须是超额警告。");
        StringAssert.Contains(preview.Text, "240,000,000 tokens");
        Assert.AreEqual(0, api.TotalCalls);
    }

    [TestMethod]
    public async Task RunAllValidationCancellationNeverStartsGeneration()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { HoldModelList = true };
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var row = await coordinator.AddAsync(Key1, Config("key-1", 20));
        var validation = coordinator.ValidateConnectionAsync(row.Configuration.Id);
        await api.ModelListStarted.Task.WaitAsync(Deadline);
        var all = coordinator.RunAllAsync();
        await coordinator.StopAsync(row.Configuration.Id);

        await Task.WhenAll(validation, all).WaitAsync(Deadline);
        Assert.AreEqual(0, api.CompletionCalls);
        Assert.AreEqual(1, api.TotalCalls);
    }

    [TestMethod]
    [DataRow("disable-plan")]
    [DataRow("pause")]
    [DataRow("stop")]
    [DataRow("reset")]
    public async Task ControlDuringInputPreparationCancelsOldScheduleStartAndNeverDispatches(string control)
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi();
        var inputStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInput = new TaskCompletionSource<CompletionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new MultiTaskCoordinator(
            new(new JsonTaskCatalogStore(folder.Path, StorageProfile.Mock)), api, api, true,
            async token =>
            {
                inputStarted.TrySetResult();
                return await releaseInput.Task.WaitAsync(token);
            });
        await coordinator.InitializeAsync();
        var row = await coordinator.AddAsync(Key1, Config("key-1", 20));

        var enabling = coordinator.StartAsync(row.Configuration.Id, schedule: true);
        await inputStarted.Task.WaitAsync(Deadline);
        switch (control)
        {
            case "disable-plan": await coordinator.DisablePlanAsync(row.Configuration.Id); break;
            case "pause": await coordinator.PauseAsync(row.Configuration.Id); break;
            case "stop": await coordinator.StopAsync(row.Configuration.Id); break;
            case "reset": await coordinator.ResetAsync(row.Configuration.Id); break;
            default: Assert.Fail($"未知控制：{control}"); break;
        }
        releaseInput.TrySetResult(new CompletionRequest("mock input", 2, 8));
        await enabling.WaitAsync(Deadline);

        Assert.AreEqual(1, api.TotalCalls, $"{control}取消旧启用只允许保留准备阶段的GET。");
        Assert.AreEqual(0, api.CompletionCalls, $"{control}期间旧启用不得发送首轮生成请求。");
        Assert.AreEqual(ScheduleState.Disabled, row.Session.Schedule.State);
        Assert.IsFalse(row.Configuration.PlanRequested);
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
        StringAssert.Contains(coordinator.RunAllPreview(), "340,000字符");
        StringAssert.Contains(coordinator.RunPreview(a.Configuration.Id), "241,024 tokens/笔");
        await coordinator.RunAllAsync().WaitAsync(Deadline);
        Assert.IsTrue(api.MaximumActive <= 10); Assert.IsTrue(api.CrossKeyOverlap);
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
        Assert.IsTrue(api.MaximumActive <= 10);
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
        await second.RunOrContinueAsync(id); Assert.AreEqual(0, api.TotalCalls);
        Assert.AreEqual(RecoveryState.NeedsReview, task.Session.Recovery.State);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.ResetAsync(id));
        Assert.AreEqual(interrupted, task.Snapshot); Assert.AreEqual(0, api.TotalCalls);
        await task.Session.ConfirmRecoveryAsync(true); Assert.AreEqual(0, api.TotalCalls);
        await second.RunOrContinueAsync(id).WaitAsync(Deadline);
        Assert.AreEqual(100L, task.Snapshot.ConfirmedUsage.TotalTokens); Assert.AreEqual(6, api.CompletionCalls);
        Assert.AreEqual(1, task.Snapshot.UnknownUsageRequests);
        var history = task.Session.Recovery.Document!.History.Single(); Assert.AreEqual(interrupted, history.Snapshot);
        Assert.AreEqual(10L, task.Snapshot.Parameters!.RequestTokenReservation); // 旧预留更新，目标/确认进度不变。
        var completed = task.Snapshot; var calls = api.TotalCalls;
        await second.ResetAsync(id);
        Assert.AreEqual(RunSnapshot.Idle, task.Snapshot); Assert.AreEqual(calls, api.TotalCalls);
        Assert.AreEqual(completed, task.Session.Recovery.Document!.History.Last().Snapshot);
    }

    [TestMethod]
    public async Task RunOrContinueKeepsOriginalProgressAndResetArchivesWithoutAutomaticRequests()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi(); Guid id;
        await using (var first = Coordinator(folder, api))
        {
            await first.InitializeAsync(); var row = await first.AddAsync(Key1, Config("key-1", 100)); id = row.Configuration.Id;
            var run = first.RunOrContinueAsync(id);
            await UntilAsync(() => row.Snapshot.ConfirmedUsage.TotalTokens > 0);
            await first.StopAsync(id); await run.WaitAsync(Deadline);
            var stopped = row.Snapshot; Assert.IsTrue(stopped.ConfirmedUsage.TotalTokens < 100);
            await first.SaveAsync(row.Configuration with { CustomTargetTokens = 50 });
            await first.RunOrContinueAsync(id).WaitAsync(Deadline);
            Assert.AreEqual(100L, row.Snapshot.Parameters!.TargetTokens);
            Assert.AreEqual(100L, row.Snapshot.ConfirmedUsage.TotalTokens);
            var calls = api.TotalCalls;
            await first.ResetAsync(id); await first.ResetAsync(id);
            Assert.AreEqual(RunSnapshot.Idle, row.Snapshot); Assert.AreEqual(calls, api.TotalCalls);
            Assert.AreEqual(2, row.Session.Recovery.Document!.History.Length);
            Assert.AreEqual(stopped, row.Session.Recovery.Document.History[0].Snapshot);
        }
        var before = api.TotalCalls;
        await using var reopened = Coordinator(folder, api); await reopened.InitializeAsync();
        Assert.AreEqual(RunSnapshot.Idle, reopened.Tasks[0].Snapshot); Assert.AreEqual(before, api.TotalCalls);
        await reopened.RunOrContinueAsync(id).WaitAsync(Deadline);
        Assert.AreEqual(50L, reopened.Tasks[0].Snapshot.ConfirmedUsage.TotalTokens);
        Assert.AreEqual(50L, reopened.Tasks[0].Snapshot.Parameters!.TargetTokens);
    }

    [TestMethod]
    public async Task ReviewedInterruptedRunCanResetWithoutSendingOrLosingCheckpoint()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi();
        await using (var first = Coordinator(folder, api))
        {
            await first.InitializeAsync(); var row = await first.AddAsync(Key1, Config("key-1"));
            var snapshot = new RunSnapshot(RunState.Running, new(SenseNovaDefaults.RebateModel, 100) { RequestTokenReservation = 10 }, new(36, 4, 40), 4, 1, 0, null) { ReservedTokens = 10 };
            var record = new RunRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, snapshot);
            await first.Workspace.Storage.RunState(row.Configuration).SaveAsync(RunStateDocument.Empty with { CurrentRun = record });
        }
        await using var reopened = Coordinator(folder, api); await reopened.InitializeAsync(); var task = reopened.Tasks[0];
        var original = task.Snapshot;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ResetAsync(task.Configuration.Id));
        Assert.AreEqual(original, task.Snapshot);
        await reopened.ResetAsync(task.Configuration.Id, reviewed: true);
        Assert.AreEqual(RunSnapshot.Idle, task.Snapshot); Assert.AreEqual(0, api.TotalCalls);
        var history = task.Session.Recovery.Document!.History.Single();
        Assert.AreEqual(original, history.Snapshot); Assert.IsNotNull(history.ReviewedUtc);
    }

    [TestMethod]
    public async Task ResetAllStopsOtherKeyWhileFirstKeyIsStillSettling()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { HoldFirstKey = true };
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var a = await coordinator.AddAsync(Key1, Config("key-1", 1_000_000));
        var b = await coordinator.AddAsync(Key2, Config("key-2", 1_000_000));
        var batch = coordinator.RunOrContinueAllAsync();
        await UntilAsync(() => api.CrossKeyOverlap && b.Snapshot.ConfirmedUsage.TotalTokens > 0);
        var reset = coordinator.ResetAllAsync();
        try
        {
            await UntilAsync(() => b.Snapshot.State == RunState.Idle);
            Assert.IsFalse(reset.IsCompleted, "第一项在途未收尾，但第二项必须已停止并归零。");
            Assert.AreEqual(0, coordinator.ResumeAll());
        }
        finally { api.ReleaseFirstKey.TrySetResult(); }
        await Task.WhenAll(batch, reset).WaitAsync(Deadline);
        Assert.AreEqual(RunSnapshot.Idle, a.Snapshot); Assert.AreEqual(RunSnapshot.Idle, b.Snapshot);
        var before = api.CompletionCalls; await Task.Delay(30); Assert.AreEqual(before, api.CompletionCalls);
        Assert.IsTrue(api.MaximumActive <= 10); Assert.IsFalse(coordinator.BatchActive);
        StringAssert.Contains(coordinator.BatchSummary, "重置");
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

    [TestMethod, DoNotParallelize]
    public Task PanelUsesMaskedKeysRemarksAndKeepsCrossKeyStartButtonAndTrayNotice()
        => WithPanelAsync(async (form, coordinator, api) =>
        {
            using (var dialog = new AddKeyDialog(_ => Task.FromResult<string?>(null)))
                Assert.IsTrue(Find<UiInput>(dialog, "NewKey").Box.UseSystemPasswordChar, "添加对话框的 key 输入必须遮蔽。");
            api.Delay = TimeSpan.FromMilliseconds(50);
            var a = await coordinator.AddAsync(Key1, Config("key-1", 100));
            var b = await coordinator.AddAsync(Key2, Config("key-2", 100));
            await UntilAsync(() => form.Roster.List.Items.Count == 2);
            StringAssert.Contains(form.Roster.List.Items[0].Name, "key-1");
            await form.SelectTaskAsync(a.Configuration.Id);
            form.Detail.Run.PerformClick();
            await UntilAsync(() => a.Active);
            await form.SelectTaskAsync(b.Configuration.Id);
            await UntilAsync(() => form.Detail.Run.Enabled);
            form.Detail.Run.PerformClick();
            await UntilAsync(() => api.CrossKeyOverlap);
            form.Hide();
            await UntilAsync(() => form.TrayNotificationCount > 0);
            StringAssert.Contains(form.NoticeText, "key-");
            Assert.IsFalse(form.Detail.Overview.ReviewConfirm.Checked);
            await coordinator.StopAllAsync();
        });

    [TestMethod, DoNotParallelize]
    public Task PanelMinimizeKeepsTaskbarAndTitleCloseOnlyHides()
        => WithPanelAsync(async (form, _, _) =>
        {
            form.WindowState = FormWindowState.Minimized;
            await Task.Delay(50);
            Assert.IsTrue(form.Visible, "最小化应留在任务栏，不能Hide。");
            Assert.IsTrue(form.ShowInTaskbar);
            form.WindowState = FormWindowState.Normal;
            form.Close();
            Assert.IsFalse(form.Visible, "标题栏关闭应隐藏到托盘。");
            Assert.IsFalse(form.IsDisposed);
            form.Restore();
            Assert.IsTrue(form.Visible);
            Assert.AreEqual(FormWindowState.Normal, form.WindowState);
        });

    [TestMethod, DoNotParallelize]
    public Task PanelOperationNoticesFollowRunPauseContinueAndReset()
        => WithPanelAsync(async (form, coordinator, api) =>
        {
            api.Delay = TimeSpan.FromMilliseconds(60);
            Assert.IsFalse(IsOnScreen(form.Detail.Schedule.Enable), "定时按钮只在定时页显示，不与手动按钮混排。");
            StringAssert.Contains(form.Detail.Target.GroupInput.Box.PlaceholderText, "账户A");
            string? addError = "not called";
            form.Prompts.AddKey = async (_, submit) => addError = await submit(new AddKeyRequest(Key1, "key-1", ""));
            form.Roster.AddButton.PerformClick();
            await UntilAsync(() => coordinator.Tasks.Count == 1 && form.Roster.List.Items.Count == 1);
            Assert.IsNull(addError);
            Assert.IsTrue(form.Roster.BatchRun.Visible && form.Roster.BatchPause.Visible && form.Roster.BatchReset.Visible);
            form.Detail.ShowPage(2);
            Assert.IsTrue(IsOnScreen(form.Detail.Schedule.Enable));
            form.Detail.ShowPage(1);
            form.Detail.Target.Custom.Checked = true;
            form.Detail.Target.Tokens.Value = 1_000_000;
            Assert.IsTrue(form.Detail.Target.IsDirty);
            form.Detail.Target.Save.PerformClick();
            await UntilAsync(() => coordinator.Tasks[0].Configuration.TargetTokens == 1_000_000 && !form.Detail.Target.IsDirty);
            form.Detail.ShowPage(0);
            form.Detail.Run.PerformClick();
            await UntilAsync(() => coordinator.Tasks[0].Snapshot.ConfirmedUsage.TotalTokens > 0);
            await UntilAsync(() => form.NoticeText.Contains("运行中", StringComparison.Ordinal)); // 等正常1秒刷新，不能只检查引擎已启动。
            Assert.IsFalse(form.NoticeText.Contains("尚未运行", StringComparison.Ordinal));
            await UntilAsync(() => form.Detail.Pause.Enabled);
            form.Detail.Pause.PerformClick();
            await UntilAsync(() => coordinator.Tasks[0].Snapshot.State == RunState.Paused);
            await UntilAsync(() => coordinator.Tasks[0].Snapshot.InFlightRequests == 0);
            var paused = coordinator.Tasks[0].Snapshot.ConfirmedUsage.TotalTokens;
            await UntilAsync(() => form.NoticeText.Contains("暂停", StringComparison.Ordinal));
            StringAssert.Contains(form.NoticeText, "定时");
            await UntilAsync(() => form.Detail.Run.Enabled);
            form.Detail.Run.PerformClick();
            await UntilAsync(() => coordinator.Tasks[0].Snapshot.State == RunState.Running);
            await UntilAsync(() => coordinator.Tasks[0].Snapshot.ConfirmedUsage.TotalTokens > paused);
            form.Detail.Reset.PerformClick();
            await UntilAsync(() => coordinator.Tasks[0].Snapshot.State == RunState.Idle);
            await UntilAsync(() => form.NoticeText.Contains("已重置", StringComparison.Ordinal));
            var calls = api.TotalCalls; await Task.Delay(30);
            Assert.AreEqual(calls, api.TotalCalls); Assert.AreEqual(0L, coordinator.Tasks[0].Snapshot.ConfirmedUsage.TotalTokens);
            Assert.IsNull(coordinator.Tasks[0].Session.Recovery.Document!.CurrentRun);
            await UntilAsync(() => form.Detail.Run.Enabled);
            form.Detail.Run.PerformClick();
            await UntilAsync(() => coordinator.Tasks[0].Snapshot.State == RunState.Running);
            Assert.AreEqual(1_000_000L, coordinator.Tasks[0].Snapshot.Parameters!.TargetTokens);
            await coordinator.StopAllAsync();
        });

    [TestMethod, DoNotParallelize]
    public Task PanelProgressFollowsSelectedTaskWithoutRunAll()
        => WithPanelAsync(async (form, coordinator, api) =>
        {
            api.Delay = TimeSpan.FromMilliseconds(60);
            var first = await coordinator.AddAsync(Key1, Config("key-1", 1_000));
            var second = await coordinator.AddAsync(Key2, Config("key-2", 2_000));
            await UntilAsync(() => form.Roster.List.Items.Count == 2);
            await form.SelectTaskAsync(first.Configuration.Id);
            form.Detail.Run.PerformClick();
            await UntilAsync(() => form.Detail.Overview.Gauge.Confirmed > 0);
            Assert.IsFalse(coordinator.BatchActive);
            Assert.IsFalse(form.Detail.Overview.Review.Visible, "正常在途请求不能被标成上次未知请求。");
            await form.SelectTaskAsync(second.Configuration.Id);
            Assert.AreEqual(0d, form.Detail.Overview.Gauge.Confirmed, "换到未运行任务应显示0%，不能沿用上一项。");
            await coordinator.StopAllAsync();
            Assert.IsTrue(first.Snapshot.ConfirmedUsage.TotalTokens > 0);
        });

    [TestMethod, DoNotParallelize]
    public Task PanelHasGetOnlyValidationAndRiskConfirmationCancelsBeforeNetwork()
    {
        var previews = new List<string>();
        return WithPanelAsync(async (form, coordinator, api) =>
        {
            var row = await coordinator.AddAsync(Key1, Config("key-1", 120_000_000));
            await UntilAsync(() => form.Roster.List.Items.Count == 1 && form.Detail.Validate.Enabled);
            form.Detail.Validate.PerformClick();
            await UntilAsync(() => row.Message.Contains("仅调用GET /models", StringComparison.Ordinal));
            Assert.AreEqual(1, api.TotalCalls); Assert.AreEqual(0, api.CompletionCalls);
            Assert.AreEqual(NoticeLevel.Success, row.MessageLevel);

            var beforeCancelledRuns = api.TotalCalls;
            await UntilAsync(() => form.Detail.Run.Enabled);
            form.Detail.Run.PerformClick();
            await UntilAsync(() => form.NoticeText.Contains("已取消运行", StringComparison.Ordinal));
            await UntilAsync(() => form.CheckedTaskIds.Count == 1 && form.Roster.BatchRun.Enabled);
            form.Roster.BatchRun.PerformClick();
            await UntilAsync(() => form.NoticeText.Contains("已取消运行所选", StringComparison.Ordinal));
            form.Detail.ShowPage(2);
            await UntilAsync(() => form.Detail.Schedule.Enable.Enabled);
            form.Detail.Schedule.Enable.PerformClick();
            await UntilAsync(() => form.NoticeText.Contains("已取消启用定时", StringComparison.Ordinal));
            Assert.AreEqual(beforeCancelledRuns, api.TotalCalls, "取消运行确认后不应再发GET或POST。");
            Assert.AreEqual(0, api.CompletionCalls);
            Assert.AreEqual(3, previews.Count);
            foreach (var preview in previews)
            {
                StringAssert.Contains(preview, "340,000字符");
                StringAssert.Contains(preview, "241,024 tokens/笔");
                StringAssert.Contains(preview, "120,000,000 tokens");
                StringAssert.Contains(preview, "滚动");
                StringAssert.Contains(preview, "外部用量");
            }
            Assert.AreEqual(ScheduleState.Disabled, row.Session.Schedule.State);
        }, preview => { previews.Add(preview); return false; });
    }

    [TestMethod, DoNotParallelize]
    public Task PanelRosterRebuildsFromTasksAfterDeleteWithoutGhostRows()
        => WithPanelAsync(async (form, coordinator, _) =>
        {
            var a = await coordinator.AddAsync(Key1, Config("key-1"));
            var b = await coordinator.AddAsync(Key2, Config("key-2"));
            var c = await coordinator.AddAsync("mock-only-not-a-real-key-3", Config("key-3"));
            await UntilAsync(() => form.Roster.List.Items.Count == 3);
            await form.SelectTaskAsync(c.Configuration.Id);
            await coordinator.DeleteAsync(b.Configuration.Id);
            await UntilAsync(() => form.Roster.List.Items.Count == 2);
            CollectionAssert.AreEqual(new[] { a.Configuration.Id, c.Configuration.Id }, form.Roster.List.Items.Select(item => item.Id).ToArray(),
                "删除中间任务后列表必须按现有任务重建，不能残留旧行。");
            await coordinator.DeleteAsync(c.Configuration.Id);
            await UntilAsync(() => form.Roster.List.Items.Count == 1);
            Assert.AreEqual(a.Configuration.Id, form.SelectedTaskId, "删除所选任务后应切到仍存在的任务。");
            await coordinator.DeleteAsync(a.Configuration.Id);
            await UntilAsync(() => form.Roster.List.Items.Count == 0);
            Assert.IsNull(form.SelectedTaskId);
        });

    [TestMethod, DoNotParallelize]
    public Task DeleteFromPanelRequiresConfirmation()
        => WithPanelAsync(async (form, coordinator, _) =>
        {
            var a = await coordinator.AddAsync(Key1, Config("key-1"));
            form.Prompts.ConfirmDelete = (_, _) => false;
            await form.DeleteTaskAsync(a.Configuration.Id);
            Assert.AreEqual(1, coordinator.Tasks.Count, "未确认不得删除。");
            form.Prompts.ConfirmDelete = (_, _) => true;
            await form.DeleteTaskAsync(a.Configuration.Id);
            Assert.AreEqual(0, coordinator.Tasks.Count);
            StringAssert.Contains(form.NoticeText, "已删除");
        });

    [TestMethod, DoNotParallelize]
    public Task PanelKeepsUserUncheckedSelectionAcrossRefresh()
        => WithPanelAsync(async (form, coordinator, _) =>
        {
            await coordinator.AddAsync(Key1, Config("key-1"));
            await coordinator.AddAsync(Key2, Config("key-2"));
            await UntilAsync(() => form.CheckedTaskIds.Count == 2);
            foreach (var row in coordinator.Tasks) form.ToggleChecked(row.Configuration.Id);
            Assert.AreEqual(0, form.CheckedTaskIds.Count);
            await Task.Delay(1300); // 至少经过一次正常的每秒刷新
            Assert.AreEqual(0, form.CheckedTaskIds.Count, "用户取消的勾选不能被刷新自动恢复。");
            Assert.IsFalse(form.Roster.BatchRun.Enabled);
        });

    [TestMethod, DoNotParallelize]
    public Task EnablePlanSavesEditedIntervalBeforeConfirmationAndCancelSendsNothing()
    {
        var previews = new List<string>();
        return WithPanelAsync(async (form, coordinator, api) =>
        {
            var row = await coordinator.AddAsync(Key1, Config("key-1"));
            await UntilAsync(() => form.SelectedTaskId == row.Configuration.Id);
            form.Detail.ShowPage(2);
            form.Detail.Schedule.Interval.Value = 2.5m;
            await UntilAsync(() => form.Detail.Schedule.Enable.Enabled);
            form.Detail.Schedule.Enable.PerformClick();
            await UntilAsync(() => form.NoticeText.Contains("已取消启用定时", StringComparison.Ordinal));
            Assert.AreEqual(2.5m, row.Configuration.IntervalHours, "启用定时前应先保存界面上的间隔，确认框显示的就是实际间隔。");
            StringAssert.Contains(previews.Single(), "每2.5小时");
            Assert.AreEqual(0, api.TotalCalls);
            Assert.AreEqual(ScheduleState.Disabled, row.Session.Schedule.State);
        }, preview => { previews.Add(preview); return false; });
    }

    [TestMethod, DoNotParallelize]
    public Task UnsavedTargetEditsPromptBeforeRunAndCancelLeavesEverythingUntouched()
        => WithPanelAsync(async (form, coordinator, api) =>
        {
            var row = await coordinator.AddAsync(Key1, Config("key-1", 100));
            await UntilAsync(() => form.SelectedTaskId == row.Configuration.Id);
            form.Detail.ShowPage(1);
            form.Detail.Target.Tokens.Value = 40;
            Assert.IsTrue(form.Detail.Target.IsDirty);
            var prompts = 0;
            form.Prompts.ResolveDirty = (_, _) => { prompts++; return DirtyChoice.Cancel; };
            form.Detail.ShowPage(0);
            form.Detail.Run.PerformClick();
            await UntilAsync(() => prompts == 1);
            await Task.Delay(50);
            Assert.AreEqual(0, api.TotalCalls, "取消未保存提示后不得发请求。");
            Assert.AreEqual(100L, row.Configuration.TargetTokens);
            Assert.IsTrue(form.Detail.Target.IsDirty, "取消后修改应保留在页面上。");
            form.Prompts.ResolveDirty = (_, _) => { prompts++; return DirtyChoice.Save; };
            form.Detail.Run.PerformClick();
            await UntilAsync(() => row.Snapshot.State == RunState.Completed);
            Assert.AreEqual(40L, row.Snapshot.Parameters!.TargetTokens, "选择保存后应按新目标运行。");
            Assert.AreEqual(2, prompts);
        });

    [TestMethod, DoNotParallelize]
    public Task SwitchingTaskWithUnsavedEditsAsksAndCancelKeepsSelection()
        => WithPanelAsync(async (form, coordinator, _) =>
        {
            var a = await coordinator.AddAsync(Key1, Config("key-1"));
            var b = await coordinator.AddAsync(Key2, Config("key-2"));
            await form.SelectTaskAsync(a.Configuration.Id);
            form.Detail.Target.GroupInput.Text = "账户A";
            Assert.IsTrue(form.Detail.Target.IsDirty);
            form.Prompts.ResolveDirty = (_, _) => DirtyChoice.Cancel;
            await form.SelectTaskAsync(b.Configuration.Id);
            Assert.AreEqual(a.Configuration.Id, form.SelectedTaskId, "取消时应留在原任务。");
            form.Prompts.ResolveDirty = (_, _) => DirtyChoice.Discard;
            await form.SelectTaskAsync(b.Configuration.Id);
            Assert.AreEqual(b.Configuration.Id, form.SelectedTaskId);
            Assert.AreEqual("", a.Configuration.QuotaGroup, "放弃修改不得保存。");
        });

    [TestMethod, DoNotParallelize]
    public Task ReopenedInterruptedRunKeepsRunButtonUsableToContinueRemaining()
        => WithPanelAsync(async (form, coordinator, api) =>
        {
            var task = coordinator.Tasks.Single();
            await UntilAsync(() => form.SelectedTaskId == task.Configuration.Id);
            Assert.AreEqual(RunState.Running, task.Snapshot.State, "前提：异常退出留下的记录仍是运行中。");
            await UntilAsync(() => form.Detail.Run.Enabled); // 记录里的“运行中”不能让按钮永久禁用
            Assert.IsTrue(form.Detail.Overview.Review.Visible, "异常退出的轮次需要先核查。");
            form.Detail.Run.PerformClick();
            await UntilAsync(() => form.NoticeText.Contains("请求结果未知", StringComparison.Ordinal));
            Assert.AreEqual(0, api.TotalCalls, "未勾选核查确认时不得发请求。");
            form.Detail.Overview.ReviewConfirm.Checked = true;
            form.Detail.Run.PerformClick();
            await UntilAsync(() => task.Snapshot.State == RunState.Completed);
            Assert.AreEqual(100L, task.Snapshot.ConfirmedUsage.TotalTokens, "核查后应沿用已确认的 40 继续剩余目标。");
        }, prepare: async (folder, api) =>
        {
            await using var first = Coordinator(folder, api);
            await first.InitializeAsync();
            var row = await first.AddAsync(Key1, Config("key-1"));
            var snapshot = new RunSnapshot(RunState.Running, new(SenseNovaDefaults.RebateModel, 100) { RequestTokenReservation = 10 }, new(36, 4, 40), 4, 0, 0, null);
            var record = new RunRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, snapshot);
            await first.Workspace.Storage.RunState(row.Configuration).SaveAsync(RunStateDocument.Empty with { CurrentRun = record });
        });

    [TestMethod]
    public async Task PerKeyConcurrencyDefaultsForOlderCatalogAndCapsEachRun()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { Delay = TimeSpan.FromMilliseconds(30) };
        await using (var first = Coordinator(folder, api))
        {
            await first.InitializeAsync();
            await first.AddAsync(Key1, Config("key-1", 200));
        }
        // 模拟旧版写出的 tasks.json：没有 PerKeyConcurrency 字段。
        var path = Path.Combine(folder.Path, "tasks.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.IsTrue(json.Remove("PerKeyConcurrency"));
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await using var second = Coordinator(folder, api);
        await second.InitializeAsync();
        Assert.AreEqual(RequestBaseline.DefaultConcurrency, second.PerKeyConcurrency);
        await second.SetConcurrencyAsync(10, 2);
        var row = second.Tasks[0];
        await second.RunOrContinueAsync(row.Configuration.Id).WaitAsync(Deadline);
        Assert.AreEqual(2, row.Snapshot.Parameters!.MaximumConcurrency, "单 key 上限应限制每轮并发，不能直接等于共享上限。");
        Assert.IsTrue(api.MaximumActive <= 2);
        var reloaded = await new JsonTaskCatalogStore(folder.Path, StorageProfile.Mock).LoadAsync();
        Assert.AreEqual(2, reloaded.PerKeyConcurrency); Assert.AreEqual(10, reloaded.MaximumConcurrency);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => second.SetConcurrencyAsync(3, 11));
    }

    [TestMethod]
    public async Task SelectedBatchRunsOnlyCheckedIdsAndConcurrencyTenIsAccepted()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi { Delay = TimeSpan.FromMilliseconds(10) };
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var a = await coordinator.AddAsync(Key1, Config("key-1", 20));
        var b = await coordinator.AddAsync(Key2, Config("key-2", 20));
        await coordinator.SetConcurrencyAsync(10);
        Assert.AreEqual(10, coordinator.Limiter.Maximum);
        await coordinator.RunOrContinueSelectedAsync([a.Configuration.Id]).WaitAsync(Deadline);
        Assert.AreEqual(RunState.Completed, a.Snapshot.State);
        Assert.AreEqual(RunState.Idle, b.Snapshot.State);
    }

    [TestMethod]
    public async Task DeleteTaskRemovesOnlyStoppedTaskAndPreservesOtherTask()
    {
        using var folder = new TestFolder(); var api = new FakeMultiApi();
        await using var coordinator = Coordinator(folder, api); await coordinator.InitializeAsync();
        var a = await coordinator.AddAsync(Key1, Config("key-1"));
        var b = await coordinator.AddAsync(Key2, Config("key-2"));
        await coordinator.DeleteAsync(a.Configuration.Id);
        Assert.IsFalse(coordinator.Tasks.Any(row => row.Configuration.Id == a.Configuration.Id));
        Assert.IsTrue(coordinator.Tasks.Any(row => row.Configuration.Id == b.Configuration.Id));
    }

    /// <summary>控件及其所有父级都可见且挂在窗体上（WinForms 的 Visible 对未挂载控件也返回 true）。</summary>
    private static bool IsOnScreen(Control control)
    {
        for (Control? current = control; current is not null; current = current.Parent)
        {
            if (!current.Visible) return false;
            if (current is Form) return true;
        }
        return false;
    }
    private static T Find<T>(Control root, string name) where T : Control
    {
        static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
        return Descendants(root).OfType<T>().Single(control => control.Name == name);
    }
    /// <summary>面板测试：屏幕外、不抢焦点、不弹系统通知；确认框默认通过。</summary>
    private static async Task WithPanelAsync(Func<SenseNova.TokenBurner.Desktop.MultiTaskForm, MultiTaskCoordinator, FakeMultiApi, Task> verify,
        Func<string, bool>? confirmRun = null, Func<TestFolder, FakeMultiApi, Task>? prepare = null)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var folder = new TestFolder(); var api = new FakeMultiApi();
            // 尚未创建控件，线程上没有同步上下文，可以同步等待准备数据。
            prepare?.Invoke(folder, api).GetAwaiter().GetResult();
            var coordinator = Coordinator(folder, api);
            using var form = new SenseNova.TokenBurner.Desktop.MultiTaskForm(coordinator, confirmReset: _ => true, confirmRun: confirmRun ?? (_ => true))
            { ShowInBackground = true };
            form.Prompts.Balloon = (_, _, _) => { };
            Exception? error = null;
            form.FormClosed += (_, _) => { if (error is null) finished.TrySetResult(); else finished.TrySetException(error); };
            form.Shown += async (_, _) =>
            {
                try { await coordinator.InitializeAsync(); await verify(form, coordinator, api); }
                catch (Exception exception) { error = exception; }
                finally { form.RequestExit(); }
            };
            Application.Run(form);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
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
        public bool HoldFirstKey;
        public TaskCompletionSource ReleaseFirstKey { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldModelList;
        public TaskCompletionSource ModelListStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseModelList { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaximumActive => _maximum; public int TotalCalls => _total; public int CompletionCalls => _completion;
        public Task<TokenUsage> ProbeAsync(string credential, ModelInfo model, CancellationToken token = default) => throw new NotSupportedException();
        public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string credential, CancellationToken token = default)
        {
            Interlocked.Increment(ref _total);
            if (HoldModelList)
            {
                ModelListStarted.TrySetResult();
                await ReleaseModelList.Task.WaitAsync(token);
            }
            return [new(SenseNovaDefaults.RebateModel, 262144, 65536, true)];
        }
        public async Task<TokenUsage> CompleteAsync(string credential, ModelInfo model, CompletionRequest request, CancellationToken token = default)
        {
            Interlocked.Increment(ref _total); Interlocked.Increment(ref _completion);
            lock (_gate) { _active++; _maximum = Math.Max(_maximum, _active); _activeKeys.Add(credential); if (_activeKeys.Count > 1) CrossKeyOverlap = true; }
            try
            {
                if (HoldFirstKey && credential == Key1) await ReleaseFirstKey.Task.WaitAsync(Deadline);
                await Task.Delay(Delay, token);
                if (credential == FailKey) throw new SenseNovaApiException(ApiFailureKind.Authentication, "virtual failure", RequestDelivery.Rejected);
                return new(9, 1, 10);
            }
            finally { lock (_gate) { _active--; _activeKeys.Remove(credential); } }
        }
    }

    [TestMethod]
    public void SettingsDialogAndDialogFormCenterToScreen()
    {
        var model = new SettingsModel(ThemeMode.System, 3, 3, true, @"C:\data", "1.0.0", SenseNovaDefaults.BaseUrl, SenseNovaDefaults.RebateModel, true);
        using var settings = new SettingsDialog(model, _ => { }, (_, _) => Task.FromResult<string?>(null));
        Assert.AreEqual(FormStartPosition.CenterScreen, settings.StartPosition);
        var fitMethod = typeof(DialogForm).GetMethod("FitToContent", BindingFlags.Instance | BindingFlags.NonPublic);
        fitMethod!.Invoke(settings, null);
        var settingsScreen = Screen.FromControl(settings).WorkingArea;
        var expectedSettingsX = Math.Max(settingsScreen.X, settingsScreen.X + (settingsScreen.Width - settings.Width) / 2);
        var expectedSettingsY = Math.Max(settingsScreen.Y, settingsScreen.Y + (settingsScreen.Height - settings.Height) / 2);
        Assert.AreEqual(new Point(expectedSettingsX, expectedSettingsY), settings.Location);

        using var dialog = new TestCenterDialog();
        Assert.AreEqual(FormStartPosition.CenterScreen, dialog.StartPosition);
        dialog.TestFit();
        var screen = Screen.FromControl(dialog).WorkingArea;
        var expectedX = Math.Max(screen.X, screen.X + (screen.Width - dialog.Width) / 2);
        var expectedY = Math.Max(screen.Y, screen.Y + (screen.Height - dialog.Height) / 2);
        Assert.AreEqual(new Point(expectedX, expectedY), dialog.Location);

        dialog.ShowInBackground = true;
        dialog.TestFit();
        Assert.AreEqual(new Point(-20000, -20000), dialog.Location);
    }

    private sealed class TestCenterDialog : DialogForm
    {
        public TestCenterDialog() : base("测试对话框", 540) { }
        public void TestFit() => FitToContent();
    }
}
