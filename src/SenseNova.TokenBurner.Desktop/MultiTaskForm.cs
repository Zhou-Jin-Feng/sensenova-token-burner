using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;

namespace SenseNova.TokenBurner.Desktop;

/// <summary>原生控件多任务面板；一个窗口/进程管理任务，无额外UI运行层。</summary>
public sealed class MultiTaskForm : Form
{
    private readonly MultiTaskCoordinator _coordinator;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None, AccessibleName = "API key任务列表"
    };
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Width = 310, MaxLength = 8192, AccessibleName = "添加API key" };
    private readonly TextBox _name = new() { Width = 180, MaxLength = 80, PlaceholderText = "key-1、key-2", AccessibleName = "key备注（可选）" };
    private readonly TextBox _group = new() { Width = 160, MaxLength = 80, AccessibleName = "额度组" };
    private readonly CheckBox _enabled = new() { Text = "启用任务", Checked = true, AutoSize = true };
    private readonly TrackBar _percentage = new() { Minimum = 0, Maximum = 95, Value = 95, TickFrequency = 5, Width = 230, AccessibleName = "目标比例" };
    private readonly CheckBox _custom = new() { Text = "自定义tokens目标", AutoSize = true };
    private readonly NumericUpDown _tokens = new() { Maximum = ConsumptionPolicy.MaximumTargetTokens, ThousandsSeparator = true, Width = 175, Value = 120_000_000, Enabled = false };
    private readonly NumericUpDown _interval = new() { DecimalPlaces = 2, Minimum = 0.01m, Maximum = 8760, Value = 5, Width = 85, AccessibleName = "运行间隔小时" };
    private readonly NumericUpDown _concurrency = new() { Minimum = 1, Maximum = 3, Value = 3, Width = 55, AccessibleName = "全应用并发上限" };
    private readonly Label _risk = new() { AutoSize = true, MaximumSize = new Size(920, 0), ForeColor = Color.FromArgb(139, 75, 10) };
    private readonly Label _detail = new() { AutoSize = true, MaximumSize = new Size(920, 0) };
    private readonly Label _summary = new() { AutoSize = true };
    private readonly Label _notice = new() { AutoSize = true, MaximumSize = new Size(900, 0), AccessibleName = "完成通知" };
    private readonly ProgressBar _overall = new() { Width = 330, Height = 18 };
    private readonly CheckBox _review = new() { Text = "已核查官方用量/额度并接受未知记录；继续只计算已确认进度", AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly NotifyIcon _tray = new() { Icon = SystemIcons.Application, Text = "SenseNova Token Burner", Visible = true };
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly Font _panelFont = new("Microsoft YaHei UI", 10F);
    private bool _loading;
    private bool _refreshing;
    private bool _closing;
    private bool _ready;
    private bool _exiting;
    private Guid? _selected;
    private Guid? _editingId;
    private FormWindowState _restore = FormWindowState.Normal;
    public int TrayNotificationCount { get; private set; }
    public string NoticeText => _notice.Text;

    public MultiTaskForm(MultiTaskCoordinator coordinator)
    {
        _coordinator = coordinator;
        Text = "SenseNova Token Burner" + (coordinator.IsMockMode ? " · 多任务模拟面板" : " · 多任务面板");
        Font = _panelFont;
        ClientSize = new Size(1100, 820); MinimumSize = new Size(920, 740);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(244, 247, 250);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(20) };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        var heading = Vertical();
        heading.Controls.Add(new Label { Text = "SenseNova · 任务面板", AutoSize = true, Font = new Font(_panelFont, FontStyle.Bold) });
        heading.Controls.Add(new Label { Text = SenseNovaDefaults.BaseUrl + "  |  " + SenseNovaDefaults.RebateModel + (coordinator.IsMockMode ? "  |  模拟，无真实扣费" : ""), AutoSize = true });
        var add = Horizontal();
        add.Controls.AddRange([Label("API key"), _key, Label("备注（可选）"), _name, Button("添加并加密保存", AddTaskAsync)]);
        heading.Controls.Add(add);
        var global = Horizontal();
        global.Controls.AddRange([Button("运行全部", RunAllAsync), Button("暂停全部", coordinator.PauseAllAsync),
            Button("继续全部", () => { coordinator.ResumeAll(); return Task.CompletedTask; }),
            Button("停止全部", coordinator.StopAllAsync), Label("全应用请求上限"), _concurrency,
            Button("保存并发", () => coordinator.SetConcurrencyAsync((int)_concurrency.Value))]);
        heading.Controls.Add(global);
        root.Controls.Add(heading, 0, 0);
        foreach (var (key, label) in new[] { ("Name", "任务 / key遮蔽"), ("Group", "额度组"), ("Enabled", "启用"),
            ("Usage", "确认tokens"), ("Target", "目标tokens"), ("State", "状态"), ("Next", "下次计划"), ("Error", "错误 / 待核查") })
            _grid.Columns.Add(key, label);
        _grid.SelectionChanged += (_, _) => SelectRow();
        root.Controls.Add(_grid, 0, 1);
        var edit = Vertical();
        var identity = Horizontal();
        identity.Controls.AddRange([Label("额度组（同账户填相同名称）"), _group, _enabled,
            Label("间隔（小时）"), _interval, Button("保存所选设置", SaveSelectedAsync)]);
        edit.Controls.Add(identity);
        var target = Horizontal();
        target.Controls.AddRange([Label("比例0–95%"), _percentage, _custom, _tokens]);
        foreach (var value in new[] { 25, 50, 75, 95 })
            target.Controls.Add(Button(value + "%", () => { _custom.Checked = false; _percentage.Value = value; return Task.CompletedTask; }));
        edit.Controls.Add(target);
        edit.Controls.Add(_risk); edit.Controls.Add(_detail);
        edit.Controls.Add(_review);
        var single = Horizontal();
        single.Controls.AddRange([Button("运行所选", () => WithSelected(id => coordinator.StartAsync(id)), allowConcurrent: true),
            Button("暂停所选", () => WithSelected(coordinator.PauseAsync)),
            Button("继续剩余目标", ContinueSelectedAsync, allowConcurrent: true), Button("停止所选", () => WithSelected(coordinator.StopAsync)),
            Button("启用所选计划", EnablePlanAsync), Button("关闭所选计划", () => WithSelected(coordinator.DisablePlanAsync))]);
        edit.Controls.Add(single);
        edit.Controls.Add(new Label { Text = "保存/重开不启动；5小时仅为计划间隔，额度为滚动窗口。关闭/最小化进入托盘；退出才停止全部。", AutoSize = true });
        root.Controls.Add(edit, 0, 2);
        var progress = Horizontal(); progress.Controls.AddRange([_overall, _summary]); root.Controls.Add(progress, 0, 3);
        var bottom = Horizontal(); bottom.Controls.AddRange([_notice, Button("退出", () => { RequestExit(); return Task.CompletedTask; })]);
        root.Controls.Add(bottom, 0, 4); Controls.Add(root);
        _percentage.ValueChanged += (_, _) => UpdateRisk();
        _custom.CheckedChanged += (_, _) => { _tokens.Enabled = _custom.Checked; UpdateRisk(); };
        _tokens.ValueChanged += (_, _) => UpdateRisk();
        _coordinator.CompletionNotice += Notify;
        _trayMenu.Items.Add("显示面板", null, (_, _) => Restore());
        _trayMenu.Items.Add("暂停全部", null, async (_, _) => await ExecuteAsync(coordinator.PauseAllAsync));
        _trayMenu.Items.Add("停止全部", null, async (_, _) => await ExecuteAsync(coordinator.StopAllAsync));
        _trayMenu.Items.Add("退出", null, (_, _) => RequestExit());
        _tray.ContextMenuStrip = _trayMenu;
        _tray.DoubleClick += (_, _) => Restore();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && !_exiting) Hide(); else _restore = WindowState; };
        FormClosing += CloseAfterTasks;
        Shown += async (_, _) =>
        {
            await ExecuteAsync(async () => { await coordinator.InitializeAsync(); _concurrency.Value = coordinator.Limiter.Maximum; });
            RefreshRows(); _notice.Text = coordinator.LastNotice; _timer.Start();
        };
        _timer.Tick += async (_, _) =>
        {
            if (_refreshing || _closing) return;
            _refreshing = true;
            try { await coordinator.RefreshAsync(); RefreshRows(); }
            catch (Exception) { _notice.Text = "状态刷新失败，请检查本地记录。"; }
            finally { _refreshing = false; }
        };
        UpdateRisk();
    }
    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(5, 9, 5, 3) };
    private static FlowLayoutPanel Horizontal() => new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 4, 0, 4) };
    private static FlowLayoutPanel Vertical() => new() { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private Button Button(string text, Func<Task> action, bool allowConcurrent = false)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 3, 6, 3), Margin = new Padding(4) };
        button.Click += async (_, _) =>
        {
            if (!allowConcurrent) button.Enabled = false;
            try { await ExecuteAsync(action); }
            finally { if (!button.IsDisposed) button.Enabled = true; }
        };
        return button;
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        try { await action(); }
        catch (LocalStorageException) { _notice.Text = "配置或记录无法读写；原文件保留，请核查。"; }
        catch (ArgumentException exception) { _notice.Text = exception.Message; }
        catch (InvalidOperationException exception) { _notice.Text = exception.Message; }
        catch (OperationCanceledException) { _notice.Text = "操作已取消。"; }
        catch (Exception) { _notice.Text = "操作失败，请检查任务状态。"; }
        if (!IsDisposed) RefreshRows();
    }
    private async Task AddTaskAsync()
    {
        var row = await _coordinator.AddAsync(_key.Text, new() { DisplayName = _name.Text });
        _key.Clear(); _name.Clear(); _selected = row.Configuration.Id;
        _notice.Text = "key已加密保存，尚未运行。";
    }
    private Task WithSelected(Func<Guid, Task> action) => action(_selected ?? throw new InvalidOperationException("请先选择一个任务。"));
    private TaskRuntime Selected => _coordinator.Tasks.Single(row => row.Configuration.Id == _selected);
    private Task SaveSelectedAsync()
    {
        if (_selected is null) throw new InvalidOperationException("请先选择任务。");
        var configuration = Selected.Configuration with
        {
            QuotaGroup = _group.Text.Trim(), Enabled = _enabled.Checked, Percentage = _percentage.Value,
            CustomTargetTokens = _custom.Checked ? (long)_tokens.Value : null, IntervalHours = _interval.Value
        };
        return _coordinator.SaveAsync(configuration);
    }
    private async Task RunAllAsync()
    {
        if (MessageBox.Show(this, _coordinator.RunAllPreview(), "运行全部 · 额度组目标确认", MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning) != DialogResult.OK) return;
        await _coordinator.RunAllAsync();
    }
    private async Task ContinueSelectedAsync()
    {
        if (_selected is null) throw new InvalidOperationException("请先选择任务。");
        if (Selected.Session.CurrentRun.State == RunState.Paused) { _coordinator.Resume(_selected.Value); return; }
        if (Selected.Session.Recovery.State == RecoveryState.NeedsReview && !_review.Checked)
            throw new InvalidOperationException("有未知请求，请先核查官方用量并勾选确认；不会自动重发。");
        var reviewed = _review.Checked;
        _review.Checked = false;
        await _coordinator.StartAsync(_selected.Value, continueRemaining: true, reviewed: reviewed);
    }
    private Task EnablePlanAsync() => WithSelected(id => _coordinator.StartAsync(id, schedule: true));
    private void SelectRow()
    {
        if (_loading || _grid.SelectedRows.Count == 0) return;
        _selected = (Guid)_grid.SelectedRows[0].Tag!;
        LoadSelected();
    }
    private void LoadSelected()
    {
        _editingId = _selected;
        var task = Selected.Configuration;
        _group.Text = task.QuotaGroup; _enabled.Checked = task.Enabled; _percentage.Value = task.Percentage;
        _custom.Checked = task.CustomTargetTokens is not null;
        _tokens.Value = task.CustomTargetTokens ?? task.TargetTokens;
        _interval.Value = Math.Clamp(task.IntervalHours, _interval.Minimum, _interval.Maximum); _review.Checked = false;
        UpdateRisk(); UpdateDetail();
    }
    private void UpdateRisk() => _risk.Text = $"{_percentage.Value}% · 目标{(_custom.Checked ? (long)_tokens.Value : ConsumptionPolicy.GetTargetTokens(_percentage.Value)):N0} tokens。"
        + TargetRisk.Describe(_custom.Checked ? (long)_tokens.Value : ConsumptionPolicy.GetTargetTokens(_percentage.Value));
    private void UpdateDetail()
    {
        if (_selected is null || !_coordinator.Tasks.Any(row => row.Configuration.Id == _selected)) { _detail.Text = "选择任务后设置独立目标与计划。"; return; }
        var row = Selected; var run = row.Snapshot;
        _detail.Text = $"{row.Configuration.DisplayName} · 确认{run.ConfirmedUsage.TotalTokens:N0} / 在途{run.InFlightRequests} / 预留{run.ReservedTokens:N0} / 未知{run.UnknownUsageRequests}。"
            + (row.Session.Recovery.UnresolvedRequests > 0 ? $"上次{row.Session.Recovery.UnresolvedRequests}笔需核查。" : "")
            + (row.Configuration.PlanRequested && row.Session.Schedule.State != ScheduleState.Enabled ? "已保存计划，重开后需手动启用。" : "")
            + row.Message;
    }
    private void RefreshRows()
    {
        if (IsDisposed) return;
        _loading = true;
        try
        {
            while (_grid.Rows.Count < _coordinator.Tasks.Count) _grid.Rows.Add();
            for (var i = 0; i < _coordinator.Tasks.Count; i++)
            {
                var row = _coordinator.Tasks[i]; var run = row.Snapshot; var item = _grid.Rows[i];
                item.Tag = row.Configuration.Id;
                var values = new object[] { row.Configuration.DisplayName + " · ••••••••", row.Configuration.QuotaGroup,
                    row.Configuration.Enabled ? "是" : "否", run.ConfirmedUsage.TotalTokens.ToString("N0"),
                    (run.Parameters?.TargetTokens ?? row.Configuration.TargetTokens).ToString("N0"), row.Status,
                    row.Session.Schedule.NextRunUtc?.ToLocalTime().ToString("MM-dd HH:mm") ?? (row.Configuration.NextRunUtc is not null ? "待手动启用" : "—"),
                    string.IsNullOrEmpty(row.Message) ? run.Failure?.ToString() ?? (row.Session.Recovery.State == RecoveryState.NeedsReview ? "核查未知用量" : "") : row.Message };
                for (var column = 0; column < values.Length; column++)
                    if (!Equals(item.Cells[column].Value, values[column])) item.Cells[column].Value = values[column];
                item.Selected = row.Configuration.Id == _selected;
            }
            _summary.Text = _coordinator.BatchSummary + $"  在途{_coordinator.Limiter.ActiveRequests}/{_coordinator.Limiter.Maximum}";
            _overall.Value = (int)Math.Clamp(_coordinator.OverallProgress * 100, 0, 100);
            UpdateDetail();
        }
        finally { _loading = false; }
        if (_selected is null && _grid.Rows.Count > 0) { _grid.Rows[0].Selected = true; SelectRow(); }
        else if (_selected != _editingId) LoadSelected();
    }
    private void Notify(string message)
    {
        if (IsDisposed || _closing) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => Notify(message))); return; }
        _notice.Text = message;
        if (!Visible)
        {
            TrayNotificationCount++;
            _tray.ShowBalloonTip(5000, "SenseNova · 任务完成", message, ToolTipIcon.Info);
        }
    }
    private void Restore() { Show(); WindowState = _restore; Activate(); }
    public void RequestExit() { if (_exiting) return; _exiting = true; Close(); }
    private async void CloseAfterTasks(object? sender, FormClosingEventArgs args)
    {
        if (_ready) return;
        args.Cancel = true;
        if (!_exiting && args.CloseReason == CloseReason.UserClosing) { Hide(); return; }
        if (_closing) return;
        _closing = true; _exiting = true; _timer.Stop();
        await _coordinator.DisposeAsync(); _ready = true; _tray.Visible = false;
        BeginInvoke(new Action(Close));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coordinator.CompletionNotice -= Notify; _timer.Dispose(); _tray.Visible = false;
            _tray.Dispose(); _trayMenu.Dispose(); _panelFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
