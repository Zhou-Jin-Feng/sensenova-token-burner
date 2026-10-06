using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Desktop.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private int _percentage = ConsumptionPolicy.DefaultPercentage;

    private readonly ISenseNovaClient? _client;
    private readonly IUserSettingsStore? _settingsStore;
    private readonly ICredentialStore? _credentialStore;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private Task _activeTask = Task.CompletedTask;
    private bool _isBusy;
    private bool _disposed;
    private bool _rememberCredential = true;
    private string _intervalHoursText = "5";
    private string _preferredModel = SenseNovaDefaults.RebateModel;
    private ModelInfo? _selectedModel;
    private string _statusText = "模拟模式就绪，尚未发送请求。";
    private string _credentialStatus = "内置虚拟测试 key，仅供模拟。";
    private TokenUsage? _lastUsage;
    private string _apiKey = "";
    private bool _connectionValidated;
    private readonly ExecutorSlot _executor = new();
    private readonly PersistentRunSession? _session;
    private readonly Func<CancellationToken, Task<CompletionRequest>> _createInput;
    private bool _runActive;
    private bool _preparingInput;
    private bool _reviewCompleted;
    private RunSnapshot _run = RunSnapshot.Idle;

    public MainWindowViewModel(ISenseNovaClient? client = null, IUserSettingsStore? settingsStore = null,
        ICredentialStore? credentialStore = null, bool mockMode = true, IRunStateStore? runStore = null,
        Func<CancellationToken, Task<CompletionRequest>>? createInput = null)
    {
        _client = client;
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        _session = runStore is null ? null : new(_executor, runStore);
        _createInput = createInput ?? (token => Task.Run(() => CompletionInput.Create(token), token));
        IsMockMode = mockMode;
        if (!mockMode)
        {
            _statusText = "请输入 API key 并校验连接，尚未发送请求。";
            _credentialStatus = "尚未输入 API key。";
        }
        SelectPresetCommand = new PresetCommand(this);
        SaveConfigurationCommand = new AsyncCommand(SaveConfigurationAsync, () => IsIdle && _settingsStore is not null);
        ReadModelsCommand = new AsyncCommand(ReadModelsAsync, () => IsIdle && _client is not null && HasValidCredential);
        ProbeCommand = new AsyncCommand(ProbeAsync, CanProbe);
        StartRunCommand = new AsyncCommand(StartRunAsync, CanStartRun);
        ConfirmRecoveryCommand = new AsyncCommand(ConfirmRecoveryAsync, () => IsIdle && _session is not null
            && (Recovery.State == RecoveryState.AwaitingConfirmation
                || (Recovery.State == RecoveryState.NeedsReview && ReviewCompleted)));
        CancelCommand = new CancelOperationCommand(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string BaseUrl => SenseNovaDefaults.BaseUrl;
    public bool IsMockMode { get; }
    public bool ConnectionValidated => _connectionValidated;
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (!IsIdle || IsMockMode || _apiKey == value) return;
            SetApiKey(value);
            CredentialStatus = HasValidCredential ? "key 仅在内存中；保存后按所选方式处理。" : "请填写有效的 API key。";
            StatusText = "key 已更改，请重新校验连接。";
        }
    }
    private string Credential => IsMockMode ? MockCredential.Value : _apiKey;
    private bool HasValidCredential => !string.IsNullOrWhiteSpace(Credential) && Credential.Length <= 8192
        && !Credential.Any(char.IsWhiteSpace) && !Credential.Any(char.IsControl);
    public string Model => SelectedModel?.Id ?? _preferredModel;
    public string IntervalLabel => $"每 {IntervalHoursText} 小时";
    public ICommand SelectPresetCommand { get; }
    public AsyncCommand SaveConfigurationCommand { get; }
    public AsyncCommand ReadModelsCommand { get; }
    public AsyncCommand ProbeCommand { get; }
    public AsyncCommand StartRunCommand { get; }
    public AsyncCommand ConfirmRecoveryCommand { get; }
    public ICommand CancelCommand { get; }
    public ObservableCollection<ModelInfo> Models { get; } = [];
    public bool IsIdle => !_isBusy && !_disposed;
    public bool IsBusy => _isBusy;
    public bool IsRunActive => _runActive;
    public RunSnapshot Run => _run;
    public RecoverySnapshot Recovery => _session?.Recovery ?? new(RecoveryState.NotInitialized, null);
    public bool CanPause => !_disposed && _runActive && _run.State == RunState.Running;
    public bool CanResume => !_disposed && _runActive && _run.State == RunState.Paused && !_run.ReviewRequired;
    public bool ReviewCompleted
    {
        get => _reviewCompleted;
        set { _reviewCompleted = value; Notify(nameof(ReviewCompleted)); ConfirmRecoveryCommand.Refresh(); }
    }
    public string RunUsageLabel => $"确认用量：输入 {_run.ConfirmedUsage.InputTokens:N0} / 输出 {_run.ConfirmedUsage.OutputTokens:N0} / 合计 {_run.ConfirmedUsage.TotalTokens:N0} tokens";
    public string RunDetailLabel => $"完成 {_run.CompletedRequests:N0} 笔 · 在途 {_run.InFlightRequests} · 未知 {_run.UnknownUsageRequests} · 预留 {_run.ReservedTokens:N0} tokens";
    public string RunStateLabel => _preparingInput ? "正在准备输入" : _run.State switch
    {
        RunState.Idle => "尚未开始", RunState.Running => _run.IsBackingOff ? "有限退避等待" : "运行中",
        RunState.Paused => "已暂停，等待在途结果", RunState.Stopping => "正在停止并收齐结果",
        RunState.Completed => _run.EndReason == RunEndReason.BudgetRemainder ? "已保守收尾，剩余预算不足一笔请求" : "本轮完成",
        RunState.Stopped => "本轮已停止", RunState.AwaitingReview => "等待账户核查，请先停止本轮收尾", _ => "本轮异常停止"
    };
    public string RunFailureLabel => (_run.Failure ?? _run.LastFailure) switch
    {
        null => "", RunFailureKind.Authentication => "认证失败，请检查 key。",
        RunFailureKind.RateOrQuotaLimit => "限流或额度受限，请核查官方账户。",
        RunFailureKind.StorageFailure => "运行记录保存失败，已阻止新增请求。",
        RunFailureKind.BudgetEstimateExceeded => "实际请求用量超出预留，请核查请求估计与余额。",
        RunFailureKind.InvalidRequest or RunFailureKind.ModelUnavailable => "请求参数或模型不可用。",
        RunFailureKind.Network or RunFailureKind.Transient => "网络或服务异常，用量可能未知。",
        RunFailureKind.Canceled or RunFailureKind.RequestTimeout or RunFailureKind.StopTimeout => "请求取消或超时，请核查未知用量。",
        _ => "请求或用量异常，请结合确认用量和官方账户记录核查。"
    };
    public string RecoveryLabel => Recovery.State switch
    {
        RecoveryState.Ready => "运行记录就绪；计划保持关闭。",
        RecoveryState.AwaitingConfirmation => "发现上次记录；确认后仍需手动开始。",
        RecoveryState.NeedsReview => $"待核查：{Recovery.UnresolvedRequests} 笔在途或未知；请核查官方账户用量、额度后勾选确认。",
        RecoveryState.StorageFailed => "记录保存失败，本次会话禁止继续；请修复存储后重新打开并核查。",
        RecoveryState.Corrupt => "记录不可读；请保留文件并核查，当前禁止开始。",
        _ => "运行记录尚未初始化。"
    };
    public string StatusText { get => _statusText; private set { _statusText = value; Notify(nameof(StatusText)); } }
    public string CredentialStatus { get => _credentialStatus; private set { _credentialStatus = value; Notify(nameof(CredentialStatus)); } }
    public TokenUsage? LastUsage => _lastUsage;
    public string UsageLabel => _lastUsage is null ? "尚无用量。" :
        $"最近一次{(IsMockMode ? "模拟" : "调用")}：输入 {_lastUsage.InputTokens:N0} / 输出 {_lastUsage.OutputTokens:N0} / 合计 {_lastUsage.TotalTokens:N0} tokens。";
    public string ModelCapabilityLabel => SelectedModel is null ? "请先校验连接并读取模型。" :
        $"模型能力：上下文 {SelectedModel.ContextLength?.ToString("N0") ?? "未知"} / 最大输出 {SelectedModel.MaxOutputLength?.ToString("N0") ?? "未知"} tokens";

    public string IntervalHoursText
    {
        get => _intervalHoursText;
        set { _intervalHoursText = value; Notify(nameof(IntervalHoursText)); Notify(nameof(IntervalLabel)); }
    }

    public bool RememberCredential
    {
        get => _rememberCredential;
        set { _rememberCredential = value; Notify(nameof(RememberCredential)); }
    }

    public ModelInfo? SelectedModel
    {
        get => _selectedModel;
        set
        {
            _selectedModel = value;
            Notify(nameof(SelectedModel)); Notify(nameof(Model)); Notify(nameof(ModelCapabilityLabel));
            ProbeCommand.Refresh();
            StartRunCommand.Refresh();
        }
    }

    public int Percentage
    {
        get => _percentage;
        set
        {
            _ = ConsumptionPolicy.GetTargetTokens(value);
            if (_percentage == value)
            {
                return;
            }

            _percentage = value;
            foreach (var property in new[] { nameof(Percentage), nameof(PercentageLabel), nameof(TargetTokens), nameof(TargetTokensLabel), nameof(SelectionHint) })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            }
            ProbeCommand.Refresh();
            StartRunCommand.Refresh();
        }
    }

    public string PercentageLabel => $"{Percentage}%";
    public long TargetTokens => ConsumptionPolicy.GetTargetTokens(Percentage);
    public string TargetTokensLabel => TargetTokens.ToString("N0", CultureInfo.InvariantCulture) + " tokens";
    public string SelectionHint => Percentage == 0
        ? "0% 表示不运行，不发送模型请求。"
        : "实际用量可能略超目标，请结合剩余专属积分选择比例。";

    public Task InitializeAsync() => StartOperation(async cancellationToken =>
    {
        if (_session is not null)
        {
            await _session.InitializeAsync(cancellationToken);
            RefreshRun();
        }
        if (_settingsStore is null) return;
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        if (settings is null) { StatusText = "首次使用，计划运行未启用；请校验连接。"; return; }
        settings.Validate();
        Percentage = settings.Percentage;
        IntervalHoursText = settings.IntervalHours.ToString(CultureInfo.InvariantCulture);
        RememberCredential = settings.RememberCredential;
        _preferredModel = settings.ModelId;
        Notify(nameof(Model));
        if (RememberCredential && _credentialStore is not null)
        {
            var credential = await _credentialStore.LoadAsync(cancellationToken);
            if (credential is not null)
            {
                if (IsMockMode && credential != MockCredential.Value) throw new LocalStorageException("模拟目录中的凭据不属于当前测试，请检查模拟数据目录。");
                if (!IsMockMode) SetApiKey(credential);
                CredentialStatus = "已恢复当前 Windows 用户加密的凭据，请重新校验连接。";
            }
        }
        StatusText = "已恢复配置；没有自动请求，计划运行未启用。";
    });

    public Task SaveConfigurationAsync() => StartOperation(async cancellationToken =>
    {
        if (_settingsStore is null) return;
        if (!decimal.TryParse(IntervalHoursText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours))
            throw new ArgumentException("间隔无效。");
        var settings = new AppSettings { Percentage = Percentage, IntervalHours = hours,
            ModelId = Model, RememberCredential = RememberCredential };
        settings.Validate();
        if (!HasValidCredential) throw new ArgumentException("key 无效。");
        if (RememberCredential && _credentialStore is not null)
        {
            await _credentialStore.SaveAsync(Credential, cancellationToken);
            CredentialStatus = "凭据已按当前 Windows 用户加密保存。";
        }
        else CredentialStatus = "凭据仅本次使用；已有密文保留，下次启动不读取。";
        await _settingsStore.SaveAsync(settings, cancellationToken);
        _preferredModel = settings.ModelId;
        StatusText = "配置已保存；保存不会触发模型调用或计划运行。";
    });

    public Task ReadModelsAsync() => StartOperation(async cancellationToken =>
    {
        if (_client is null) return;
        InvalidateConnection();
        if (!HasValidCredential) throw new ArgumentException("key 无效。");
        var models = await _client.GetModelsAsync(Credential, cancellationToken);
        Models.Clear();
        foreach (var model in models) Models.Add(model);
        SelectedModel = Models.FirstOrDefault(model => model.Id == _preferredModel)
            ?? Models.FirstOrDefault(model => model.IsRebateEligible);
        _connectionValidated = true;
        Notify(nameof(ConnectionValidated));
        StatusText = $"连接校验成功，读取 {Models.Count} 个模型；未查询积分余额。";
    });

    public Task ProbeAsync()
    {
        if (Percentage == 0) { StatusText = "0% 不执行模拟消耗请求。"; return Task.CompletedTask; }
        if (!CanProbe()) return Task.CompletedTask;
        var selectedModel = SelectedModel!;
        return StartOperation(async cancellationToken =>
        {
            _lastUsage = await _client!.ProbeAsync(Credential, selectedModel, cancellationToken);
            Notify(nameof(LastUsage)); Notify(nameof(UsageLabel));
            StatusText = "一次模拟调用完成；没有真实积分扣减或返赠。";
        });
    }

    public void CancelOperation() => _operation?.Cancel();

    private bool CanStartRun() => IsIdle && _session is not null && _client is ISenseNovaCompletionClient
        && _connectionValidated && HasValidCredential && Percentage > 0 && Recovery.State == RecoveryState.Ready
        && SelectedModel is { IsRebateEligible: true, ContextLength: >= CompletionInput.EstimatedInputTokens + RequestBaseline.MaximumOutputTokens,
            MaxOutputLength: >= RequestBaseline.MaximumOutputTokens }
        && SelectedModel.Id == SenseNovaDefaults.RebateModel && Models.Contains(SelectedModel);

    public Task StartRunAsync()
    {
        if (Percentage == 0) { StatusText = "0% 不运行，不发送消耗请求。"; return Task.CompletedTask; }
        if (!CanStartRun()) return Task.CompletedTask;
        var model = SelectedModel!;
        var credential = Credential;
        var target = TargetTokens;
        return StartOperation(async cancellationToken =>
        {
            _runActive = true;
            _preparingInput = true;
            _run = RunSnapshot.Idle;
            ReviewCompleted = false;
            RefreshRun();
            try
            {
                StatusText = "正在准备本轮输入；尚未开始消耗。";
                var input = await _createInput(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _executor.Bind(new CompletionRunExecutor((ISenseNovaCompletionClient)_client!, credential, model, input));
                _preparingInput = false;
                var parameters = new RunParameters(model.Id, target)
                {
                    MaximumConcurrency = RequestBaseline.Concurrency,
                    RequestTokenReservation = input.EstimatedTotalTokens
                };
                var pending = _session!.RunOnceAsync(parameters, cancellationToken);
                while (!pending.IsCompleted)
                {
                    RefreshRun();
                    await Task.WhenAny(pending, Task.Delay(500));
                }
                _run = await pending;
                RefreshRun();
                StatusText = _run.ReviewRequired ? "本轮已进入保护，请先核查账户；未知用量未计入确认总量。"
                    : _run.Failure is not null ? "本轮异常停止，请检查运行状态与账户。"
                    : IsMockMode ? "本轮模拟结束，没有真实积分扣减。" : "本轮结束；实际扣减与返赠请查看官方账户记录。";
            }
            finally
            {
                // 保持同一journal，不能因改key或重开一轮绕过StorageFailed/核查门槛。
                _executor.Clear();
                _preparingInput = false;
                _runActive = false;
                RefreshRun();
            }
        });
    }

    public void PauseRun() { if (CanPause) { _session!.Pause(); RefreshRun(); } }
    public void ResumeRun() { if (CanResume) { _session!.Resume(); RefreshRun(); } }

    public async Task StopRunAsync()
    {
        if (!_runActive || _session is null) return;
        if (_preparingInput) CancelOperation();
        await _session.StopAsync();
        RefreshRun();
    }

    public Task ConfirmRecoveryAsync() => StartOperation(async token =>
    {
        if (_session is null) return;
        await _session.ConfirmRecoveryAsync(ReviewCompleted, token);
        ReviewCompleted = false;
        RefreshRun();
        StatusText = "记录已确认；没有发送请求，请校验连接后手动开始。";
    });

    private void RefreshRun()
    {
        if (!_preparingInput && _session is not null && (_session.CurrentRun.State != RunState.Idle || !_runActive))
            _run = _session.CurrentRun.State == RunState.Idle ? Recovery.Document?.CurrentRun?.Snapshot ?? RunSnapshot.Idle : _session.CurrentRun;
        if (_runActive && !_preparingInput) _statusText = RunStateLabel;
        // 空名称表示整批属性更新；Form只需同步一次，避免每个标签触发全量控件更新。
        Notify(string.Empty);
        StartRunCommand.Refresh(); ConfirmRecoveryCommand.Refresh();
    }

    private bool CanProbe() => IsMockMode && IsIdle && _connectionValidated && _client is not null && Percentage > 0
        && SelectedModel is { IsRebateEligible: true } && SelectedModel.Id == SenseNovaDefaults.RebateModel;

    private Task StartOperation(Func<CancellationToken, Task> operation)
    {
        if (!IsIdle) return Task.CompletedTask;
        _activeTask = RunOperationAsync(operation);
        return _activeTask;
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        SetBusy(true);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = source;
        try { await operation(source.Token); }
        catch (OperationCanceledException) { StatusText = "操作已取消。"; }
        catch (SenseNovaApiException exception) { StatusText = SafeApiMessage(exception.Kind); }
        catch (LocalStorageException) { StatusText = "本地数据无法安全读取或保存，请检查数据目录和当前 Windows 用户。"; }
        catch (ArgumentException) { StatusText = "配置无效，请检查 key、比例、模型和正数运行间隔。"; }
        catch (Exception) { StatusText = "操作未完成，请检查配置与数据权限。"; }
        finally { _operation = null; SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        Notify(nameof(IsBusy)); Notify(nameof(IsIdle));
        SaveConfigurationCommand.Refresh(); ReadModelsCommand.Refresh(); ProbeCommand.Refresh();
        StartRunCommand.Refresh(); ConfirmRecoveryCommand.Refresh();
        ((CancelOperationCommand)CancelCommand).Refresh();
    }

    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private void SetApiKey(string value)
    {
        _apiKey = value;
        InvalidateConnection();
        Notify(nameof(ApiKey));
        ReadModelsCommand.Refresh();
    }

    private void InvalidateConnection()
    {
        _connectionValidated = false;
        Models.Clear();
        SelectedModel = null;
        _lastUsage = null;
        Notify(nameof(ConnectionValidated)); Notify(nameof(UsageLabel)); Notify(nameof(LastUsage));
    }

    private static string SafeApiMessage(ApiFailureKind kind) => kind switch
    {
        ApiFailureKind.Authentication => "认证失败，请检查 API key。",
        ApiFailureKind.RateOrQuotaLimit => "请求限流或额度受限，请核查账户。",
        ApiFailureKind.ModelUnavailable => "模型或接口当前不可用。",
        ApiFailureKind.InvalidRequest => "请求参数或模型能力无效。",
        ApiFailureKind.Protocol => "响应格式或用量无效，请核查账户。",
        _ => "服务连接未完成，请检查网络并核查账户。"
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        SetBusy(_isBusy);
        _lifetime.Cancel();
        await _activeTask;
        if (_session is not null)
        {
            try { await _session.ShutdownAsync(); }
            catch (LocalStorageException) { StatusText = "退出时记录保存失败；下次启动需核查，请保留数据文件。"; }
        }
        _apiKey = "";
        _executor.Clear();
        _lifetime.Dispose();
    }

    private sealed class ExecutorSlot : IRunRequestExecutor
    {
        private CompletionRunExecutor? _bound;
        public void Bind(CompletionRunExecutor executor) => _bound = executor;
        public void Clear() => _bound = null;
        public void ValidateParameters(RunParameters parameters) => Get().ValidateParameters(parameters);
        public Task<TokenUsage> ExecuteAsync(RunParameters parameters, CancellationToken token) => Get().ExecuteAsync(parameters, token);
        private CompletionRunExecutor Get() => _bound ?? throw new InvalidOperationException("尚未准备请求。");
    }

    private sealed class CancelOperationCommand(MainWindowViewModel viewModel) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => viewModel.IsBusy;
        public void Execute(object? parameter) => viewModel.CancelOperation();
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class PresetCommand(MainWindowViewModel viewModel) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => TryGetPreset(parameter, out _);

        public void Execute(object? parameter)
        {
            if (TryGetPreset(parameter, out var percentage))
            {
                viewModel.Percentage = percentage;
            }
        }

        private static bool TryGetPreset(object? parameter, out int percentage)
            => int.TryParse(parameter as string, NumberStyles.None, CultureInfo.InvariantCulture, out percentage)
                && percentage is 25 or 50 or 75 or 95;
    }
}
