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
    internal bool Validating { get; set; }
    internal TaskCompletionSource? ValidationOperation { get; set; }
    internal CancellationTokenSource? ValidationCancellation { get; set; }
    internal bool ValidationSucceeded { get; set; }
    internal int LifecycleVersion { get; set; }
    internal bool Resetting { get; set; }
    internal CancellationTokenSource? Preparation { get; set; }
    /// <summary>串行化准备完成与暂停/停止/关闭计划之间的最终提交边界。</summary>
    internal SemaphoreSlim StartGate { get; } = new(1, 1);
    internal TaskCompletionSource StartedOperation { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource? FinishedOperation { get; set; }
    internal string? KeyIdentity { get; set; }
    internal Guid? NotifiedRecord { get; set; }
    internal DateTimeOffset? SavedNextUtc { get; set; }
    internal Func<TaskRuntime, bool>? IsWeeklyQuotaPaused { get; set; }
    /// <summary>任务最近一条结果说明；赋值时级别默认按“需要注意”处理，成功/取消/错误处另行标注。</summary>
    public string Message { get; internal set { field = value; MessageLevel = NoticeLevel.Warning; } } = "";
    public NoticeLevel MessageLevel { get; internal set; } = NoticeLevel.Warning;
    public bool Active => Resetting || Validating || Preparing || Operation is { IsCompleted: false } || Session.CurrentRun.State is
        RunState.Running or RunState.Paused or RunState.Stopping or RunState.AwaitingReview;
    public RunSnapshot Snapshot => Session.CurrentRun.Parameters is not null ? Session.CurrentRun
        : Session.Recovery.Document?.CurrentRun?.Snapshot ?? RunSnapshot.Idle;
    public bool HasRemainder => Snapshot.Parameters is { } parameters && Snapshot.State != RunState.Completed
        && Snapshot.ConfirmedUsage.TotalTokens < parameters.TargetTokens;
    public string Status => Describe().Text;
    /// <summary>面板用的简短状态与类别；与 <see cref="Status"/> 同源，避免界面靠文字猜颜色。</summary>
    public TaskStatusKind StatusKind => Describe().Kind;
    public string ShortStatus => Describe().Short;
    private (string Text, string Short, TaskStatusKind Kind) Describe() => Resetting ? ("正在重置", "重置中", TaskStatusKind.Busy)
        : Validating ? ("验证连接中", "验证中", TaskStatusKind.Busy)
        : Preparing ? ("准备中", "准备中", TaskStatusKind.Burning)
        : Session.Recovery.State switch
        {
            RecoveryState.Corrupt or RecoveryState.StorageFailed => ("记录异常，禁止运行", "记录异常", TaskStatusKind.Danger),
            RecoveryState.NeedsReview => ("待核查", "待核查", TaskStatusKind.Warning),
            RecoveryState.AwaitingConfirmation when HasRemainder => ("未完成，需继续剩余目标", "可继续", TaskStatusKind.Info),
            _ => Session.CurrentRun.State switch
            {
                RunState.Running => ("运行中", "运行中", TaskStatusKind.Burning),
                RunState.Paused => ("已暂停", "已暂停", TaskStatusKind.Paused),
                RunState.Stopping => ("正在停止", "停止中", TaskStatusKind.Busy),
                RunState.AwaitingReview => ("待核查", "待核查", TaskStatusKind.Warning),
                RunState.Completed => ("已完成", "已完成", TaskStatusKind.Success),
                RunState.Stopped => ("已停止", "已停止", TaskStatusKind.Idle),
                RunState.Faulted => ("异常停止", "异常停止", TaskStatusKind.Danger),
                _ => Session.Schedule.State == ScheduleState.Enabled
                    ? (IsWeeklyQuotaPaused?.Invoke(this) == true ? ("周额度已满，等待刷新", "超额等待", TaskStatusKind.Warning) : ("等待计划", "等待定时", TaskStatusKind.Info))
                    : ("尚未运行", "未运行", TaskStatusKind.Idle)
            }
        };
}

public enum TaskStatusKind { Idle, Burning, Paused, Busy, Info, Success, Warning, Danger }
public enum NoticeLevel { Info, Success, Warning, Error }
/// <summary>面板通知；TaskId 用于把提示定位到任务，文字不含key。</summary>
public sealed record PanelNotice(string Text, NoticeLevel Level, Guid? TaskId = null)
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
}
/// <summary>运行前确认摘要：Facts/Risks 供界面分条展示，Text 为完整文字（测试和回退显示使用）。</summary>
public sealed record RunPreview(string Title, IReadOnlyList<KeyValuePair<string, string>> Facts,
    IReadOnlyList<KeyValuePair<string, string>> RequestFacts, IReadOnlyList<string> Risks, string Text)
{
    public override string ToString() => Text;
}

/// <summary>把一次面板动作绑定到用户确认时观察到的任务生命周期。</summary>
public readonly record struct RunStartGuard(int LifecycleVersion, bool WaitOnlyIfPreviouslyBusy);

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
    private readonly object _runtimeGate = new();
    private readonly Dictionary<string, Guid> _keys = new(StringComparer.Ordinal);
    private readonly List<TaskRuntime> _tasks = [];
    private Guid[] _batch = [];
    private bool _batchActive;
    private Task? _batchOperation;
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
    public NoticeLevel LastNoticeLevel { get; private set; } = NoticeLevel.Info;
    public string BatchSummary { get; private set; } = "尚未运行全部。";
    /// <summary>是否已有批量运行结果；面板据此显示批量进度，不再比较提示文字。</summary>
    public bool HasBatch => _batch.Length > 0;
    public int BatchTaskCount => _batch.Length;
    public event Action<PanelNotice>? Noticed;
    /// <summary>单个key同时在途请求上限；与 <see cref="Limiter"/> 的全应用上限分开。</summary>
    public int PerKeyConcurrency => _workspace.Catalog.PerKeyConcurrency;
    public TaskWorkspace Workspace => _workspace;
    public bool BatchActive => _batchActive;
    /// <summary>捕获单个任务的运行护栏；面板在显示风险摘要前调用，并在确认后原样传回。</summary>
    public RunStartGuard CaptureRunGuard(Guid id)
    {
        var row = Find(id);
        lock (_runtimeGate) return new(row.LifecycleVersion, IsBusyForRunStart(row));
    }
    /// <summary>捕获当前启用任务的运行护栏，供“运行/继续全部”的确认摘要使用。</summary>
    public IReadOnlyDictionary<Guid, RunStartGuard> CaptureRunGuards()
    {
        lock (_runtimeGate)
        {
            return _tasks.Where(row => row.Configuration.Enabled)
                .ToDictionary(row => row.Configuration.Id,
                    row => new RunStartGuard(row.LifecycleVersion, IsBusyForRunStart(row)));
        }
    }
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
        if (pending > 0) { LastNotice = $"发现{pending}个未完成或待核查的任务；核查后可点运行/继续完成剩余目标。重开未发送请求。"; LastNoticeLevel = NoticeLevel.Warning; }
        _initialized = true;
        }
        finally { _initialization.Release(); }
    }
    private async Task<TaskRuntime> AddRuntimeAsync(TaskConfiguration task)
    {
        var slot = new ExecutorSlot();
        var session = new PersistentRunSession(slot, _workspace.Storage.RunState(task), timeProvider: _time, limiter: Limiter);
        var runtime = new TaskRuntime(task, session, slot) { SavedNextUtc = null };
        runtime.IsWeeklyQuotaPaused = r => IsWeeklyQuotaPaused(r);
        session.CanStartSchedule = () => CanScheduleRun(runtime);
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
    public bool CanChangeConcurrency => !_tasks.Any(row => row.Active || row.Session.Schedule.State == ScheduleState.Enabled);
    public async Task SetConcurrencyAsync(int maximum, int? perKey = null)
    {
        if (!CanChangeConcurrency)
            throw new InvalidOperationException("请先停止全部任务及计划再修改并发上限。");
        await _workspace.SetConcurrencyAsync(maximum, perKey, _lifetime.Token);
        Limiter.SetMaximum(maximum);
    }
    public string RunAllPreview(bool continueRemaining = false)
    {
        var rows = _tasks.Where(row => row.Configuration.Enabled).ToArray();
        var groups = rows.GroupBy(row => string.IsNullOrWhiteSpace(row.Configuration.QuotaGroup)
            ? "未分组（不表示独立额度）" : row.Configuration.QuotaGroup);
        long Target(TaskRuntime row) => continueRemaining && (row.Active || row.HasRemainder) && row.Snapshot.Parameters is { } parameters
            ? parameters.TargetTokens : row.Configuration.TargetTokens;
        var lines = groups.Select(group => $"{group.Key}：{group.Count()}个key，目标合计{group.Sum(Target):N0} tokens"
            + (continueRemaining ? $"，剩余约{group.Sum(row => Math.Max(0, Target(row) - (row.Active || row.HasRemainder ? row.Snapshot.ConfirmedUsage.TotalTokens : 0))):N0} tokens" : ""));
        var alreadyActive = rows.Count(row => (row.Preparing || row.Operation is { IsCompleted: false }
            || row.Session.CurrentRun.State is RunState.Running or RunState.Stopping or RunState.AwaitingReview)
            && row.Session.CurrentRun.State != RunState.Paused);
        var missingCredentials = rows.Count(row => !row.Configuration.CredentialSaved);
        return string.Join(Environment.NewLine, lines)
            + $"\n范围：{rows.Length}个已启用key，其中{alreadyActive}个正在准备/运行的任务只等待收尾；暂停任务会继续。"
            + (missingCredentials > 0 ? $"\n{missingCredentials}个任务未保存key，将无法运行。" : "")
            + "\n\n" + RequestRiskSummary
            + "\n多个key不代表多份独立积分池；同账户应归同组。请核查该账户滚动5小时/周额度与外部用量。组目标超过1.2亿可能扣通用积分；较低也不能保证绝对安全或精确返赠。5小时仅是运行间隔。";
    }
    public string RunSelectedPreview(IEnumerable<Guid> ids, bool continueRemaining = false)
        => PreviewSelected(ids, continueRemaining).Text;
    public RunPreview PreviewSelected(IEnumerable<Guid> ids, bool continueRemaining = false)
    {
        var selected = ids.ToHashSet();
        var chosen = _tasks.Where(row => selected.Contains(row.Configuration.Id)).ToArray();
        var rows = chosen.Where(row => row.Configuration.Enabled).ToArray();
        long Target(TaskRuntime row) => continueRemaining && (row.Active || row.HasRemainder) && row.Snapshot.Parameters is { } parameters
            ? parameters.TargetTokens : row.Configuration.TargetTokens;
        long Remaining(TaskRuntime row) => Math.Max(0, Target(row) - (continueRemaining && (row.Active || row.HasRemainder)
            ? row.Snapshot.ConfirmedUsage.TotalTokens : 0));
        var groups = rows.GroupBy(row => string.IsNullOrWhiteSpace(row.Configuration.QuotaGroup) ? "未分组（不表示独立额度）" : row.Configuration.QuotaGroup).ToArray();
        var facts = new List<KeyValuePair<string, string>> { new("范围", $"{rows.Length} 个已勾选且启用的 key") };
        if (chosen.Length > rows.Length) facts.Add(new("跳过", $"{chosen.Length - rows.Length} 个已停用的 key"));
        foreach (var group in groups)
            facts.Add(new(group.Key, $"{group.Count()} 个 key，目标合计 {group.Sum(Target):N0} tokens，剩余约 {group.Sum(Remaining):N0}"));
        const string pool = "多个 key 不代表多份独立积分池；同一账户的 key 请填相同额度组。";
        var reference = ConsumptionPolicy.GetTargetTokens(95);
        var overGroups = groups.Where(group => group.Sum(Target) > reference)
            .Select(group => $"{group.Key}目标合计{group.Sum(Target):N0} tokens").ToArray();
        var overRisk = overGroups.Length == 0 ? null
            : $"{string.Join("；", overGroups)}，超过同一账户1.2亿经验目标，可能扣除通用积分。实际扣减与返赠以官方记录为准。";
        var busy = rows.Count(row => IsBusyForRunStart(row));
        var review = rows.Count(row => row.Session.Recovery.State == RecoveryState.NeedsReview);
        var scope = (busy > 0 ? $"其中{busy}个正在运行的任务只等待收尾，不会重复启动。" : "")
            + (review > 0 ? $"{review}个待核查的任务不会运行，需要单独核查后再继续。" : "");
        var risks = new List<string>();
        if (overRisk is not null) risks.Add(overRisk);
        if (scope.Length > 0) risks.Add(scope);
        risks.Add(pool);
        risks.Add(RollingQuotaNote);
        var text = string.Join(Environment.NewLine, groups.Select(group => $"{group.Key}：{group.Count()}个key，目标合计{group.Sum(Target):N0} tokens"))
            + $"\n范围：{rows.Length}个已勾选且启用的key。{scope}"
            + (overRisk is null ? "" : $"\n{overRisk}")
            + $"\n\n{RequestRiskSummary}\n\n额度按滚动窗口计算，请核查官方剩余额度与同账户外部用量。";
        return new($"运行/继续 {rows.Length} 个所选任务", facts, RequestFacts, risks, text);
    }
    /// <summary>为所选任务捕获运行护栏；面板必须在显示批量确认之前调用，确认后原样传给 <see cref="RunOrContinueSelectedAsync(IEnumerable{Guid}, IReadOnlyDictionary{Guid, RunStartGuard})"/>。</summary>
    public IReadOnlyDictionary<Guid, RunStartGuard> CaptureRunGuards(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        return CaptureRunGuards().Where(pair => selected.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
    }
    public string RunPreview(Guid id, bool schedule = false) => PreviewRun(id, schedule).Text;
    public RunPreview PreviewRun(Guid id, bool schedule = false)
    {
        var row = Find(id);
        var continues = row.HasRemainder && row.Snapshot.Parameters is not null;
        var target = continues ? row.Snapshot.Parameters!.TargetTokens : row.Configuration.TargetTokens;
        var confirmed = continues ? row.Snapshot.ConfirmedUsage.TotalTokens : 0;
        var remaining = Math.Max(0, target - confirmed);
        var action = schedule ? "启用周期计划（立即开始首轮）"
            : row.Session.CurrentRun.State == RunState.Paused ? "继续已暂停轮次"
            : continues ? "继续剩余目标" : "开始新一轮";
        var group = string.IsNullOrWhiteSpace(row.Configuration.QuotaGroup)
            ? "未分组（不代表独立额度）" : row.Configuration.QuotaGroup;
        var plan = schedule ? $"计划间隔：每{row.Configuration.IntervalHours:0.##}小时；以后每轮都会重新发送生成请求。"
            : "计划状态：此操作不启用周期计划。";
        var weekly = GetWeeklyQuotaStatus(row);
        var weeklyText = $"{weekly.ConsumedPoints:N0} / {weekly.QuotaPoints:N0} 积分 ({weekly.ConsumedTokens:N0} tokens)";
        var facts = new List<KeyValuePair<string, string>>
        {
            new("任务", row.Configuration.DisplayName), new("操作", action), new("额度组", group),
            new("本轮目标", $"{target:N0} tokens"), new("已确认", $"{confirmed:N0} tokens"), new("剩余", $"{remaining:N0} tokens"),
            new("本周专属积分", weeklyText),
            new("定时", schedule ? $"每 {row.Configuration.IntervalHours:0.##} 小时自动开始新一轮" : "不启用")
        };
        var notes = new List<string> { TargetRisk.Describe(target), RollingQuotaNote };
        if (weekly.IsQuotaExceeded)
        {
            notes.Insert(0, $"【周额度超额警告】本周已累计消耗 {weekly.ConsumedPoints:N0} 点积分（已达上限 {weekly.QuotaPoints:N0} 点）。继续运行可能会消耗通用积分！");
        }
        else if (weekly.WouldExceedWith(remaining))
        {
            notes.Insert(0, $"【周额度超额预警】本周已消耗 {weekly.ConsumedPoints:N0} 点积分，本次运行剩余目标约需 {ConsumptionPolicy.TokensToPoints(remaining):N0} 点积分，运行后预计超出周上限 {weekly.QuotaPoints:N0} 点！");
        }
        var text = $"任务：{row.Configuration.DisplayName}\n操作：{action}\n额度组：{group}"
            + $"\n本轮目标：{target:N0} tokens；已确认：{confirmed:N0}；剩余：{remaining:N0} tokens。"
            + $"\n本周专属额度：{weeklyText}"
            + $"\n{plan}\n\n{RequestRiskSummary}\n\n{TargetRisk.Describe(target)}"
            + "\n额度按滚动窗口计算；请核查官方剩余额度及账户其他key/外部用量。实际usage、扣减与返赠可能不同。";
        var title = schedule ? $"启用定时：{row.Configuration.DisplayName}" : $"{action}：{row.Configuration.DisplayName}";
        return new(title, facts, RequestFacts, notes, text);
    }
    public bool IsWeeklyQuotaPaused(TaskRuntime row)
    {
        if (!row.Configuration.WeeklyQuotaAutoStop) return false;
        var status = GetWeeklyQuotaStatus(row);
        return status.IsQuotaExceeded || status.RemainingPoints <= 0;
    }

    private bool CanScheduleRun(TaskRuntime row)
    {
        return !IsWeeklyQuotaPaused(row);
    }

    public WeeklyQuotaStatus GetWeeklyQuotaStatus(TaskRuntime row, DateTimeOffset? now = null)
    {
        var nowUtc = now ?? (_time?.GetUtcNow() ?? DateTimeOffset.UtcNow);
        var group = row.Configuration.QuotaGroup;
        var tasksInGroup = string.IsNullOrEmpty(group)
            ? [row]
            : _tasks.Where(t => t.Configuration.QuotaGroup == group).ToArray();

        var allHistory = new List<RunRecord>();
        long activeTokens = 0;
        DateTimeOffset? activeStarted = null;

        foreach (var t in tasksInGroup)
        {
            if (t.Session.Recovery.Document?.History is { } history)
            {
                allHistory.AddRange(history);
            }
            if (t.Active && t.Snapshot.ConfirmedUsage is { TotalTokens: > 0 } usage)
            {
                activeTokens += usage.TotalTokens;
                activeStarted = t.Session.Recovery.Document?.CurrentRun?.StartedUtc ?? nowUtc;
            }
        }

        var activeSnapshot = activeTokens > 0
            ? new RunSnapshot(RunState.Running, row.Snapshot.Parameters ?? new(row.Configuration.ModelId, row.Configuration.TargetTokens), new(activeTokens, 0, activeTokens), 0, 0, 0, null)
            : null;

        return WeeklyQuotaCalculator.CalculateStatus(
            group,
            row.Configuration,
            allHistory,
            activeSnapshot,
            activeStarted,
            nowUtc);
    }

    public async Task UpdateWeeklyQuotaSettingsAsync(Guid id, DayOfWeek resetDay, TimeSpan resetTime,
        long quotaPoints, bool autoStop, long manualAdjustment)
    {
        var row = Find(id);
        var group = row.Configuration.QuotaGroup;
        var targetRows = string.IsNullOrEmpty(group)
            ? [row]
            : _tasks.Where(t => t.Configuration.QuotaGroup == group).ToArray();

        foreach (var target in targetRows)
        {
            var updated = target.Configuration with
            {
                WeeklyResetDay = resetDay,
                WeeklyResetTime = resetTime,
                WeeklyQuotaPoints = quotaPoints,
                WeeklyQuotaAutoStop = autoStop,
                WeeklyQuotaManualAdjustment = manualAdjustment
            };
            await _workspace.SaveTaskAsync(updated, _lifetime.Token);
            target.Configuration = updated;
        }
    }

    private const string RollingQuotaNote = "额度按滚动窗口计算，请先核查官方剩余额度和同账户其他 key 或外部用量；实际 usage、扣减与返赠可能不同。";
    private static readonly KeyValuePair<string, string>[] RequestFacts =
    [
        new("单笔输入", "约 34 万字符程序合成文本（不是你的内容）"),
        new("单笔输出上限", "1,024 tokens"),
        new("单笔本地预留", "约 241,024 tokens（非费用上限）"),
        new("同类实测", "约 218,852 tokens/笔，完成目标通常需要多笔")
    ];
    private const string RequestRiskSummary = "每笔生成使用程序合成的340,000字符（34万）技术文本（不是你的输入），输出上限1,024 tokens；本地预算按最多预留约241,024 tokens/笔（240,000输入估计+1,024输出），这不是实际usage或费用上限。历史同类请求记录为218,852 tokens；实际可能变化，完成一个目标可能需要多笔请求。";

    /// <summary>只查询模型列表验证凭据和活动模型，不构造生成输入，不调用completion接口。</summary>
    public async Task<string> ValidateConnectionAsync(Guid id)
    {
        var row = Find(id);
        CancellationTokenSource validationCancellation;
        TaskCompletionSource validationOperation;
        lock (_runtimeGate)
        {
            if (_disposed) throw new InvalidOperationException("应用正在退出。连接验证未开始。");
            if (row.Active || row.Session.Schedule.State == ScheduleState.Enabled)
                throw new InvalidOperationException("任务正在运行、准备中或计划已启用，请等待收尾/关闭计划后再验证连接。");
            if (row.Validating) throw new InvalidOperationException("此任务已经在验证连接。");
            row.Validating = true;
            row.ValidationSucceeded = false;
            row.Message = "";
            validationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            row.ValidationCancellation = validationCancellation;
            validationOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            row.ValidationOperation = validationOperation;
        }
        var validationToken = validationCancellation.Token;
        try
        {
            if (!row.Configuration.CredentialSaved)
                throw new InvalidOperationException("此任务尚未保存API key。");
            var credential = await _workspace.Storage.Credentials(row.Configuration).LoadAsync(validationToken)
                ?? throw new InvalidOperationException("未找到已保存的API key。");
            ClaimKey(row, credential);
            IReadOnlyList<ModelInfo> models;
            using (await Limiter.AcquireAsync(validationToken))
                models = await _client.GetModelsAsync(credential, validationToken);
            var model = models.FirstOrDefault(item => item.Id == row.Configuration.ModelId && item.IsRebateEligible);
            row.Message = model is null
                ? $"连接成功，读取到{models.Count}个模型，但活动模型不可用。仅调用GET /models，未发送生成请求。"
                : $"连接成功，活动模型可用。仅调用GET /models，未发送生成请求。";
            row.MessageLevel = model is null ? NoticeLevel.Warning : NoticeLevel.Success;
            lock (_runtimeGate) row.ValidationSucceeded = model is not null;
            Notify($"{row.Configuration.DisplayName}：{row.Message}", model is null ? NoticeLevel.Warning : NoticeLevel.Success, row);
            return row.Message;
        }
        catch (OperationCanceledException)
        {
            row.Message = "连接验证已取消；未发送生成请求。"; row.MessageLevel = NoticeLevel.Info;
            Notify($"{row.Configuration.DisplayName}：{row.Message}", NoticeLevel.Info, row);
            return row.Message;
        }
        catch (SenseNovaApiException exception)
        {
            row.Message = $"连接验证失败：{exception.Kind}。未发送生成请求。"; row.MessageLevel = NoticeLevel.Error;
            Notify($"{row.Configuration.DisplayName}：{row.Message}", NoticeLevel.Error, row);
            return row.Message;
        }
        catch (LocalStorageException)
        {
            row.Message = "凭据或本地任务记录不可读，连接验证未发送请求。"; row.MessageLevel = NoticeLevel.Error;
            Notify($"{row.Configuration.DisplayName}：{row.Message}", NoticeLevel.Error, row);
            return row.Message;
        }
        catch (InvalidOperationException exception)
        {
            row.Message = exception.Message;
            Notify($"{row.Configuration.DisplayName}：{row.Message}", NoticeLevel.Warning, row);
            return row.Message;
        }
        finally
        {
            lock (_runtimeGate)
            {
                row.Validating = false;
                ReleaseKey(row);
                validationOperation.TrySetResult();
                if (ReferenceEquals(row.ValidationOperation, validationOperation)) row.ValidationOperation = null;
                if (ReferenceEquals(row.ValidationCancellation, validationCancellation)) row.ValidationCancellation = null;
                validationCancellation.Dispose();
            }
        }
    }
    private async Task CancelValidationAsync(TaskRuntime row)
    {
        Task? validation = null;
        CancellationTokenSource? cancellation = null;
        lock (_runtimeGate)
        {
            if (!row.Validating) return;
            row.LifecycleVersion++;
            validation = row.ValidationOperation?.Task;
            cancellation = row.ValidationCancellation;
        }
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (validation is null) return;
        try { await validation.WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token); }
        catch (TimeoutException) { throw new InvalidOperationException("连接验证仍在收尾；未重置或启动任务，请稍后重试。"); }
    }
    public Task RunAllAsync() => BeginBatch(false, null);
    public Task RunOrContinueAllAsync() => BeginBatch(true, null);
    public Task RunOrContinueAllAsync(IReadOnlyDictionary<Guid, RunStartGuard> guards) => BeginBatch(true, guards);
    public Task RunOrContinueSelectedAsync(IEnumerable<Guid> ids) => RunOrContinueSelectedAsync(ids, CaptureRunGuards(ids));
    /// <summary>按确认前捕获的护栏批量运行/继续：确认期间已收尾的任务不会被当作新一轮重新开始。</summary>
    public Task RunOrContinueSelectedAsync(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, RunStartGuard> guards)
    {
        var selected = ids.ToHashSet();
        return BeginBatch(true, guards.Where(pair => selected.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value), selected);
    }
    private Task BeginBatch(bool continueRemaining, IReadOnlyDictionary<Guid, RunStartGuard>? guards, IReadOnlySet<Guid>? selected = null)
    {
        if (_batchActive) return RunBatchAsync(continueRemaining, guards);
        return _batchOperation = RunBatchAsync(continueRemaining, guards, selected);
    }
    private async Task RunBatchAsync(bool continueRemaining, IReadOnlyDictionary<Guid, RunStartGuard>? guards, IReadOnlySet<Guid>? selected = null)
    {
        if (_batchActive)
        {
            if (!continueRemaining) throw new InvalidOperationException("运行全部尚未结束。");
            var resumed = ResumeAll(guards);
            Notify(resumed > 0 ? $"批量运行进行中：已继续{resumed}个暂停任务，定时未恢复；不在本批中的任务没有启动。"
                : "批量运行进行中，没有启动新任务；新勾选的任务请单独运行，或等本批结束后再批量运行。", NoticeLevel.Info);
            return;
        }
        var rows = _tasks.Where(row => row.Configuration.Enabled && (selected is null || selected.Contains(row.Configuration.Id))
            && (guards is null || guards.ContainsKey(row.Configuration.Id))).ToArray();
        _batch = rows.Select(row => row.Configuration.Id).ToArray();
        _batchActive = true;
        BatchSummary = $"批量运行：{rows.Length}个任务。";
        try
        {
            await Task.WhenAll(rows.Select(row =>
            {
                var guard = guards is not null && guards.TryGetValue(row.Configuration.Id, out var captured)
                    ? captured : CaptureRunGuard(row.Configuration.Id);
                return continueRemaining
                    ? RunOrContinueAsync(row.Configuration.Id, expectedLifecycleVersion: guard.LifecycleVersion,
                        waitOnlyIfPreviouslyBusy: guard.WaitOnlyIfPreviouslyBusy)
                    : StartOrAwaitAsync(row, guard);
            }));
        }
        finally
        {
            _batchActive = false;
            var completed = rows.Count(row => row.Session.CurrentRun.State == RunState.Completed && string.IsNullOrEmpty(row.Message));
            var stopped = rows.Count(row => row.Session.CurrentRun.State == RunState.Stopped && string.IsNullOrEmpty(row.Message));
            var other = rows.Length - completed - stopped;
            BatchSummary = $"批量运行收尾：完成{completed}，停止{stopped}，异常/待核查/未开始{other}。";
            Notify(BatchSummary, other > 0 ? NoticeLevel.Warning : completed == rows.Length ? NoticeLevel.Success : NoticeLevel.Info);
        }
    }
    private async Task StartOrAwaitAsync(TaskRuntime row, RunStartGuard guard)
    {
        Task? validation = null;
        lock (_runtimeGate)
        {
            if (guard.LifecycleVersion != row.LifecycleVersion)
            {
                row.Message = "任务状态已变化，请重新确认本轮目标后再运行。";
                return;
            }
            if (row.Validating) validation = row.ValidationOperation?.Task;
        }
        if (validation is not null)
        {
            await validation;
            lock (_runtimeGate)
            {
                if (row.LifecycleVersion != guard.LifecycleVersion || !row.ValidationSucceeded || row.Resetting)
                {
                    if (row.LifecycleVersion != guard.LifecycleVersion)
                        row.Message = "连接验证或任务控制已取消；未开始生成请求。";
                    return;
                }
            }
        }
        if (guard.WaitOnlyIfPreviouslyBusy && !row.Active && row.Session.Schedule.State != ScheduleState.Enabled)
        {
            row.Message = "原任务已收尾，请重新确认目标后再运行；本次未新增请求。";
            return;
        }
        var existing = row.Preparing || row.Active || row.Session.Schedule.State == ScheduleState.Enabled;
        if (row.Preparing) await row.StartedOperation.Task;
        if (existing)
            await row.Session.WaitForCompletionAsync();
        else await StartAsync(row.Configuration.Id, expectedLifecycleVersion: guard.LifecycleVersion,
            waitOnlyIfPreviouslyBusy: guard.WaitOnlyIfPreviouslyBusy);
    }
    /// <summary>同一个按钮根据状态开始、恢复暂停或继续剩余；未知记录不自动确认。</summary>
    public async Task RunOrContinueAsync(Guid id, bool reviewed = false, int? expectedLifecycleVersion = null, bool waitOnlyIfPreviouslyBusy = false)
    {
        var row = Find(id);
        Task? validation = null;
        int observedVersion;
        lock (_runtimeGate)
        {
            if (row.Resetting) { row.Message = "正在重置，请等待收尾。"; return; }
            if (expectedLifecycleVersion is { } expected && expected != row.LifecycleVersion)
            {
                row.Message = "任务状态已变化，请重新确认本轮目标后再运行。";
                return;
            }
            observedVersion = row.LifecycleVersion;
            if (row.Validating) validation = row.ValidationOperation?.Task;
        }
        if (validation is not null)
        {
            await validation;
            lock (_runtimeGate)
            {
                if (row.LifecycleVersion != observedVersion || !row.ValidationSucceeded || row.Resetting)
                {
                    if (row.LifecycleVersion != observedVersion) row.Message = "连接验证或任务控制已取消；未开始生成请求。";
                    return;
                }
            }
        }
        lock (_runtimeGate)
        {
            if (row.LifecycleVersion != observedVersion || row.Resetting)
            {
                if (row.LifecycleVersion != observedVersion)
                    row.Message = "连接验证或任务控制已取消；未开始生成请求。";
                return;
            }
        }
        if (waitOnlyIfPreviouslyBusy && !row.Active && row.Session.Schedule.State != ScheduleState.Enabled)
        {
            row.Message = "原任务已收尾，请重新确认目标后再运行；本次未新增请求。";
            return;
        }
        if (!row.Active && row.FinishedOperation is { Task.IsCompleted: false } finished)
        {
            await finished.Task;
            lock (_runtimeGate)
            {
                if (row.LifecycleVersion != observedVersion || row.Resetting)
                {
                    if (row.LifecycleVersion != observedVersion)
                        row.Message = "连接验证或任务控制已取消；未开始生成请求。";
                    return;
                }
            }
        }
        if (row.Session.CurrentRun.State == RunState.Paused)
        {
            lock (_runtimeGate)
            {
                if (row.LifecycleVersion != observedVersion || row.Resetting)
                {
                    if (row.LifecycleVersion != observedVersion)
                        row.Message = "连接验证或任务控制已取消；未开始生成请求。";
                    return;
                }
            }
            if (!row.Session.Resume()) { row.Message = "当前暂停仍需核查，未继续。"; return; }
            row.Message = "";
            await row.Session.WaitForCompletionAsync();
            return;
        }
        if (row.Preparing || row.Active || row.Session.Schedule.State == ScheduleState.Enabled)
        {
            if (row.Preparing) await row.StartedOperation.Task;
            await row.Session.WaitForCompletionAsync();
            return;
        }
        await StartAsync(id, continueRemaining: row.HasRemainder, reviewed: reviewed,
            expectedLifecycleVersion: observedVersion, waitOnlyIfPreviouslyBusy: waitOnlyIfPreviouslyBusy);
    }
    public async Task ResetAsync(Guid id, bool reviewed = false)
    {
        var row = Find(id);
        lock (_runtimeGate)
        {
            if (row.Resetting) throw new InvalidOperationException("任务正在重置，请等待。");
            row.Resetting = true;
            row.LifecycleVersion++;
            row.Message = "";
        }
        var previousFinished = row.FinishedOperation;
        reviewed = reviewed && row.Session.Recovery.State == RecoveryState.NeedsReview && !row.Preparing;
        try
        {
            await CancelValidationAsync(row);
            if (row.Preparing)
            {
                row.Preparation?.Cancel();
                try { await row.StartedOperation.Task.WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token); }
                catch (TimeoutException) { throw new InvalidOperationException("请求准备仍在收尾，请稍后重置。"); }
            }
            await StopAsync(id);
            if (row.Session.CurrentRun.State == RunState.Stopping || row.Session.CurrentRun.InFlightRequests > 0)
                throw new InvalidOperationException("在途请求尚未收尾，进度保留；请稍后重置。");
            if (previousFinished is not null)
            {
                try { await previousFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token); }
                catch (TimeoutException) { throw new InvalidOperationException("当前操作仍在收尾，进度保留；请稍后重置。"); }
            }
            await row.Session.ResetProgressAsync(reviewed, _lifetime.Token);
            row.Operation = null; row.NotifiedRecord = null; row.Message = "";
            Notify($"{row.Configuration.DisplayName}：进度已重置，原轮已归档；下一次运行从0开始，官方额度未改变。", NoticeLevel.Success, row);
        }
        finally
        {
            row.Resetting = false;
            if (!row.Active && row.Session.Schedule.State != ScheduleState.Enabled) ReleaseKey(row);
        }
    }
    public async Task ResetAllAsync()
    {
        var oldBatch = _batchOperation;
        await Task.WhenAll(_tasks.ToArray().Select(async row =>
        {
            try { await ResetAsync(row.Configuration.Id); }
            catch (InvalidOperationException exception) { row.Message = exception.Message; }
            catch (LocalStorageException) { row.Message = "记录保存失败，原进度保留，未完成重置。"; }
        }));
        if (oldBatch is not null)
        {
            try { await oldBatch.WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token); }
            catch (TimeoutException) { return; } // 未响应取消的旧操作仍保留互斥，不无限等或覆盖其证据。
        }
        if (!_batchActive) { _batch = []; BatchSummary = "重置全部已收尾；未重置项的原因见列表。"; }
    }
    public async Task ResetSelectedAsync(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        await Task.WhenAll(_tasks.Where(row => selected.Contains(row.Configuration.Id)).Select(async row =>
        {
            try { await ResetAsync(row.Configuration.Id); } catch (Exception exception) { row.Message = exception.Message; }
        }));
    }
    /// <summary>删除任务；返回未能删除的残留目录（配置已移除，残留目录不再被引用），全部删除成功返回 null。</summary>
    public async Task<string?> DeleteAsync(Guid id)
    {
        var row = Find(id);
        if (row.Active || row.Session.Schedule.State == ScheduleState.Enabled || row.Preparing)
            throw new InvalidOperationException("运行中或计划已启用的任务不能删除，请先停止并关闭计划。");
        var residual = await _workspace.DeleteAsync(id, _lifetime.Token);
        _tasks.Remove(row);
        ReleaseKey(row);
        return residual;
    }
    public async Task StartAsync(Guid id, bool continueRemaining = false, bool reviewed = false, bool schedule = false,
        int? expectedLifecycleVersion = null, bool waitOnlyIfPreviouslyBusy = false)
    {
        var row = Find(id);
        CancellationTokenSource preparation;
        lock (_runtimeGate)
        {
            if (expectedLifecycleVersion is { } expected && expected != row.LifecycleVersion)
            {
                row.Message = "任务状态已变化，请重新确认本轮目标后再运行。";
                return;
            }
            if (waitOnlyIfPreviouslyBusy && !row.Active && row.Session.Schedule.State != ScheduleState.Enabled)
            {
                row.Message = "原任务已收尾，请重新确认目标后再运行；本次未新增请求。";
                return;
            }
            if (_disposed || row.Preparing || row.Active || row.FinishedOperation is { Task.IsCompleted: false }
                || row.Session.Schedule.State == ScheduleState.Enabled)
            { row.Message = "任务已运行或计划已启用，未重叠启动。"; return; }
            row.Preparing = true;
            row.StartedOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            row.FinishedOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            row.Message = "";
            row.Preparation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            preparation = row.Preparation;
        }
        var preparationToken = preparation.Token;
        try
        {
            if (!row.Configuration.Enabled) throw new InvalidOperationException("任务已禁用。");
            if (!continueRemaining && row.HasRemainder)
                throw new InvalidOperationException("有未完成目标，请先运行/继续（继续剩余目标），或确认重置后从0开始。");
            if (row.Session.Recovery.State is RecoveryState.AwaitingConfirmation or RecoveryState.NeedsReview)
                await row.Session.ConfirmRecoveryAsync(reviewed, preparationToken);
            if (row.Session.Recovery.State != RecoveryState.Ready) throw new InvalidOperationException("运行记录尚未就绪。");
            if (!row.Configuration.CredentialSaved) throw new InvalidOperationException("旧任务未保存凭据，请重新配置key。");
            var credential = await _workspace.Storage.Credentials(row.Configuration).LoadAsync(preparationToken)
                ?? throw new InvalidOperationException("请先添加并保存API key。");
            ClaimKey(row, credential);
            IReadOnlyList<ModelInfo> models;
            using (await Limiter.AcquireAsync(preparationToken))
                models = await _client.GetModelsAsync(credential, preparationToken);
            var modelId = continueRemaining ? row.Snapshot.Parameters!.ModelId : row.Configuration.ModelId;
            var model = models.FirstOrDefault(model => model.Id == modelId && model.IsRebateEligible)
                ?? throw new InvalidOperationException("活动模型不可用，请核查模型权限。");
            Task<CompletionRequest> inputTask;
            lock (_keysGate) inputTask = _input ??= _createInput(_lifetime.Token);
            var input = await inputTask.WaitAsync(preparationToken);
            preparationToken.ThrowIfCancellationRequested();
            row.Executor.Inner = new CompletionRunExecutor(_completion, credential, model, input);
            var target = continueRemaining ? row.Snapshot.Parameters!.TargetTokens : row.Configuration.TargetTokens;
            var parameters = Parameters(row, target, modelId);
            await row.StartGate.WaitAsync(preparationToken);
            try
            {
                // 控件操作会先取消 preparation，再等待同一闸门；若它先发生，这里不会提交旧启动。
                if (!IsCurrentStart(row, expectedLifecycleVersion, preparationToken)) return;
                if (schedule)
                {
                    if (continueRemaining) throw new InvalidOperationException("请先继续剩余目标，再手动启用后续计划。");
                    await row.Session.EnableScheduleAsync(new(parameters, row.Configuration.IntervalHours), preparationToken);
                    lock (_runtimeGate) row.Preparing = false;
                    await PersistPlanAsync(row);
                    return;
                }
                Task<RunSnapshot> operation;
                lock (_runtimeGate)
                {
                    if (!IsCurrentStartLocked(row, expectedLifecycleVersion, preparationToken)) return;
                    operation = continueRemaining
                        ? row.Session.ContinueRemainingAsync(preparationToken, parameters)
                        : row.Session.RunOnceAsync(parameters, preparationToken);
                    row.Operation = operation;
                    row.Preparing = false;
                    row.StartedOperation.TrySetResult();
                }
            }
            finally { row.StartGate.Release(); }
            if (row.Operation is { } startedOperation) await startedOperation;
            if (!schedule) NotifyFinished(row);
        }
        catch (OperationCanceledException) { row.Message = "准备已取消，未新增请求。"; row.MessageLevel = NoticeLevel.Info; }
        catch (SenseNovaApiException exception) { row.Message = $"连接/请求失败：{exception.Kind}；请核查该任务。"; row.MessageLevel = NoticeLevel.Error; }
        catch (LocalStorageException) { row.Message = "配置/记录不可读写，已阻止该任务继续。"; row.MessageLevel = NoticeLevel.Error; }
        catch (InvalidOperationException exception) { row.Message = exception.Message; }
        catch (ArgumentException) { row.Message = "任务配置或请求参数无效。"; }
        finally
        {
            lock (_runtimeGate)
            {
                row.Preparing = false;
                row.StartedOperation.TrySetResult();
                if (row.Session.Schedule.State != ScheduleState.Enabled && !row.Active) ReleaseKey(row);
                if (row.Session.Schedule.State != ScheduleState.Enabled)
                { row.Preparation?.Dispose(); row.Preparation = null; }
                row.FinishedOperation?.TrySetResult();
            }
        }
    }
    private RunParameters Parameters(TaskRuntime row, long target, string? modelId = null)
        => new(modelId ?? row.Configuration.ModelId, target)
        {
            // 单key上限与全应用共享上限分开：等待共享许可时不占在途预算，多key按许可先后轮流发送。
            MaximumConcurrency = Math.Clamp(Math.Min(PerKeyConcurrency, Limiter.Maximum), 1, RequestBaseline.Concurrency),
            RequestTokenReservation = _input?.IsCompletedSuccessfully == true ? _input.Result.EstimatedTotalTokens : 0,
            CustomTargetEnabled = target > ConsumptionPolicy.GetTargetTokens(95)
        };
    public async Task PauseAsync(Guid id)
    {
        var row = Find(id);
        InvalidateStart(row);
        if (row.Validating)
        {
            await CancelValidationAsync(row);
            Notify($"{row.Configuration.DisplayName}：连接验证已取消，没有生成请求。", NoticeLevel.Info, row);
            return;
        }
        await row.StartGate.WaitAsync(_lifetime.Token);
        try
        {
            await row.Session.DisableScheduleAsync();
            row.Session.Pause();
            await PersistPlanAsync(row);
        }
        finally { row.StartGate.Release(); }
    }
    public bool Resume(Guid id) => Find(id).Session.Resume();
    public async Task StopAsync(Guid id)
    {
        var row = Find(id);
        InvalidateStart(row);
        await CancelValidationAsync(row);
        await row.StartGate.WaitAsync(_lifetime.Token);
        try
        {
            try { await row.Session.DisableScheduleAsync(); }
            finally { await row.Session.StopAsync(); }
            await PersistPlanAsync(row);
            if (!row.Active) ReleaseKey(row);
        }
        finally { row.StartGate.Release(); }
    }
    public async Task DisablePlanAsync(Guid id)
    {
        var row = Find(id);
        InvalidateStart(row);
        await row.StartGate.WaitAsync(_lifetime.Token);
        try
        {
            await row.Session.DisableScheduleAsync();
            await PersistPlanAsync(row);
            if (!row.Active) ReleaseKey(row);
        }
        finally { row.StartGate.Release(); }
    }
    public Task PauseAllAsync() => Task.WhenAll(_tasks.Select(row => PauseAsync(row.Configuration.Id)));
    public Task PauseSelectedAsync(IEnumerable<Guid> ids) => Task.WhenAll(_tasks.Where(row => ids.Contains(row.Configuration.Id)).Select(row => PauseAsync(row.Configuration.Id)));
    public int ResumeAll() => ResumeAll(null);
    private int ResumeAll(IReadOnlyDictionary<Guid, RunStartGuard>? guards)
    {
        lock (_runtimeGate)
        {
            return _tasks.Count(row => !row.Resetting && !row.Preparing
                && (guards is null || guards.TryGetValue(row.Configuration.Id, out var guard)
                    && guard.LifecycleVersion == row.LifecycleVersion)
                && row.Session.Resume());
        }
    }
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
            row.Message = "计划配置保存失败，未来计划已关闭。"; row.MessageLevel = NoticeLevel.Error;
        }
    }
    private void NotifyFinished(TaskRuntime row)
    {
        var record = row.Session.Recovery.Document?.CurrentRun;
        if (record is null || row.Active || record.Snapshot.State is not (RunState.Completed or RunState.Stopped or RunState.Faulted)
            || row.Session.CurrentRun.State is not (RunState.Completed or RunState.Stopped or RunState.Faulted)
            || row.NotifiedRecord == record.Id) return;
        row.NotifiedRecord = record.Id;
        var level = row.Snapshot.ReviewRequired ? NoticeLevel.Warning : record.Snapshot.State switch
        {
            RunState.Completed => NoticeLevel.Success, RunState.Faulted => NoticeLevel.Error, _ => NoticeLevel.Info
        };
        Notify($"{row.Configuration.DisplayName}：{row.Status}，确认{row.Snapshot.ConfirmedUsage.TotalTokens:N0} tokens"
            + (row.Snapshot.ReviewRequired ? "，有未知用量需核查。" : row.HasRemainder ? "；可点运行/继续完成剩余目标。" : "。"), level, row);
    }
    private static bool IsBusyForRunStart(TaskRuntime row)
        => !row.Validating && row.Session.CurrentRun.State != RunState.Paused
            && (row.Preparing || row.Operation is { IsCompleted: false }
                || row.Session.CurrentRun.State is RunState.Running or RunState.Stopping or RunState.AwaitingReview
                || row.Session.Schedule.State == ScheduleState.Enabled);
    private void Notify(string message, NoticeLevel level, TaskRuntime? row = null)
    {
        LastNotice = message; LastNoticeLevel = level;
        Noticed?.Invoke(new(message, level, row?.Configuration.Id));
    }
    private void InvalidateStart(TaskRuntime row)
    {
        CancellationTokenSource? preparation;
        lock (_runtimeGate)
        {
            row.LifecycleVersion++;
            preparation = row.Preparing ? row.Preparation : null;
        }
        try { preparation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    private bool IsCurrentStart(TaskRuntime row, int? expectedLifecycleVersion, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_runtimeGate) return IsCurrentStartLocked(row, expectedLifecycleVersion, token);
    }
    private bool IsCurrentStartLocked(TaskRuntime row, int? expectedLifecycleVersion, CancellationToken token)
    {
        var lifecycleChanged = expectedLifecycleVersion is { } expectedVersion && expectedVersion != row.LifecycleVersion;
        if (_disposed || row.Resetting || token.IsCancellationRequested || lifecycleChanged)
        {
            if (lifecycleChanged)
                row.Message = "任务状态已变化，请重新确认本轮目标后再运行。";
            return false;
        }
        return true;
    }
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
        Task[] validations;
        CancellationTokenSource[] cancellations;
        lock (_runtimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var row in _tasks)
                row.LifecycleVersion++;
            cancellations = _tasks.Select(row => row.ValidationCancellation)
                .Where(item => item is not null).Cast<CancellationTokenSource>().ToArray();
            validations = _tasks.Where(row => row.ValidationOperation is not null)
                .Select(row => row.ValidationOperation!.Task).ToArray();
        }
        foreach (var cancellation in cancellations)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (validations.Length > 0)
        {
            try { await Task.WhenAll(validations).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { foreach (var row in _tasks) row.Message = "退出时连接验证仍在收尾；未发送生成请求。"; }
            catch (Exception) { foreach (var row in _tasks) row.Message = "退出时连接验证失败；未发送生成请求。"; }
        }
        foreach (var row in _tasks.Where(row => row.Preparing)) row.Preparation?.Cancel();
        await Task.WhenAll(_tasks.Select(async row =>
        {
            await row.StartGate.WaitAsync();
            try
            {
                try { await row.Session.ShutdownAsync(); }
                catch (LocalStorageException) { row.Message = "退出时存储失败，请重开核查。"; }
            }
            finally { row.StartGate.Release(); }
        }));
        foreach (var row in _tasks) row.StartGate.Dispose();
        await _lifetime.CancelAsync();
        _lifetime.Dispose();
    }
}
