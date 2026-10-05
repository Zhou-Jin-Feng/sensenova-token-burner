using System.ComponentModel;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;

namespace SenseNova.TokenBurner.Desktop;

internal sealed class MainForm : Form
{
    private readonly MainWindowViewModel _viewModel;
    private readonly Font _panelFont;
    private readonly Font _headingFont;
    private readonly Font _sectionFont;
    private readonly ComboBox _models = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460, AccessibleName = "模拟模型" };
    private readonly Label _capability = new() { AutoSize = true };
    private readonly Label _credential = new() { AutoSize = true };
    private readonly TextBox _key = new() { Width = 620, UseSystemPasswordChar = true, MaxLength = 8192, AccessibleName = "SenseNova API key" };
    private readonly CheckBox _remember = new() { Text = "按当前 Windows 用户加密保存 API key", AutoSize = true, Checked = true };
    private readonly TrackBar _percentage = new() { Minimum = 0, Maximum = 95, TickFrequency = 5, Width = 880, AccessibleName = "预计专属积分消耗比例" };
    private readonly Label _percentageLabel = new() { AutoSize = true };
    private readonly Label _target = new() { AutoSize = true };
    private readonly Label _hint = new() { AutoSize = true };
    private readonly TextBox _interval = new() { Width = 140, AccessibleName = "运行间隔（小时）" };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Label _usage = new() { AutoSize = true };
    private readonly List<Control> _inputs = [];
    private readonly Button _read = new() { Text = "读取模拟模型", AutoSize = true };
    private readonly Button _save = new() { Text = "保存模拟配置", AutoSize = true };
    private readonly Button _probe = new() { Text = "模拟调用一次", AutoSize = true };
    private readonly Button _cancel = new() { Text = "取消", AutoSize = true };
    private readonly Button _start = new() { Text = "开始本轮", AutoSize = true };
    private readonly Button _pause = new() { Text = "暂停", AutoSize = true };
    private readonly Button _resume = new() { Text = "继续", AutoSize = true };
    private readonly Button _stop = new() { Text = "停止本轮", AutoSize = true };
    private readonly Button _confirm = new() { Text = "确认运行记录", AutoSize = true };
    private readonly CheckBox _review = new() { Text = "我已核查官方账户用量与额度，接受未知记录并手动开始新轮", AutoSize = true };
    private readonly Label _recovery = new() { AutoSize = true, MaximumSize = new Size(860, 0) };
    private readonly Label _runState = new() { AutoSize = true };
    private readonly Label _runUsage = new() { AutoSize = true };
    private readonly Label _runDetail = new() { AutoSize = true };
    private readonly Label _runFailure = new() { AutoSize = true };
    private bool _updating;
    private bool _closing;
    private bool _readyToClose;

    public MainForm(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        Text = viewModel.IsMockMode ? "SenseNova Token Burner · 模拟面板" : "SenseNova Token Burner";
        ClientSize = new Size(1024, 806);
        MinimumSize = new Size(800, 650);
        StartPosition = FormStartPosition.CenterScreen;
        _panelFont = new Font("Microsoft YaHei UI", 10.5F);
        _headingFont = new Font(_panelFont.FontFamily, 20, FontStyle.Bold);
        _sectionFont = new Font(_panelFont.FontFamily, 12.5F, FontStyle.Bold);
        Font = _panelFont;
        BackColor = Color.FromArgb(243, 247, 248);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(30, 22, 30, 22) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        header.Controls.Add(new Label { Text = "用量与运行管理", Font = _headingFont, AutoSize = true });
        header.Controls.Add(new Label { Text = viewModel.IsMockMode ? "模拟模式 · 内置 HTTP mock · 无真实扣费" : "官方 API · 显式开始运行 · 用量与返赠以账户记录为准", AutoSize = true });
        layout.Controls.Add(header, 0, 0);

        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var service = CreateCard("服务与模型");
        service.Controls.Add(new Label { Text = "官方服务地址：" + viewModel.BaseUrl, AutoSize = true });
        service.Controls.Add(new Label { Text = viewModel.IsMockMode ? "当前使用内置虚拟 key，无真实扣费。" : "填写自己的 SenseNova API key：", AutoSize = true });
        _key.Visible = !viewModel.IsMockMode;
        service.Controls.Add(_key);
        service.Controls.Add(_models);
        _models.AccessibleName = "SenseNova 模型";
        _read.Text = "校验连接并读取模型";
        service.Controls.Add(_read);
        service.Controls.Add(_capability);
        service.Controls.Add(_remember);
        service.Controls.Add(_credential);
        content.Controls.Add(service);

        var budget = CreateCard("预计专属积分消耗比例");
        budget.Controls.Add(_percentageLabel);
        budget.Controls.Add(_percentage);
        var presets = new FlowLayoutPanel { AutoSize = true };
        foreach (var value in new[] { 25, 50, 75, 95 })
        {
            var preset = new Button { Text = value + "%", AutoSize = true };
            preset.Click += (_, _) => _viewModel.Percentage = value;
            presets.Controls.Add(preset);
            _inputs.Add(preset);
        }
        budget.Controls.Add(presets);
        budget.Controls.Add(_target);
        budget.Controls.Add(_hint);
        budget.Controls.Add(new Label { Text = "比例来自经验校准，实际扣减和返赠以官方账户记录为准。", AutoSize = true });
        content.Controls.Add(budget);

        var schedule = CreateCard("运行间隔（小时）");
        schedule.Controls.Add(_interval);
        schedule.Controls.Add(new Label { Text = "默认 5 小时，当前仅保存配置；计划开关将在完整面板中接入。", AutoSize = true });
        schedule.Controls.Add(_save);
        _save.Text = "保存配置与凭据选择";
        content.Controls.Add(schedule);
        var recovery = CreateCard("运行记录与重开保护");
        recovery.Controls.Add(_recovery);
        recovery.Controls.Add(_review);
        recovery.Controls.Add(_confirm);
        content.Controls.Add(recovery);
        layout.Controls.Add(content, 0, 1);

        var footer = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        footer.Controls.Add(_status);
        footer.Controls.Add(_usage);
        footer.Controls.Add(_runState);
        footer.Controls.Add(_runUsage);
        footer.Controls.Add(_runDetail);
        footer.Controls.Add(_runFailure);
        footer.Controls.Add(new Label { Text = "开始后按本轮目标运行；暂停停止新增请求，停止收齐在途结果。关闭窗口当前退出。", AutoSize = true });
        var actions = new FlowLayoutPanel { AutoSize = true };
        var exit = new Button { Text = "退出", AutoSize = true };
        exit.Click += (_, _) => Close();
        _probe.Visible = viewModel.IsMockMode;
        actions.Controls.AddRange([_start, _pause, _resume, _stop, _probe, _cancel, exit]);
        footer.Controls.Add(actions);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        _inputs.AddRange([_key, _models, _remember, _percentage, _interval]);
        _key.TextChanged += (_, _) => { if (!_updating) _viewModel.ApiKey = _key.Text; };
        _start.Click += async (_, _) => await _viewModel.StartRunCommand.ExecuteAsync();
        _pause.Click += (_, _) => _viewModel.PauseRun();
        _resume.Click += (_, _) => _viewModel.ResumeRun();
        _stop.Click += async (_, _) => await _viewModel.StopRunAsync();
        _confirm.Click += async (_, _) => await _viewModel.ConfirmRecoveryCommand.ExecuteAsync();
        _review.CheckedChanged += (_, _) => { if (!_updating) _viewModel.ReviewCompleted = _review.Checked; };
        _read.Click += async (_, _) => await _viewModel.ReadModelsCommand.ExecuteAsync();
        _save.Click += async (_, _) => await _viewModel.SaveConfigurationCommand.ExecuteAsync();
        _probe.Click += async (_, _) => await _viewModel.ProbeCommand.ExecuteAsync();
        _cancel.Click += (_, _) => _viewModel.CancelOperation();
        _percentage.ValueChanged += (_, _) => { if (!_updating) _viewModel.Percentage = _percentage.Value; };
        _interval.TextChanged += (_, _) => { if (!_updating) _viewModel.IntervalHoursText = _interval.Text; };
        _remember.CheckedChanged += (_, _) => { if (!_updating) _viewModel.RememberCredential = _remember.Checked; };
        _models.SelectedIndexChanged += (_, _) => { if (!_updating) _viewModel.SelectedModel = _models.SelectedItem as ModelInfo; };
        _viewModel.PropertyChanged += ViewModelChanged;
        FormClosing += CloseAfterOperations;
        UpdateFromViewModel();
    }

    private FlowLayoutPanel CreateCard(string title)
    {
        var card = new FlowLayoutPanel
        {
            Width = 920, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = Color.White, Padding = new Padding(20), Margin = new Padding(0, 12, 0, 0)
        };
        card.Controls.Add(new Label { Text = title, Font = _sectionFont, AutoSize = true });
        return card;
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args) => UpdateFromViewModel();

    private void UpdateFromViewModel()
    {
        if (IsDisposed) return;
        _updating = true;
        try
        {
            if (_models.Items.Count != _viewModel.Models.Count || !_viewModel.Models.SequenceEqual(_models.Items.Cast<ModelInfo>()))
            {
                _models.DisplayMember = nameof(ModelInfo.DisplayName);
                _models.DataSource = _viewModel.Models.ToArray();
            }
            _models.SelectedItem = _viewModel.SelectedModel;
            if (_key.Text != _viewModel.ApiKey) _key.Text = _viewModel.ApiKey;
            _percentage.Value = _viewModel.Percentage;
            _percentageLabel.Text = _viewModel.PercentageLabel;
            _target.Text = "本轮 token 目标：" + _viewModel.TargetTokensLabel;
            _hint.Text = _viewModel.SelectionHint;
            _interval.Text = _viewModel.IntervalHoursText;
            _remember.Checked = _viewModel.RememberCredential;
            _credential.Text = _viewModel.CredentialStatus;
            _capability.Text = _viewModel.ModelCapabilityLabel;
            _status.Text = _viewModel.StatusText;
            _usage.Text = _viewModel.UsageLabel;
            _runState.Text = _viewModel.RunStateLabel;
            _runUsage.Text = _viewModel.RunUsageLabel;
            _runDetail.Text = _viewModel.RunDetailLabel;
            _runFailure.Text = _viewModel.RunFailureLabel;
            _recovery.Text = _viewModel.RecoveryLabel;
            _review.Checked = _viewModel.ReviewCompleted;
            _review.Enabled = _viewModel.IsIdle && _viewModel.Recovery.State == RecoveryState.NeedsReview;
            _confirm.Enabled = _viewModel.ConfirmRecoveryCommand.CanExecute(null);
            _start.Enabled = _viewModel.StartRunCommand.CanExecute(null);
            _pause.Enabled = _viewModel.CanPause;
            _resume.Enabled = _viewModel.CanResume;
            _stop.Enabled = _viewModel.IsRunActive;
            foreach (var input in _inputs) input.Enabled = _viewModel.IsIdle;
            _read.Enabled = _viewModel.ReadModelsCommand.CanExecute(null);
            _save.Enabled = _viewModel.SaveConfigurationCommand.CanExecute(null);
            _probe.Enabled = _viewModel.ProbeCommand.CanExecute(null);
            _cancel.Enabled = _viewModel.IsBusy && !_viewModel.IsRunActive;
        }
        finally { _updating = false; }
    }

    private async void CloseAfterOperations(object? sender, FormClosingEventArgs args)
    {
        if (_readyToClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        await _viewModel.DisposeAsync();
        _readyToClose = true;
        // 清理可能同步完成，最终 Close 必须排到当前 FormClosing 事件返回后。
        BeginInvoke(new Action(Close));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _viewModel.PropertyChanged -= ViewModelChanged;
        base.Dispose(disposing);
        if (disposing)
        {
            _headingFont.Dispose();
            _sectionFont.Dispose();
            _panelFont.Dispose();
        }
    }
}
