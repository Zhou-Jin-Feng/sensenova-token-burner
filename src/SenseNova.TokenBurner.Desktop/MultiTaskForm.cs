using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.Ui;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Desktop;

/// <summary>需要用户确认的交互入口；默认弹出主题化对话框，测试可替换为无界面实现。</summary>
internal sealed class PanelPrompts
{
    public Func<IWin32Window, RunPreview, bool> ConfirmRun { get; set; } = (owner, preview) =>
    {
        using var dialog = new ConfirmDialog(ConfirmDialog.ForRun(preview));
        return dialog.ShowDialog(owner) == DialogResult.OK;
    };
    public Func<IWin32Window, string, bool> ConfirmReset { get; set; } = (owner, scope) =>
    {
        using var dialog = new ConfirmDialog(new("重置进度", $"重置{scope}的本轮进度？", null,
            [new("范围", scope), new("会发生", "先结束当前运行和定时，把本轮用量归档到运行记录，客户端进度归零。"),
             new("不会发生", "不发请求、不自动运行、不修改 key 和目标设置。")],
            null, [], [(NoticeLevel.Warning, "重置不会退还或刷新官方额度，也不代表滚动额度已经恢复；下一次运行前请先核查剩余额度。有未核查的未知请求时不会重置。")],
            "确认重置", true));
        return dialog.ShowDialog(owner) == DialogResult.OK;
    };
    public Func<IWin32Window, string, bool> ConfirmDelete { get; set; } = (owner, name) =>
    {
        using var dialog = new ConfirmDialog(new("删除任务", $"删除“{name}”？", null,
            [new("会删除", "这个 key 的加密凭据、运行记录、历史和定时设置。"), new("不影响", "其他 key 和官方账户。")],
            null, [], [(NoticeLevel.Warning, "删除后无法恢复；需要时可以重新添加同一个 key。")], "删除任务", true));
        return dialog.ShowDialog(owner) == DialogResult.OK;
    };
    public Func<IWin32Window, string, DirtyChoice> ResolveDirty { get; set; } = ChoiceDialog.AskDirty;
    public Func<IWin32Window, Func<AddKeyRequest, Task<string?>>, Task> AddKey { get; set; } = (owner, submit) =>
    {
        using var dialog = new AddKeyDialog(submit);
        dialog.ShowDialog(owner);
        return Task.CompletedTask;
    };
    /// <summary>托盘气泡；为 null 时使用真实系统通知。</summary>
    public Action<string, string, NoticeLevel>? Balloon { get; set; }
}

/// <summary>多任务主面板：左侧 key 列表，右侧燃烧仪表与设置；一个进程、无额外 UI 运行层。</summary>
public sealed class MultiTaskForm : Form
{
    private const int MaximumActivity = 100;
    private readonly MultiTaskCoordinator _coordinator;
    private readonly JsonUiPreferencesStore _preferences;
    // 视图在构造函数里、主题按当前 DPI 初始化之后再创建，确保字体与缩放一致。
    private readonly HeaderBar _header;
    private readonly OverviewStrip _overview;
    private readonly RosterPane _roster;
    private readonly DetailPane _detail;
    private readonly StatusLine _status;
    private readonly NotifyIcon _tray = new() { Text = "SenseNova Token Burner", Visible = true };
    private readonly ContextMenuStrip _trayMenu;
    private readonly Icon _windowIcon;
    private readonly Icon _trayIdle;
    private readonly Icon _trayBurning;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _pulseTimer = new() { Interval = 80 };
    private readonly HashSet<Guid> _checked = [];
    private readonly HashSet<Button> _busy = [];
    private readonly List<PanelNotice> _activity = [];
    private readonly BurnTracker _burn = new();
    private PanelNotice _notice = new("", NoticeLevel.Info);
    private Guid? _selected;
    private Guid? _liveTaskId;
    private bool _initialChecked;
    private bool _initFailed;
    private bool _refreshing;
    private bool _closing;
    private bool _ready;
    private bool _exiting;
    private bool _hiddenHintShown;
    private int _unseen;
    private double _phase;
    private FormWindowState _restore = FormWindowState.Normal;

    public int TrayNotificationCount { get; private set; }
    public string NoticeText => _notice.Text;
    internal PanelPrompts Prompts { get; } = new();
    /// <summary>测试用：不抢前台焦点并放到屏幕外，避免打扰正在使用电脑的人。</summary>
    internal bool ShowInBackground { get; init; }
    internal RosterPane Roster => _roster;
    internal DetailPane Detail => _detail;
    internal OverviewStrip Overview => _overview;
    internal Guid? SelectedTaskId => _selected;
    internal IReadOnlyCollection<Guid> CheckedTaskIds => _checked;

    public MultiTaskForm(MultiTaskCoordinator coordinator, Func<string, bool>? confirmReset = null, Func<string, bool>? confirmRun = null)
    {
        _coordinator = coordinator;
        _preferences = new JsonUiPreferencesStore(coordinator.Workspace.Storage.RootDirectory);
        Theme.Initialize(DeviceDpi, ParseTheme(_preferences.Load().Theme));
        if (confirmRun is not null) Prompts.ConfirmRun = (_, preview) => confirmRun(preview.Text);
        if (confirmReset is not null) Prompts.ConfirmReset = (_, scope) => confirmReset(scope);

        Text = "SenseNova Token Burner" + (coordinator.IsMockMode ? "（模拟环境）" : "");
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Font = Theme.Fonts.Body;
        BackColor = Theme.Current.Canvas;
        StartPosition = FormStartPosition.CenterScreen;
        using (var stream = new MemoryStream(BrandMark.IconBytes(true, 16, 20, 24, 32, 40, 48, 64, 256))) _windowIcon = new Icon(stream);
        var trayIcon = SystemInformation.SmallIconSize.Width;
        _trayIdle = BrandMark.CreateIcon(trayIcon, burning: false);
        _trayBurning = BrandMark.CreateIcon(trayIcon, burning: true);
        Icon = _windowIcon;
        _tray.Icon = _trayIdle;

        _overview = new OverviewStrip();
        _roster = new RosterPane();
        _detail = new DetailPane();
        _status = new StatusLine();
        _header = new HeaderBar(coordinator.IsMockMode);
        Controls.AddRange([_header, _overview, _roster, _detail, _status]);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        Size = new Size(Math.Min(Theme.S(1260), (int)(area.Width * 0.94)), Math.Min(Theme.S(860), (int)(area.Height * 0.94)));
        MinimumSize = new Size(Math.Min(Theme.S(980), area.Width), Math.Min(Theme.S(660), area.Height));

        _header.SettingsButton.Click += async (_, _) => await ExecuteAsync(ShowSettingsAsync);
        _header.ThemeButton.Click += (_, _) => ApplyThemeMode(Theme.Current.IsDark ? ThemeMode.Light : ThemeMode.Dark, save: true);
        Bind(_roster.AddButton, AddKeyAsync);
        Bind(_roster.EmptyAdd, AddKeyAsync);
        Bind(_roster.BatchRun, BatchRunAsync, allowConcurrent: true);
        Bind(_roster.BatchPause, BatchPauseAsync);
        Bind(_roster.BatchReset, BatchResetAsync);
        _roster.SelectAllClicked += ToggleSelectAll;
        _roster.List.SelectRequested += id => _ = ExecuteAsync(() => RequestSelectAsync(id));
        _roster.List.CheckToggled += ToggleChecked;
        _roster.List.MenuRequested += (id, point) => ShowTaskMenu(id, point);
        Bind(_detail.Run, RunSelectedAsync, allowConcurrent: true);
        Bind(_detail.Pause, PauseSelectedAsync);
        Bind(_detail.Reset, ResetSelectedAsync);
        Bind(_detail.Validate, ValidateSelectedAsync);
        _detail.More.Click += (_, _) =>
        {
            if (_selected is { } id) ShowTaskMenu(id, _detail.More.PointToScreen(new Point(0, _detail.More.Height + Theme.S(4))));
        };
        Bind(_detail.Target.Save, SaveSettingsAsync);
        _detail.Target.DirtyChanged += () => UpdateView();
        Bind(_detail.Schedule.Enable, EnablePlanAsync, allowConcurrent: true);
        Bind(_detail.Schedule.Disable, DisablePlanAsync);
        Bind(_detail.Schedule.SaveWeeklySettings, SaveWeeklySettingsAsync);
        _detail.Schedule.Interval.ValueChanged += (_, _) => UpdateView();
        _detail.Schedule.Interval.Edited += (_, _) => UpdateView();
        _detail.Schedule.WeeklyDirtyChanged += () => UpdateView();
        _detail.PageShown += _ => UpdateView();
        _status.ActivityButton.Click += (_, _) => ShowActivity();

        _trayMenu = MenuRenderer.Create();
        _trayMenu.Items.Add(MenuRenderer.Item("显示面板", Restore));
        _trayMenu.Items.Add(MenuRenderer.Item("暂停全部任务", () => _ = ExecuteAsync(PauseEverythingAsync)));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(MenuRenderer.Item("退出", RequestExit));
        _tray.ContextMenuStrip = _trayMenu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Restore(); };

        _coordinator.Noticed += OnCoordinatorNotice;
        Theme.Changed += OnThemeChanged;
        FormClosing += CloseAfterTasks;
        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized) _restore = WindowState;
            UpdatePulse();
        };
        VisibleChanged += (_, _) => UpdatePulse();
        Shown += async (_, _) =>
        {
            try { await _coordinator.InitializeAsync(); }
            catch (Exception exception) when (exception is LocalStorageException or InvalidOperationException or IOException)
            { _initFailed = true; }
            UpdateView();
            // 配置不可读时保持错误状态并禁止添加，避免用户误以为任务丢失而清空数据目录。
            if (_initFailed)
                SetNotice($"任务配置不可读，原文件已保留，没有启动任何任务。请检查数据目录：{_coordinator.Workspace.Storage.RootDirectory}", NoticeLevel.Error);
            else if (_coordinator.Tasks.Count == 0) SetNotice("就绪：先添加一个 API key。保存不会自动开始运行。", NoticeLevel.Info);
            else if (_coordinator.LastNoticeLevel == NoticeLevel.Warning) SetNotice(_coordinator.LastNotice, NoticeLevel.Warning);
            else SetNotice("就绪：重开不会自动运行。选择任务后点运行/继续。", NoticeLevel.Info);
            _timer.Start();
        };
        _timer.Tick += async (_, _) =>
        {
            if (_refreshing || _closing) return;
            _refreshing = true;
            try { await _coordinator.RefreshAsync(); UpdateView(); }
            catch (Exception) { SetNotice("状态刷新失败，请检查本地记录。", NoticeLevel.Error); }
            finally { _refreshing = false; }
        };
        _pulseTimer.Tick += (_, _) =>
        {
            _phase = (_phase + Math.PI * 2 * _pulseTimer.Interval / 2400d) % (Math.PI * 2);
            var pulse = (float)(0.5 + 0.5 * Math.Sin(_phase));
            _roster.List.Pulse = pulse;
            _overview.Pulse = pulse;
            _detail.Overview.Gauge.Pulse = pulse;
        };
    }

    protected override bool ShowWithoutActivation => ShowInBackground;

    /// <summary>面板下方的柔和投影（浅色主题更明显），让三块面板浮在画布上。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_header is null) return;
        var g = e.Graphics;
        Draw.Smooth(g);
        var dark = Theme.Current.IsDark;
        foreach (var panel in new Control[] { _overview, _roster, _detail })
        {
            var bounds = panel.Bounds;
            for (var layer = 1; layer <= 6; layer++)
            {
                var alpha = (dark ? 26 : 11) * (7 - layer) / 6;
                var spread = Theme.Sf(layer);
                var rect = new RectangleF(bounds.X - spread * 0.5f, bounds.Y + Theme.Sf(2) + spread * 0.35f, bounds.Width + spread, bounds.Height + spread * 0.6f);
                using var brush = new SolidBrush(Color.FromArgb(alpha, dark ? Color.Black : Color.FromArgb(16, 48, 44)));
                using var path = Draw.Round(rect, Theme.Sf(12) + spread * 0.5f);
                g.FillPath(brush, path);
            }
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (ShowInBackground) { StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowChrome(this);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_header is null) return;
        var pad = Theme.S(20);
        var width = ClientSize.Width - pad * 2;
        _header.SetBounds(pad, Theme.S(8), width, Theme.S(54));
        _overview.SetBounds(pad, _header.Bottom + Theme.S(6), width, Theme.S(86));
        _status.SetBounds(pad, ClientSize.Height - Theme.S(42), width, Theme.S(34));
        var top = _overview.Bottom + Theme.S(14);
        var height = Math.Max(Theme.S(200), _status.Top - Theme.S(8) - top);
        var rosterWidth = Math.Clamp((int)(width * 0.35), Theme.S(340), Theme.S(440));
        _roster.SetBounds(pad, top, rosterWidth, height);
        _detail.SetBounds(_roster.Right + Theme.S(14), top, width - rosterWidth - Theme.S(14), height);
        Invalidate();
    }

    private static ThemeMode ParseTheme(string value) => value switch
    {
        UiPreferences.ThemeLight => ThemeMode.Light, UiPreferences.ThemeDark => ThemeMode.Dark, _ => ThemeMode.System
    };

    private void ApplyThemeMode(ThemeMode mode, bool save)
    {
        Theme.SetMode(mode);
        if (save)
            _ = _preferences.SaveAsync(new UiPreferences
            {
                Theme = mode switch { ThemeMode.Light => UiPreferences.ThemeLight, ThemeMode.Dark => UiPreferences.ThemeDark, _ => UiPreferences.ThemeSystem }
            });
    }

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(OnThemeChanged)); return; }
        BackColor = Theme.Current.Canvas;
        Theme.ApplyWindowChrome(this);
        _trayMenu.Invalidate();
        Invalidate(true);
    }

    // ---- 视图刷新 ----

    private TaskRuntime? Find(Guid id) => _coordinator.Tasks.FirstOrDefault(row => row.Configuration.Id == id);
    private TaskRuntime? SelectedRow => _selected is { } id ? Find(id) : null;
    private HashSet<Guid> CheckedIds() => _checked.Where(id => Find(id) is { Configuration.Enabled: true }).ToHashSet();

    private void UpdateView()
    {
        if (IsDisposed || _status is null) return;
        var tasks = _coordinator.Tasks;
        if (!_initialChecked && tasks.Count > 0)
        {
            // 只在首次载入时默认勾选全部已启用任务；之后完全尊重用户的勾选。
            foreach (var row in tasks.Where(row => row.Configuration.Enabled)) _checked.Add(row.Configuration.Id);
            _initialChecked = true;
        }
        _checked.RemoveWhere(id => Find(id) is not { Configuration.Enabled: true });
        if (_selected is { } current && Find(current) is null) _selected = null;
        if (_selected is null && tasks.Count > 0) Select(tasks[0].Configuration.Id, refresh: false);

        var now = DateTimeOffset.Now;
        var rows = tasks.Select(TaskRowView.From).ToList();
        foreach (var row in tasks)
            _burn.Observe(row.Configuration.Id, row.Snapshot.ConfirmedUsage.TotalTokens, row.StatusKind == TaskStatusKind.Burning, now);
        _roster.List.SetData(rows, _selected, id => _checked.Contains(id));
        var checkedCount = CheckedIds().Count;
        _roster.SetSummary(tasks.Count, tasks.Count(row => row.Configuration.Enabled), checkedCount,
            _coordinator.BatchActive ? $"批量运行 {_coordinator.BatchTaskCount} 个，{Format.Percent(_coordinator.OverallProgress)}" : null);
        foreach (var button in new[] { _roster.BatchRun, _roster.BatchPause, _roster.BatchReset })
            if (_busy.Contains(button)) button.Enabled = false;
        _roster.Failed = _initFailed;
        _roster.AddButton.Enabled = _roster.EmptyAdd.Enabled = !_initFailed && !_busy.Contains(_roster.AddButton);

        var burning = tasks.Count(row => row.StatusKind == TaskStatusKind.Burning);
        var next = tasks.Where(row => row.Session.Schedule.State == ScheduleState.Enabled && row.Session.Schedule.NextRunUtc is not null)
            .OrderBy(row => row.Session.Schedule.NextRunUtc).FirstOrDefault();
        _overview.SetData(new(
            tasks.Sum(row => row.Snapshot.ConfirmedUsage.TotalTokens),
            tasks.Where(row => row.Configuration.Enabled || row.Snapshot.Parameters is not null)
                .Sum(row => row.Snapshot.Parameters?.TargetTokens ?? row.Configuration.TargetTokens),
            burning, tasks.Count(row => row.StatusKind == TaskStatusKind.Paused), tasks.Count(row => row.StatusKind == TaskStatusKind.Warning),
            tasks.Count, _coordinator.Limiter.ActiveRequests, _coordinator.Limiter.Maximum, PerKeyLimit(),
            next?.Session.Schedule.NextRunUtc is { } nextRun ? Format.When(nextRun) : null, next?.Configuration.DisplayName,
            _coordinator.BatchActive ? $"批量运行 {Format.Percent(_coordinator.OverallProgress)}" : null));
        _header.Burning = burning > 0;
        var trayIcon = burning > 0 ? _trayBurning : _trayIdle;
        if (_tray.Icon != trayIcon) _tray.Icon = trayIcon;
        var trayText = burning > 0 ? $"Token Burner：{burning} 个任务运行中" : "SenseNova Token Burner";
        if (_tray.Text != trayText) _tray.Text = trayText;

        if (SelectedRow is { } selected) UpdateDetail(selected, now);
        else _detail.SetEmpty(true, noTasks: tasks.Count == 0, failed: _initFailed);

        if (_liveTaskId is { } liveId && Find(liveId) is { } live)
            ShowLive($"{live.Configuration.DisplayName}：{live.Status}，已确认 {Format.Exact(live.Snapshot.ConfirmedUsage.TotalTokens)} tokens。",
                live.StatusKind switch { TaskStatusKind.Success => NoticeLevel.Success, TaskStatusKind.Warning => NoticeLevel.Warning, TaskStatusKind.Danger => NoticeLevel.Error, _ => NoticeLevel.Info });
        UpdatePulse();
    }

    private int PerKeyLimit()
    {
        try { return _coordinator.PerKeyConcurrency; }
        catch (InvalidOperationException) { return RequestBaseline.DefaultConcurrency; }
    }

    private void UpdateDetail(TaskRuntime row, DateTimeOffset now)
    {
        var configuration = row.Configuration;
        var run = row.Snapshot;
        var id = configuration.Id;
        _detail.SetEmpty(false);
        var kind = !configuration.Enabled && !row.Active ? TaskStatusKind.Idle : row.StatusKind;
        var status = !configuration.Enabled && !row.Active ? "已停用" : row.ShortStatus;
        var scheduleOn = row.Session.Schedule.State == ScheduleState.Enabled;
        var hint = scheduleOn && row.Session.Schedule.NextRunUtc is { } nextRun ? $"定时已启用，下次 {Format.When(nextRun)}"
            : configuration.PlanRequested && !scheduleOn ? "定时需要重新启用" : "";
        _detail.SetHeader(configuration.DisplayName, configuration.QuotaGroup, status, kind, hint);

        var corrupt = row.Session.Recovery.State is RecoveryState.Corrupt or RecoveryState.StorageFailed;
        // 只看本进程引擎的实时状态：异常退出后记录里可能仍是“运行中”，重开后必须能继续剩余目标。
        var running = row.Preparing || row.Resetting || row.Session.CurrentRun.State is RunState.Running or RunState.Stopping;
        SetEnabled(_detail.Run, configuration.Enabled && !corrupt && !running && !scheduleOn);
        SetEnabled(_detail.Pause, row.Validating || row.Preparing || row.Session.CurrentRun.State == RunState.Running || scheduleOn);
        SetEnabled(_detail.Reset, !row.Resetting && (run.Parameters is not null || row.Active || scheduleOn
            || row.Session.Recovery.Document?.CurrentRun is not null));
        SetEnabled(_detail.Validate, configuration.CredentialSaved && !row.Active && !scheduleOn && !row.Validating);

        // 概览：仪表、读数、提示、速率图
        var target = run.Parameters?.TargetTokens ?? configuration.TargetTokens;
        var confirmed = run.ConfirmedUsage.TotalTokens;
        var remaining = Math.Max(0, target - confirmed);
        var fraction = target <= 0 ? 0 : (double)confirmed / target;
        var reserved = target <= 0 ? 0 : (double)run.ReservedTokens / target;
        var reference = ConsumptionPolicy.GetTargetTokens(95);
        _detail.Overview.Gauge.Set(fraction, reserved, target > reference ? (double)reference / target : null, kind,
            Format.Percent(fraction), "已确认", $"{Format.Compact(confirmed)} / {Format.Compact(target)}");
        var burning = row.StatusKind == TaskStatusKind.Burning;
        var rate = _burn.Rate(id, now);
        DateTimeOffset? eta = burning && rate > 0 && remaining > 0 ? now.AddMinutes(remaining / rate) : null;
        _detail.Overview.Readouts.SetItems([
            new("已确认", Format.Exact(confirmed), "tokens"),
            new("本轮目标", Format.Exact(target), run.Parameters is not null ? "本轮固定" : configuration.CustomTargetTokens is null ? $"{configuration.Percentage}% 档" : "自定义"),
            new("剩余", Format.Exact(remaining), "tokens"),
            new("确认速率", rate > 0 ? Format.Rate(rate) : "—", rate > 0 ? "近 5 分钟" : null, burning && rate > 0 ? TaskStatusKind.Burning : null),
            new("预计完成", eta is { } finish ? Format.When(finish) : "—", eta is { } left ? "约 " + Format.Duration(left - now) : null),
            new("在途请求", $"{run.InFlightRequests} 笔", run.ReservedTokens > 0 ? "预留 " + Format.Compact(run.ReservedTokens) : null),
            new("已完成请求", $"{run.CompletedRequests} 笔"),
            new("未知用量", $"{run.UnknownUsageRequests} 笔", null, run.UnknownUsageRequests > 0 ? TaskStatusKind.Warning : null)
        ]);
        var weekly = _coordinator.GetWeeklyQuotaStatus(row, now);
        _detail.Overview.WeeklyQuota.SetData(weekly, now);

        var overview = _detail.Overview;
        var needsReview = row.Session.Recovery.State == RecoveryState.NeedsReview;
        overview.Review.Visible = needsReview;
        if (needsReview)
            overview.Review.Text = $"上次运行有 {row.Session.Recovery.UnresolvedRequests} 笔请求没有拿到用量结果。请先到 SenseNova 控制台核查实际用量和剩余额度；"
                + "勾选确认后，继续或重置只按已确认的进度计算，这些请求不会被重发。";
        overview.Message.Visible = !string.IsNullOrEmpty(row.Message);
        overview.Message.Text = row.Message;
        overview.Message.Level = row.MessageLevel;
        var note = !configuration.Enabled ? "此 key 已停用：不参与批量运行，也不能单独运行。可在更多菜单或目标设置中重新启用。"
            : configuration.PlanRequested && !scheduleOn ? "上次启用的定时在重开应用后不会自动恢复，需要时请到定时页重新启用。"
            : corrupt ? "本地运行记录异常，已禁止运行；原文件已保留，请核查数据目录。" : null;
        overview.Note.Visible = note is not null;
        overview.Note.Text = note ?? "";
        overview.Note.Level = corrupt ? NoticeLevel.Error : NoticeLevel.Info;
        overview.Chart.SetData(_burn.PerMinute(id, now), burning);

        // 目标设置：未修改时跟随已保存配置
        var page = _detail.Target;
        if (page.TaskId != id || (!page.IsDirty && page.BaselineDiffers(configuration))) page.Load(configuration);

        // 定时
        var schedule = _detail.Schedule;
        if (!schedule.IsWeeklyDirty) schedule.LoadWeekly(configuration, weekly, now);
        schedule.State.Level = scheduleOn ? NoticeLevel.Success : configuration.PlanRequested ? NoticeLevel.Warning : NoticeLevel.Info;
        schedule.State.Title = scheduleOn ? "定时已启用" : configuration.PlanRequested ? "定时需要重新启用" : "定时未启用";
        schedule.State.Text = scheduleOn
            ? $"每 {Format.Hours(row.Session.Schedule.Configuration?.IntervalHours ?? configuration.IntervalHours)} 小时自动开始新一轮"
                + (row.Session.Schedule.NextRunUtc is { } upcoming ? $"，下次 {Format.When(upcoming)}" : "")
                + "。之后的自动轮次不再逐次确认；要修改间隔或目标，请先关闭定时。"
            : running ? "这个任务正在运行。等这一轮结束或暂停后，再启用定时。"
            : !configuration.Enabled ? "此 key 已停用，启用后才能设置定时。"
            : configuration.PlanRequested ? "重开应用后定时不会自动恢复。确认额度后点“启用定时”，会立即开始第一轮。"
            : "启用后会立即开始第一轮，之后按间隔自动开始新一轮。";
        schedule.Interval.Enabled = !scheduleOn;
        SetEnabled(schedule.Enable, configuration.Enabled && !corrupt && !scheduleOn && !running);
        SetEnabled(schedule.Disable, scheduleOn);

        if (_detail.Tabs.SelectedIndex == 3) _detail.History.SetItems(HistoryPage.From(row));
        _detail.RefreshPage();
    }

    private void SetEnabled(Button button, bool enabled) => button.Enabled = enabled && !_busy.Contains(button);

    private void UpdatePulse()
    {
        var animate = Visible && WindowState != FormWindowState.Minimized && !_closing
            && _coordinator.Tasks.Any(row => row.StatusKind == TaskStatusKind.Burning);
        if (animate && !_pulseTimer.Enabled) _pulseTimer.Start();
        else if (!animate && _pulseTimer.Enabled)
        {
            _pulseTimer.Stop();
            _roster.List.Pulse = 0; _overview.Pulse = 0; _detail.Overview.Gauge.Pulse = 0;
        }
    }

    // ---- 通知 ----

    private void SetNotice(string text, NoticeLevel level, Guid? taskId = null)
    {
        _liveTaskId = null;
        _notice = new PanelNotice(text, level, taskId);
        _status.SetNotice(_notice);
        _activity.Add(_notice);
        if (_activity.Count > MaximumActivity) _activity.RemoveAt(0);
        _unseen++;
        _status.SetActivityCount(_unseen);
    }

    /// <summary>正在执行的任务的实时状态，只显示不记入动态。</summary>
    private void ShowLive(string text, NoticeLevel level)
    {
        if (_notice.Text == text && _notice.Level == level) return;
        _notice = new PanelNotice(text, level, _liveTaskId);
        _status.SetNotice(_notice);
    }

    private void OnCoordinatorNotice(PanelNotice notice)
    {
        if (IsDisposed || _closing) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => OnCoordinatorNotice(notice))); return; }
        SetNotice(notice.Text, notice.Level, notice.TaskId);
        UpdateView();
        if (!Visible || WindowState == FormWindowState.Minimized)
        {
            TrayNotificationCount++;
            var title = notice.Level switch
            {
                NoticeLevel.Success => "任务完成", NoticeLevel.Warning => "需要处理", NoticeLevel.Error => "出现错误", _ => "SenseNova Token Burner"
            };
            ShowBalloon(title, notice.Text, notice.Level);
        }
    }

    private void ShowBalloon(string title, string text, NoticeLevel level)
    {
        if (Prompts.Balloon is { } custom) { custom(title, text, level); return; }
        _tray.ShowBalloonTip(5000, title, text, level switch
        {
            NoticeLevel.Error => ToolTipIcon.Error, NoticeLevel.Warning => ToolTipIcon.Warning, _ => ToolTipIcon.Info
        });
    }

    private void ShowActivity()
    {
        var list = new ActivityList { Size = new Size(Theme.S(460), Theme.S(380)) };
        list.SetItems(_activity.AsEnumerable().Reverse().ToList());
        var host = new ToolStripControlHost(list) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = list.Size };
        var dropDown = new ToolStripDropDown { Padding = new Padding(1), Renderer = new MenuRenderer() };
        dropDown.Items.Add(host);
        dropDown.Closed += (_, _) => BeginInvoke(new Action(dropDown.Dispose));
        _unseen = 0;
        _status.SetActivityCount(0);
        var button = _status.ActivityButton;
        dropDown.Show(button, new Point(button.Width - list.Width - 2, -list.Height - Theme.S(8)));
    }

    // ---- 操作入口 ----

    private void Bind(Button button, Func<Task> action, bool allowConcurrent = false)
    {
        button.Click += async (_, _) =>
        {
            if (!allowConcurrent) { _busy.Add(button); button.Enabled = false; }
            try { await ExecuteAsync(action); }
            finally
            {
                if (!allowConcurrent) _busy.Remove(button);
                if (!IsDisposed && !button.IsDisposed) { button.Enabled = true; UpdateView(); }
            }
        };
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try { await action(); }
        catch (LocalStorageException) { SetNotice("配置或记录无法读写；原文件已保留，请核查数据目录。", NoticeLevel.Error); }
        catch (ArgumentException exception) { SetNotice(exception.Message, NoticeLevel.Warning); }
        catch (InvalidOperationException exception) { SetNotice(exception.Message, NoticeLevel.Warning); }
        catch (OperationCanceledException) { SetNotice("操作已取消。", NoticeLevel.Info); }
        catch (Exception) { SetNotice("操作失败，请检查任务状态。", NoticeLevel.Error); }
        if (!IsDisposed) UpdateView();
    }

    internal Task SelectTaskAsync(Guid id) => RequestSelectAsync(id);

    private async Task RequestSelectAsync(Guid id)
    {
        if (id == _selected || Find(id) is null) return;
        if (!await ResolveDirtyAsync()) return;
        Select(id, refresh: true);
    }

    private void Select(Guid id, bool refresh)
    {
        _selected = id;
        _detail.Overview.ReviewConfirm.Checked = false;
        if (Find(id) is { } row)
        {
            _detail.Target.Load(row.Configuration);
            _detail.Schedule.Interval.Value = Math.Clamp(row.Configuration.IntervalHours, _detail.Schedule.Interval.Minimum, _detail.Schedule.Interval.Maximum);
            _detail.Schedule.LoadWeekly(row.Configuration, _coordinator.GetWeeklyQuotaStatus(row, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        }
        if (!refresh) return;
        UpdateView();
        _roster.List.EnsureVisible(id);
    }

    /// <summary>目标设置或周额度设置有未保存修改时，先让用户选择保存、放弃或取消；取消返回 false。</summary>
    private async Task<bool> ResolveDirtyAsync()
    {
        var page = _detail.Target;
        if (page.IsDirty && SelectedRow is { } row)
        {
            switch (Prompts.ResolveDirty(this, row.Configuration.DisplayName))
            {
                case DirtyChoice.Save: if (!await SaveSettingsCoreAsync(row)) return false; break;
                case DirtyChoice.Discard: page.Load(row.Configuration); break;
                default: SetNotice("已取消，未保存的修改仍保留在目标设置页。", NoticeLevel.Info); return false;
            }
        }
        var schedule = _detail.Schedule;
        if (schedule.IsWeeklyDirty && SelectedRow is { } sRow)
        {
            switch (Prompts.ResolveDirty(this, sRow.Configuration.DisplayName + "的周额度设置"))
            {
                case DirtyChoice.Save: await SaveWeeklySettingsAsync(); break;
                case DirtyChoice.Discard: schedule.LoadWeekly(sRow.Configuration, _coordinator.GetWeeklyQuotaStatus(sRow, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow); break;
                default: SetNotice("已取消，未保存的修改仍保留在定时页。", NoticeLevel.Info); return false;
            }
        }
        return true;
    }

    private async Task SaveWeeklySettingsAsync()
    {
        var row = RequireSelected();
        var schedule = _detail.Schedule;
        var day = schedule.WeeklyResetDay.SelectedIndex switch
        {
            0 => DayOfWeek.Monday,
            1 => DayOfWeek.Tuesday,
            2 => DayOfWeek.Wednesday,
            3 => DayOfWeek.Thursday,
            4 => DayOfWeek.Friday,
            5 => DayOfWeek.Saturday,
            _ => DayOfWeek.Sunday
        };
        var time = TimeSpan.FromHours((double)schedule.WeeklyResetHour.Value);
        var quotaPoints = (long)schedule.WeeklyQuotaLimit.Value;
        var autoStop = schedule.WeeklyAutoStop.Checked;
        var adjustment = (long)schedule.WeeklyManualAdjustment.Value;

        try
        {
            await _coordinator.UpdateWeeklyQuotaSettingsAsync(row.Configuration.Id, day, time, quotaPoints, autoStop, adjustment);
            var group = string.IsNullOrEmpty(row.Configuration.QuotaGroup) ? "" : $"（额度组“{row.Configuration.QuotaGroup}”）";
            SetNotice($"{row.Configuration.DisplayName}：周额度设置已保存{group}。", NoticeLevel.Success, row.Configuration.Id);
        }
        catch (Exception exception) when (exception is ArgumentException or LocalStorageException)
        {
            SetNotice($"周额度设置保存失败：{exception.Message}", NoticeLevel.Warning, row.Configuration.Id);
        }
        UpdateView();
    }

    private async Task<bool> SaveSettingsCoreAsync(TaskRuntime row)
    {
        var updated = _detail.Target.Apply(row.Configuration, commit: true);
        if (string.IsNullOrWhiteSpace(updated.DisplayName))
        {
            _detail.ShowPage(1);
            SetNotice("名称不能为空。", NoticeLevel.Warning);
            return false;
        }
        // 定时启用后，之后的自动轮次不再弹确认；改目标必须先关闭定时，再在重新启用时确认新目标。
        if (row.Session.Schedule.State == ScheduleState.Enabled && updated.Enabled && updated.TargetTokens != row.Configuration.TargetTokens)
        {
            _detail.ShowPage(1);
            SetNotice($"{row.Configuration.DisplayName}：定时已启用，修改目标前请先在定时页关闭定时；重新启用时会再确认新目标。", NoticeLevel.Warning, row.Configuration.Id);
            return false;
        }
        try
        {
            await _coordinator.SaveAsync(updated);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or LocalStorageException)
        {
            _detail.ShowPage(1);
            SetNotice($"设置未保存：{exception.Message}", NoticeLevel.Warning, row.Configuration.Id);
            return false;
        }
        _detail.Target.Load(row.Configuration);
        SetNotice($"{row.Configuration.DisplayName}：设置已保存；运行中的本轮目标不变，新设置从下一轮开始生效。", NoticeLevel.Success, row.Configuration.Id);
        return true;
    }

    private async Task SaveSettingsAsync()
    {
        if (SelectedRow is { } row) await SaveSettingsCoreAsync(row);
    }

    private TaskRuntime RequireSelected() => SelectedRow ?? throw new InvalidOperationException("请先在左侧选择一个任务。");

    private async Task AddKeyAsync()
    {
        await Prompts.AddKey(this, async request =>
        {
            try
            {
                var row = await _coordinator.AddAsync(request.Key, new TaskConfiguration { DisplayName = request.Name, QuotaGroup = request.Group });
                _initialChecked = true;
                _checked.Add(row.Configuration.Id);
                if (await ResolveDirtyAsync()) Select(row.Configuration.Id, refresh: true);
                SetNotice($"{row.Configuration.DisplayName}：key 已加密保存。保存不会开始运行，可先验证连接。", NoticeLevel.Success, row.Configuration.Id);
                return null;
            }
            catch (ArgumentException exception) { return exception.Message; }
            catch (LocalStorageException exception) { return exception.Message; }
            catch (InvalidOperationException exception) { return exception.Message; }
        });
    }

    private async Task RunSelectedAsync()
    {
        var row = RequireSelected();
        var id = row.Configuration.Id;
        if (!await ResolveDirtyAsync()) return;
        var review = _detail.Overview.ReviewConfirm;
        if (row.Session.Recovery.State == RecoveryState.NeedsReview && !review.Checked)
        {
            _detail.ShowPage(0);
            throw new InvalidOperationException("有请求结果未知：请先核查官方用量，并在概览页勾选确认；不会自动重发这些请求。");
        }
        var guard = _coordinator.CaptureRunGuard(id);
        var willStartOrResume = row.Validating || row.Session.CurrentRun.State == RunState.Paused
            || (!row.Active && row.Session.Schedule.State != ScheduleState.Enabled);
        if (willStartOrResume && !Prompts.ConfirmRun(this, _coordinator.PreviewRun(id)))
        {
            SetNotice("已取消运行，未发出请求。", NoticeLevel.Info, id);
            return;
        }
        var reviewed = review.Checked;
        review.Checked = false;
        SetNotice($"{row.Configuration.DisplayName}：开始运行/继续；已有进度会继续剩余目标。", NoticeLevel.Info, id);
        _liveTaskId = id;
        var operation = _coordinator.RunOrContinueAsync(id, reviewed, guard.LifecycleVersion, guard.WaitOnlyIfPreviouslyBusy);
        UpdateView();
        await operation;
        if (!string.IsNullOrEmpty(row.Message)) SetNotice($"{row.Configuration.DisplayName}：{row.Message}", row.MessageLevel, id);
    }

    private async Task PauseSelectedAsync()
    {
        var row = RequireSelected();
        await _coordinator.PauseAsync(row.Configuration.Id);
        SetNotice($"{row.Configuration.DisplayName}：已暂停，未来定时已关闭；已发出的请求仍会结算。点运行/继续可继续剩余目标。",
            NoticeLevel.Success, row.Configuration.Id);
    }

    private async Task ResetSelectedAsync()
    {
        var row = RequireSelected();
        var review = _detail.Overview.ReviewConfirm;
        var needsReview = row.Session.Recovery.State == RecoveryState.NeedsReview;
        if (needsReview && !review.Checked)
        {
            _detail.ShowPage(0);
            throw new InvalidOperationException("存在请求结果未知：请先核查官方用量，并在概览页勾选确认；原进度保留。");
        }
        if (!Prompts.ConfirmReset(this, $"“{row.Configuration.DisplayName}”")) return;
        var reviewed = needsReview && review.Checked;
        review.Checked = false;
        SetNotice($"{row.Configuration.DisplayName}：正在收尾并重置，原记录会归档。", NoticeLevel.Info, row.Configuration.Id);
        await _coordinator.ResetAsync(row.Configuration.Id, reviewed);
    }

    private async Task ValidateSelectedAsync()
    {
        var row = RequireSelected();
        SetNotice($"{row.Configuration.DisplayName}：正在验证连接，只读取模型列表，不发送生成请求。", NoticeLevel.Info, row.Configuration.Id);
        UpdateView();
        await _coordinator.ValidateConnectionAsync(row.Configuration.Id);
    }

    private async Task EnablePlanAsync()
    {
        var row = RequireSelected();
        var id = row.Configuration.Id;
        var interval = _detail.Schedule.Interval;
        interval.Commit();
        // 已启用的定时之后自动运行不再确认，因此不允许直接改间隔；关闭后重新启用会再确认。
        if (row.Session.Schedule.State == ScheduleState.Enabled)
            throw new InvalidOperationException("定时已启用。要修改间隔，请先关闭定时，再按新间隔重新启用（会再次确认）。");
        if (row.Session.Recovery.State == RecoveryState.NeedsReview)
        {
            _detail.ShowPage(0);
            throw new InvalidOperationException("有请求结果未知：请先在概览页核查并继续或重置，再启用定时。");
        }
        if (!await ResolveDirtyAsync()) return;
        if (interval.Value != row.Configuration.IntervalHours)
            await _coordinator.SaveAsync(row.Configuration with { IntervalHours = interval.Value });
        var guard = _coordinator.CaptureRunGuard(id);
        var startsGeneration = !guard.WaitOnlyIfPreviouslyBusy && row.Session.CurrentRun.State != RunState.Paused;
        if (startsGeneration && !Prompts.ConfirmRun(this, _coordinator.PreviewRun(id, schedule: true)))
        {
            SetNotice("已取消启用定时，未发出请求。定时保持关闭。", NoticeLevel.Info, id);
            return;
        }
        SetNotice($"{row.Configuration.DisplayName}：正在启用定时，第一轮马上开始。", NoticeLevel.Info, id);
        _liveTaskId = id;
        var operation = _coordinator.StartAsync(id, schedule: true, expectedLifecycleVersion: guard.LifecycleVersion,
            waitOnlyIfPreviouslyBusy: guard.WaitOnlyIfPreviouslyBusy);
        UpdateView();
        await operation;
        if (!string.IsNullOrEmpty(row.Message)) SetNotice($"{row.Configuration.DisplayName}：{row.Message}", row.MessageLevel, id);
        else SetNotice($"{row.Configuration.DisplayName}：定时已启用，第一轮已开始；下次时间见定时页。", NoticeLevel.Success, id);
    }

    private async Task DisablePlanAsync()
    {
        var row = RequireSelected();
        await _coordinator.DisablePlanAsync(row.Configuration.Id);
        SetNotice($"{row.Configuration.DisplayName}：未来定时已关闭，当前这一轮不受影响。", NoticeLevel.Success, row.Configuration.Id);
    }

    private async Task BatchRunAsync()
    {
        var ids = CheckedIds();
        if (ids.Count == 0) { SetNotice("请先勾选要运行的任务。", NoticeLevel.Info); return; }
        if (!await ResolveDirtyAsync()) return;
        // 护栏必须在展示确认之前捕获：确认期间已收尾的任务只能“等待收尾”，不能被当成新一轮重新开始。
        var guards = _coordinator.CaptureRunGuards(ids);
        if (!Prompts.ConfirmRun(this, _coordinator.PreviewSelected(ids, continueRemaining: true)))
        {
            SetNotice("已取消运行所选任务，未发出请求。", NoticeLevel.Info);
            return;
        }
        SetNotice($"开始运行/继续 {ids.Count} 个所选任务，共享请求上限；结果未知的任务需要单独核查。", NoticeLevel.Info);
        await _coordinator.RunOrContinueSelectedAsync(ids, guards);
    }

    private async Task BatchPauseAsync()
    {
        var ids = CheckedIds();
        if (ids.Count == 0) { SetNotice("请先勾选任务。", NoticeLevel.Info); return; }
        await _coordinator.PauseSelectedAsync(ids);
        SetNotice($"已暂停 {ids.Count} 个所选任务，定时已关闭；已发出的请求仍会结算。", NoticeLevel.Success);
    }

    private async Task BatchResetAsync()
    {
        var ids = CheckedIds();
        if (ids.Count == 0) { SetNotice("请先勾选任务。", NoticeLevel.Info); return; }
        if (!Prompts.ConfirmReset(this, $"所选 {ids.Count} 个 key ")) return;
        await _coordinator.ResetSelectedAsync(ids);
        SetNotice("所选任务的重置已处理，没有自动运行；未能重置的原因见各任务。", NoticeLevel.Info);
    }

    private async Task PauseEverythingAsync()
    {
        await _coordinator.PauseAllAsync();
        SetNotice("已暂停全部任务并关闭定时；已发出的请求仍会结算。", NoticeLevel.Success);
    }

    internal void ToggleChecked(Guid id)
    {
        if (Find(id) is not { Configuration.Enabled: true }) return;
        if (!_checked.Remove(id)) _checked.Add(id);
        _initialChecked = true;
        UpdateView();
    }

    private void ToggleSelectAll()
    {
        var enabled = _coordinator.Tasks.Where(row => row.Configuration.Enabled).Select(row => row.Configuration.Id).ToList();
        if (enabled.All(_checked.Contains)) _checked.Clear();
        else foreach (var id in enabled) _checked.Add(id);
        _initialChecked = true;
        UpdateView();
    }

    private void ShowTaskMenu(Guid id, Point screen)
    {
        if (Find(id) is not { } row) return;
        var menu = MenuRenderer.Create();
        var enabled = row.Configuration.Enabled;
        menu.Items.Add(MenuRenderer.Item("验证连接", () => _ = ExecuteAsync(async () => { await RequestSelectAsync(id); if (_selected == id) await ValidateSelectedAsync(); })));
        menu.Items.Add(MenuRenderer.Item(enabled ? "停用此 key" : "启用此 key", () => _ = ExecuteAsync(() => SetTaskEnabledAsync(id, !enabled))));
        menu.Items.Add(MenuRenderer.Item("复制数据位置", () =>
        {
            try
            {
                Clipboard.SetText(_coordinator.Workspace.Storage.RootDirectory);
                SetNotice("数据位置已复制。", NoticeLevel.Success);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            { SetNotice("剪贴板正被其他程序占用，请稍后再试；数据位置也可在设置里查看。", NoticeLevel.Warning); }
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MenuRenderer.Item("删除任务…", () => _ = ExecuteAsync(() => DeleteTaskAsync(id)), danger: true));
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(screen);
    }

    private async Task SetTaskEnabledAsync(Guid id, bool enabled)
    {
        if (Find(id) is not { } row) return;
        if (id == _selected && !await ResolveDirtyAsync()) return;
        await _coordinator.SaveAsync(row.Configuration with { Enabled = enabled });
        if (!enabled) _checked.Remove(id);
        if (id == _selected) _detail.Target.Load(row.Configuration);
        SetNotice(enabled ? $"{row.Configuration.DisplayName}：已启用。" : $"{row.Configuration.DisplayName}：已停用，不参与批量运行；定时已关闭。",
            NoticeLevel.Success, id);
    }

    internal async Task DeleteTaskAsync(Guid id)
    {
        if (Find(id) is not { } row) return;
        var name = row.Configuration.DisplayName;
        if (!Prompts.ConfirmDelete(this, name)) return;
        var residual = await _coordinator.DeleteAsync(id);
        _checked.Remove(id);
        _burn.Forget(id);
        if (_selected == id)
        {
            _selected = null;
            if (_coordinator.Tasks.Count > 0) Select(_coordinator.Tasks[0].Configuration.Id, refresh: false);
        }
        if (residual is null) SetNotice($"已删除“{name}”及其加密 key、运行记录和定时。", NoticeLevel.Success);
        else SetNotice($"已删除“{name}”，但任务目录被占用未能删除（已不再使用），可稍后手动删除：{residual}", NoticeLevel.Warning);
    }

    private async Task ShowSettingsAsync()
    {
        var version = Application.ProductVersion.Split('+')[0];
        var model = new SettingsModel(Theme.Mode, _coordinator.Limiter.Maximum, PerKeyLimit(), _coordinator.CanChangeConcurrency,
            _coordinator.Workspace.Storage.RootDirectory, version, SenseNovaDefaults.BaseUrl, SenseNovaDefaults.RebateModel, _coordinator.IsMockMode);
        using var dialog = new SettingsDialog(model, mode => ApplyThemeMode(mode, save: true), async (shared, perKey) =>
        {
            try
            {
                await _coordinator.SetConcurrencyAsync(shared, perKey);
                SetNotice($"并发已保存：全应用最多 {shared} 笔，单个 key 最多 {perKey} 笔。", NoticeLevel.Success);
                UpdateView();
                return null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or LocalStorageException)
            { return exception.Message; }
        });
        dialog.ShowDialog(this);
        await Task.CompletedTask;
    }

    // ---- 窗口与托盘 ----

    internal void Restore()
    {
        Show();
        WindowState = _restore;
        if (!ShowInBackground) Activate();
        UpdatePulse();
    }

    public void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        Close();
    }

    private async void CloseAfterTasks(object? sender, FormClosingEventArgs args)
    {
        if (_ready) return;
        args.Cancel = true;
        if (!_exiting && args.CloseReason == CloseReason.UserClosing)
        {
            Hide();
            if (!_hiddenHintShown && !ShowInBackground)
            {
                _hiddenHintShown = true;
                ShowBalloon("面板已收进托盘", "任务会继续运行。左键托盘图标打开面板，右键可暂停全部或退出。", NoticeLevel.Info);
            }
            return;
        }
        if (_closing) return;
        _closing = true; _exiting = true; _timer.Stop(); _pulseTimer.Stop();
        try { await _coordinator.DisposeAsync(); }
        catch (Exception)
        {
            // 收尾失败也必须能退出；检查点在每笔请求前后已落盘，重开时会要求核查。
        }
        finally
        {
            _ready = true; _tray.Visible = false;
            if (!IsDisposed) BeginInvoke(new Action(Close));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coordinator.Noticed -= OnCoordinatorNotice;
            Theme.Changed -= OnThemeChanged;
            _timer.Dispose(); _pulseTimer.Dispose();
            _tray.Visible = false; _tray.Dispose(); _trayMenu.Dispose();
            _windowIcon.Dispose(); _trayIdle.Dispose(); _trayBurning.Dispose();
        }
        base.Dispose(disposing);
    }
}
