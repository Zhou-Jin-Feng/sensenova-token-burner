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

    public MainWindowViewModel(ISenseNovaClient? client = null, IUserSettingsStore? settingsStore = null,
        ICredentialStore? credentialStore = null)
    {
        _client = client;
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        SelectPresetCommand = new PresetCommand(this);
        SaveConfigurationCommand = new AsyncCommand(SaveConfigurationAsync, () => IsIdle && _settingsStore is not null);
        ReadModelsCommand = new AsyncCommand(ReadModelsAsync, () => IsIdle && _client is not null);
        ProbeCommand = new AsyncCommand(ProbeAsync, CanProbe);
        CancelCommand = new CancelOperationCommand(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string BaseUrl => SenseNovaDefaults.BaseUrl;
    public string Model => SelectedModel?.Id ?? _preferredModel;
    public string IntervalLabel => $"每 {IntervalHoursText} 小时";
    public ICommand SelectPresetCommand { get; }
    public AsyncCommand SaveConfigurationCommand { get; }
    public AsyncCommand ReadModelsCommand { get; }
    public AsyncCommand ProbeCommand { get; }
    public ICommand CancelCommand { get; }
    public ObservableCollection<ModelInfo> Models { get; } = [];
    public bool IsIdle => !_isBusy && !_disposed;
    public bool IsBusy => _isBusy;
    public string StatusText { get => _statusText; private set { _statusText = value; Notify(nameof(StatusText)); } }
    public string CredentialStatus { get => _credentialStatus; private set { _credentialStatus = value; Notify(nameof(CredentialStatus)); } }
    public TokenUsage? LastUsage => _lastUsage;
    public string UsageLabel => _lastUsage is null ? "尚无模拟用量。" :
        $"最近一次模拟：输入 {_lastUsage.InputTokens:N0} / 输出 {_lastUsage.OutputTokens:N0} / 合计 {_lastUsage.TotalTokens:N0} tokens。";
    public string ModelCapabilityLabel => SelectedModel is null ? "请先读取模拟模型。" :
        $"模拟能力：上下文 {SelectedModel.ContextLength?.ToString("N0") ?? "未知"} / 最大输出 {SelectedModel.MaxOutputLength?.ToString("N0") ?? "未知"} tokens";

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
        if (_settingsStore is null) return;
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        if (settings is null) { StatusText = "首次使用模拟配置，计划运行未启用。"; return; }
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
                if (credential != MockCredential.Value) throw new LocalStorageException("模拟目录中的凭据不属于当前测试，请检查模拟数据目录。");
                CredentialStatus = "已恢复当前 Windows 用户加密的测试凭据。";
            }
        }
        StatusText = "已恢复模拟配置；没有自动请求，计划运行未启用。";
    });

    public Task SaveConfigurationAsync() => StartOperation(async cancellationToken =>
    {
        if (_settingsStore is null) return;
        if (!decimal.TryParse(IntervalHoursText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours))
            throw new ArgumentException("间隔无效。");
        var settings = new AppSettings { Percentage = Percentage, IntervalHours = hours,
            ModelId = Model, RememberCredential = RememberCredential };
        settings.Validate();
        if (RememberCredential && _credentialStore is not null)
        {
            await _credentialStore.SaveAsync(MockCredential.Value, cancellationToken);
            CredentialStatus = "测试凭据已按当前 Windows 用户加密保存。";
        }
        else CredentialStatus = "测试凭据仅本次使用，不写入新的密文。";
        await _settingsStore.SaveAsync(settings, cancellationToken);
        _preferredModel = settings.ModelId;
        StatusText = "模拟配置已保存；保存不会触发模型调用或计划运行。";
    });

    public Task ReadModelsAsync() => StartOperation(async cancellationToken =>
    {
        if (_client is null) return;
        var models = await _client.GetModelsAsync(MockCredential.Value, cancellationToken);
        Models.Clear();
        foreach (var model in models) Models.Add(model);
        SelectedModel = Models.FirstOrDefault(model => model.Id == _preferredModel)
            ?? Models.FirstOrDefault(model => model.IsRebateEligible);
        StatusText = $"已读取 {Models.Count} 个模拟模型；仅已确认活动模型可模拟调用。";
    });

    public Task ProbeAsync()
    {
        if (Percentage == 0) { StatusText = "0% 不执行模拟消耗请求。"; return Task.CompletedTask; }
        if (!CanProbe()) return Task.CompletedTask;
        var selectedModel = SelectedModel!;
        return StartOperation(async cancellationToken =>
        {
            _lastUsage = await _client!.ProbeAsync(MockCredential.Value, selectedModel, cancellationToken);
            Notify(nameof(LastUsage)); Notify(nameof(UsageLabel));
            StatusText = "一次模拟调用完成；没有真实积分扣减或返赠。";
        });
    }

    public void CancelOperation() => _operation?.Cancel();

    private bool CanProbe() => IsIdle && _client is not null && Percentage > 0
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
        catch (Exception exception) when (exception is SenseNovaApiException or LocalStorageException)
        { StatusText = exception.Message; }
        catch (ArgumentException) { StatusText = "配置无效，请检查比例、模型和正数运行间隔。"; }
        catch (Exception) { StatusText = "操作未完成，请检查模拟配置与数据权限。"; }
        finally { _operation = null; SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        Notify(nameof(IsBusy)); Notify(nameof(IsIdle));
        SaveConfigurationCommand.Refresh(); ReadModelsCommand.Refresh(); ProbeCommand.Refresh();
        ((CancelOperationCommand)CancelCommand).Refresh();
    }

    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        await _activeTask;
        _lifetime.Dispose();
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
