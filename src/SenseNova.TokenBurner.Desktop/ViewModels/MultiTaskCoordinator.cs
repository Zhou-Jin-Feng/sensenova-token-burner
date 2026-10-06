using System.Security.Cryptography;
using System.Text;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Desktop.ViewModels;

public sealed class TaskRuntime
{
    internal TaskRuntime(TaskConfiguration configuration, PersistentRunSession session, ExecutorSlot executor)
    { Configuration = configuration; Session = session; Executor = executor; }
    public TaskConfiguration Configuration { get; internal set; }
    public PersistentRunSession Session { get; }
    internal ExecutorSlot Executor { get; }
    internal Task<RunSnapshot>? Operation { get; set; }
    internal bool Preparing { get; set; }
    internal CancellationTokenSource? Preparation { get; set; }
    internal TaskCompletionSource StartedOperation { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string? KeyIdentity { get; set; }
    internal Guid? NotifiedRecord { get; set; }
    internal DateTimeOffset? SavedNextUtc { get; set; }
    public string Message { get; internal set; } = "";
    public bool Active => Preparing || Operation is { IsCompleted: false } || Session.CurrentRun.State is
        RunState.Running or RunState.Paused or RunState.Stopping or RunState.AwaitingReview;
    public RunSnapshot Snapshot => Session.CurrentRun.Parameters is not null ? Session.CurrentRun
        : Session.Recovery.Document?.CurrentRun?.Snapshot ?? RunSnapshot.Idle;
    public bool HasRemainder => Snapshot.Parameters is { } parameters && Snapshot.State != RunState.Completed
        && Snapshot.ConfirmedUsage.TotalTokens < parameters.TargetTokens;
    public string Status => Preparing ? "准备中" : Session.Recovery.State switch
    {
        RecoveryState.Corrupt or RecoveryState.StorageFailed => "记录异常，禁止运行",
        RecoveryState.NeedsReview => "待核查",
        RecoveryState.AwaitingConfirmation when HasRemainder => "未完成，需继续剩余目标",
        _ => Session.CurrentRun.State switch
        {
            RunState.Running => "运行中", RunState.Paused => "已暂停", RunState.Stopping => "正在停止",
            RunState.AwaitingReview => "待核查", RunState.Completed => "已完成",
            RunState.Stopped => "已停止", RunState.Faulted => "异常停止",
            _ => Session.Schedule.State == ScheduleState.Enabled ? "等待计划" : "尚未运行"
        }
    };
}

internal sealed class ExecutorSlot : IRunRequestExecutor
{
    public IRunRequestExecutor? Inner { get; set; }
    public void ValidateParameters(RunParameters parameters) => (Inner ?? throw new InvalidOperationException("请求尚未准备。")).ValidateParameters(parameters);
    public Task<TokenUsage> ExecuteAsync(RunParameters parameters, CancellationToken token)
        => (Inner ?? throw new InvalidOperationException("请求尚未准备。")).ExecuteAsync(parameters, token);
}

/// <summary>一个桌面进程内管理多个持久session，共用API客户端、输入及请求许可。</summary>
public sealed class MultiTaskCoordinator : IAsyncDisposable
{
    private readonly TaskWorkspace _workspace;
    private readonly ISenseNovaClient _client;
    private readonly ISenseNovaCompletionClient _completion;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<CancellationToken, Task<CompletionRequest>> _createInput;
    private Task<CompletionRequest>? _input;
    private readonly object _keysGate = new();
    private readonly Dictionary<string, Guid> _keys = new(StringComparer.Ordinal);
    private readonly List<TaskRuntime> _tasks = [];
    private Guid[] _batch = [];
    private bool _batchActive;
    private bool _disposed;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _initialized;
    public MultiTaskCoordinator(TaskWorkspace workspace, ISenseNovaClient client, ISenseNovaCompletionClient completion,
        bool mockMode = false, Func<CancellationToken, Task<CompletionRequest>>? createInput = null, TimeProvider? timeProvider = null)
    {
        _workspace = workspace; _client = client; _completion = completion; IsMockMode = mockMode;
        _time = timeProvider ?? TimeProvider.System;
        _createInput = createInput ?? (token => Task.Run(() => CompletionInput.Create(token), token));
    }
    public bool IsMockMode { get; }
    public IReadOnlyList<TaskRuntime> Tasks => _tasks;
    public RequestConcurrencyLimiter Limiter { get; private set; } = new();
    public string LastNotice { get; private set; } = "保存配置不会自动运行。";
    public string BatchSummary { get; private set; } = "尚未运行全部。";
    public event Action<string>? CompletionNotice;
    public TaskWorkspace Workspace => _workspace;
    public bool BatchActive => _batchActive;
    public double OverallProgress
    {
        get
        {
            var rows = _tasks.Where(row => _batch.Contains(row.Configuration.Id)).ToArray();
            var target = rows.Sum(row => (double)(row.Snapshot.Parameters?.TargetTokens ?? row.Configuration.TargetTokens));
            return target <= 0 ? 0 : rows.Sum(row => (double)Math.Min(row.Snapshot.ConfirmedUsage.TotalTokens,
                row.Snapshot.Parameters?.TargetTokens ?? row.Configuration.TargetTokens)) / target;
        }
    }

    public async Task InitializeAsync()
    {
        await _initialization.WaitAsync(_lifetime.Token);
        try
        {
        if (_initialized) return;
        await _workspace.InitializeAsync(_lifetime.Token);
        Limiter.SetMaximum(_workspace.Catalog.MaximumConcurrency);
        foreach (var task in _workspace.Catalog.Tasks) await AddRuntimeAsync(task);
        var pending = _tasks.Count(row => row.HasRemainder || row.Session.Recovery.State == RecoveryState.NeedsReview);
        if (pending > 0) LastNotice = $"发现{pending}个未完成/待核查任务；请核查后选择“继续剩余目标”。重开未发送请求。";
        _initialized = true;
        }
        finally { _initialization.Release(); }
    }
    private async Task<TaskRuntime> AddRuntimeAsync(TaskConfiguration task)
    {
        var slot = new ExecutorSlot();
        var session = new PersistentRunSession(slot, _workspace.Storage.RunState(task), timeProvider: _time, limiter: Limiter);
        var runtime = new TaskRuntime(task, session, slot) { SavedNextUtc = null };
        await session.InitializeAsync(_lifetime.Token);
        _tasks.Add(runtime);
        return runtime;
    }
    public async Task<TaskRuntime> AddAsync(string credential, TaskConfiguration? configuration = null)
        => await AddRuntimeAsync(await _workspace.AddAsync(credential, configuration, _lifetime.Token));

    public async Task SaveAsync(TaskConfiguration configuration)
    {
        var row = Find(configuration.Id);
        await _workspace.SaveTaskAsync(configuration, _lifetime.Token);
        row.Configuration = _workspace.Catalog.Tasks.Single(task => task.Id == configuration.Id);
        if (row.Session.Schedule.State == ScheduleState.Enabled)
        {
            if (!configuration.Enabled) await row.Session.DisableScheduleAsync();
            else await row.Session.UpdateScheduleAsync(new(Parameters(row, configuration.TargetTokens), configuration.IntervalHours));
        }
    }
    public async Task SetConcurrencyAsync(int maximum)
    {
        if (_tasks.Any(row => row.Active || row.Session.Schedule.State == ScheduleState.Enabled))
            throw new InvalidOperationException("请先停止全部任务及计划再修改并发上限。");
        await _workspace.SetConcurrencyAsync(maximum, _lifetime.Token);
        Limiter.SetMaximum(maximum);
    }
    public string RunAllPreview()
    {
        var groups = _tasks.Where(row => row.Configuration.Enabled).GroupBy(row => string.IsNullOrWhiteSpace(row.Configuration.QuotaGroup)
            ? "未分组（不表示独立额度）" : row.Configuration.QuotaGroup);
        var lines = groups.Select(group => $"{group.Key}：{group.Count()}个key，目标合计{group.Sum(row => row.Configuration.TargetTokens):N0} tokens");
        return string.Join(Environment.NewLine, lines) + "\n\n多个key不代表多份独立积分池；同账户应归同组。请核查该账户滚动5小时/周额度与外部用量。组目标超过1.2亿可能扣通用积分；较低也不能保证绝对安全或精确返赠。5小时仅是运行间隔。";
    }
    public async Task RunAllAsync()
    {
        if (_batchActive) throw new InvalidOperationException("运行全部尚未结束。");
        var rows = _tasks.Where(row => row.Configuration.Enabled).ToArray();
        _batch = rows.Select(row => row.Configuration.Id).ToArray();
        _batchActive = true;
        BatchSummary = $"运行全部：{rows.Length}个任务。";
        try { await Task.WhenAll(rows.Select(StartOrAwaitAsync)); }
        finally
        {
            _batchActive = false;
            var completed = rows.Count(row => row.Session.CurrentRun.State == RunState.Completed && string.IsNullOrEmpty(row.Message));
            var stopped = rows.Count(row => row.Session.CurrentRun.State == RunState.Stopped && string.IsNullOrEmpty(row.Message));
            BatchSummary = $"全部收尾：完成{completed}，停止{stopped}，异常/待核查/未开始{rows.Length - completed - stopped}。";
            Notify(BatchSummary);
        }
    }
    private async Task StartOrAwaitAsync(TaskRuntime row)
    {
        var existing = row.Preparing || row.Active || row.Session.Schedule.State == ScheduleState.Enabled;
        if (row.Preparing) await row.StartedOperation.Task;
        if (existing)
            await row.Session.WaitForCompletionAsync();
        else await StartAsync(row.Configuration.Id);
    }
    public async Task StartAsync(Guid id, bool continueRemaining = false, bool reviewed = false, bool schedule = false)
    {
        var row = Find(id);
        if (_disposed || row.Preparing || row.Active || row.Session.Schedule.State == ScheduleState.Enabled)
        { row.Message = "任务已运行或计划已启用，未重叠启动。"; return; }
        row.Preparing = true;
        row.StartedOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        row.Message = "";
        row.Preparation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            if (!row.Configuration.Enabled) throw new InvalidOperationException("任务已禁用。");
            if (!continueRemaining && row.HasRemainder)
                throw new InvalidOperationException("有未完成目标，请先使用“继续剩余目标”，避免重置整轮。");
            if (row.Session.Recovery.State is RecoveryState.AwaitingConfirmation or RecoveryState.NeedsReview)
                await row.Session.ConfirmRecoveryAsync(reviewed, row.Preparation.Token);
            if (row.Session.Recovery.State != RecoveryState.Ready) throw new InvalidOperationException("运行记录尚未就绪。");
            if (!row.Configuration.CredentialSaved) throw new InvalidOperationException("旧任务未保存凭据，请重新配置key。");
            var credential = await _workspace.Storage.Credentials(row.Configuration).LoadAsync(row.Preparation.Token)
                ?? throw new InvalidOperationException("请先添加并保存API key。");
            ClaimKey(row, credential);
            IReadOnlyList<ModelInfo> models;
            using (await Limiter.AcquireAsync(row.Preparation.Token))
                models = await _client.GetModelsAsync(credential, row.Preparation.Token);
            var modelId = continueRemaining ? row.Snapshot.Parameters!.ModelId : row.Configuration.ModelId;
            var model = models.FirstOrDefault(model => model.Id == modelId && model.IsRebateEligible)
                ?? throw new InvalidOperationException("活动模型不可用，请核查模型权限。");
            Task<CompletionRequest> inputTask;
            lock (_keysGate) inputTask = _input ??= _createInput(_lifetime.Token);
            var input = await inputTask.WaitAsync(row.Preparation.Token);
            row.Preparation.Token.ThrowIfCancellationRequested();
            row.Executor.Inner = new CompletionRunExecutor(_completion, credential, model, input);
            var target = continueRemaining ? row.Snapshot.Parameters!.TargetTokens : row.Configuration.TargetTokens;
            var parameters = Parameters(row, target, modelId);
            if (schedule)
            {
                if (continueRemaining) throw new InvalidOperationException("请先继续剩余目标，再手动启用后续计划。");
                await row.Session.EnableScheduleAsync(new(parameters, row.Configuration.IntervalHours), row.Preparation.Token);
                row.Preparing = false;
                await PersistPlanAsync(row);
                return;
            }
            row.Operation = continueRemaining
                ? row.Session.ContinueRemainingAsync(row.Preparation.Token, parameters)
                : row.Session.RunOnceAsync(parameters, row.Preparation.Token);
            row.Preparing = false;
            row.StartedOperation.TrySetResult();
            await row.Operation;
            NotifyFinished(row);
        }
        catch (OperationCanceledException) { row.Message = "准备已取消，未新增请求。"; }
        catch (SenseNovaApiException exception) { row.Message = $"连接/请求失败：{exception.Kind}；请核查该任务。"; }
        catch (LocalStorageException) { row.Message = "配置/记录不可读写，已阻止该任务继续。"; }
        catch (InvalidOperationException exception) { row.Message = exception.Message; }
        catch (ArgumentException) { row.Message = "任务配置或请求参数无效。"; }
        finally
        {
            row.Preparing = false;
            row.StartedOperation.TrySetResult();
            if (row.Session.Schedule.State != ScheduleState.Enabled && !row.Active) ReleaseKey(row);
            if (row.Session.Schedule.State != ScheduleState.Enabled)
            { row.Preparation.Dispose(); row.Preparation = null; }
        }
    }
    private RunParameters Parameters(TaskRuntime row, long target, string? modelId = null)
        => new(modelId ?? row.Configuration.ModelId, target)
        {
            MaximumConcurrency = Math.Clamp((int)Math.Ceiling((double)Limiter.Maximum /
                Math.Max(1, _tasks.Count(task => task.Configuration.Enabled))), 1, RequestBaseline.Concurrency),
            RequestTokenReservation = _input?.IsCompletedSuccessfully == true ? _input.Result.EstimatedTotalTokens : 0,
            CustomTargetEnabled = target > ConsumptionPolicy.GetTargetTokens(95)
        };
    public async Task PauseAsync(Guid id)
    {
        var row = Find(id);
        if (row.Preparing) row.Preparation?.Cancel();
        await row.Session.DisableScheduleAsync();
        row.Session.Pause();
        await PersistPlanAsync(row);
    }
    public bool Resume(Guid id) => Find(id).Session.Resume();
    public async Task StopAsync(Guid id)
    {
        var row = Find(id);
        if (row.Preparing) row.Preparation?.Cancel();
        try { await row.Session.DisableScheduleAsync(); }
        finally { await row.Session.StopAsync(); }
        await PersistPlanAsync(row);
        if (!row.Active) ReleaseKey(row);
    }
    public async Task DisablePlanAsync(Guid id)
    {
        var row = Find(id);
        await row.Session.DisableScheduleAsync();
        await PersistPlanAsync(row);
        if (!row.Active) ReleaseKey(row);
    }
    public Task PauseAllAsync() => Task.WhenAll(_tasks.Select(row => PauseAsync(row.Configuration.Id)));
    public int ResumeAll() => _tasks.Count(row => row.Session.Resume());
    public Task StopAllAsync() => Task.WhenAll(_tasks.Select(row => StopAsync(row.Configuration.Id)));

    public async Task RefreshAsync()
    {
        foreach (var row in _tasks.ToArray())
        {
            if (row.Session.CurrentRun.State == RunState.AwaitingReview)
                await StopAsync(row.Configuration.Id);
            NotifyFinished(row);
            if (row.Session.Schedule.State != ScheduleState.Enabled && !row.Active) ReleaseKey(row);
            // 已保存但本进程未启用的计划不能被重开后的第一次刷新清空。
            if (row.Session.Schedule.NextRunUtc != row.SavedNextUtc) await PersistPlanAsync(row);
        }
    }
    private async Task PersistPlanAsync(TaskRuntime row)
    {
        var next = row.Session.Schedule.NextRunUtc;
        try
        {
            await _workspace.UpdatePlanStateAsync(row.Configuration.Id, row.Session.Schedule.State == ScheduleState.Enabled, next, _lifetime.Token);
            row.Configuration = _workspace.Catalog.Tasks.Single(task => task.Id == row.Configuration.Id);
            row.SavedNextUtc = next;
        }
        catch (LocalStorageException)
        {
            await row.Session.DisableScheduleAsync();
            row.Message = "计划配置保存失败，未来计划已关闭。";
        }
    }
    private void NotifyFinished(TaskRuntime row)
    {
        var record = row.Session.Recovery.Document?.CurrentRun;
        if (record is null || row.Active || record.Snapshot.State is not (RunState.Completed or RunState.Stopped or RunState.Faulted)
            || row.Session.CurrentRun.State is not (RunState.Completed or RunState.Stopped or RunState.Faulted)
            || row.NotifiedRecord == record.Id) return;
        row.NotifiedRecord = record.Id;
        Notify($"{row.Configuration.DisplayName}：{row.Status}，确认{row.Snapshot.ConfirmedUsage.TotalTokens:N0} tokens"
            + (row.Snapshot.ReviewRequired ? "，有未知用量需核查。" : "。"));
    }
    private void Notify(string message) { LastNotice = message; CompletionNotice?.Invoke(message); }
    private TaskRuntime Find(Guid id) => _tasks.Single(row => row.Configuration.Id == id);
    private void ClaimKey(TaskRuntime row, string credential)
    {
        var bytes = Encoding.UTF8.GetBytes(credential);
        string identity;
        try { identity = Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        lock (_keysGate)
        {
            if (_keys.TryGetValue(identity, out var owner) && owner != row.Configuration.Id)
                throw new InvalidOperationException("相同API key已有运行，已阻止重叠。");
            _keys[identity] = row.Configuration.Id; row.KeyIdentity = identity;
        }
    }
    private void ReleaseKey(TaskRuntime row)
    {
        lock (_keysGate)
        {
            if (row.KeyIdentity is { } identity && _keys.GetValueOrDefault(identity) == row.Configuration.Id) _keys.Remove(identity);
            row.KeyIdentity = null;
        }
        row.Executor.Inner = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var row in _tasks.Where(row => row.Preparing)) row.Preparation?.Cancel();
        await Task.WhenAll(_tasks.Select(async row =>
        {
            try { await row.Session.ShutdownAsync(); }
            catch (LocalStorageException) { row.Message = "退出时存储失败，请重开核查。"; }
        }));
        await _lifetime.CancelAsync();
        _lifetime.Dispose();
    }
}
