using SenseNova.TokenBurner.Desktop.ViewModels;

namespace SenseNova.TokenBurner.Desktop.Ui;

/// <summary>右侧详情：任务标题、单任务操作、四个页签。没有选中任务时显示空状态。</summary>
internal sealed class DetailPane : UiControl
{
    private readonly ScrollHost _scroll = new();
    private string _name = "";
    private string _group = "";
    private string _hint = "";
    private bool _empty = true;
    private bool _noTasks = true;
    private bool _failed;
    private readonly ToolTip _tips = Tips.Create();

    public DetailPane()
    {
        Validate = new UiButton("验证连接", ButtonKind.Secondary, Glyphs.Plug, "ValidateTask") { Compact = true };
        More = new UiButton("", ButtonKind.Ghost, Glyphs.More, "TaskMenu") { AccessibleName = "更多操作" };
        Run = new UiButton("运行/继续", ButtonKind.Primary, Glyphs.Play, "RunTask");
        Pause = new UiButton("暂停", ButtonKind.Secondary, Glyphs.Pause, "PauseTask");
        Reset = new UiButton("重置", ButtonKind.Danger, Glyphs.Reset, "ResetTask");
        Tabs = new UiSegmented(SegmentedStyle.Tabs, "概览", "目标设置", "定时", "运行记录") { Name = "DetailTabs", AccessibleName = "任务详情页签" };
        Tabs.SetSelectedSilently(0);
        Tabs.SelectedIndexChanged += (_, _) => ShowPage(Tabs.SelectedIndex);
        _tips.SetToolTip(Validate, "只读取官方模型列表，检查已保存的 key 和活动模型；不发送生成请求，不产生生成用量。");
        _tips.SetToolTip(Run, "首次从 0 开始；暂停或中断后继续剩余目标，沿用原目标和已确认进度。开始前会先显示请求规模和额度提示。");
        _tips.SetToolTip(Pause, "停止新增请求并保留本轮进度；已发出的请求仍会结算。暂停会关闭定时。");
        _tips.SetToolTip(Reset, "先结束当前轮和定时，把本轮记录归档后进度归零。不会退还或刷新官方额度，也不会自动运行。");
        Controls.AddRange([Validate, More, Run, Pause, Reset, Tabs, _scroll, Pill]);
        ShowPage(0);
        foreach (Control control in Controls) control.Visible = false; // 初始为空状态，选中任务后再显示
    }

    public UiButton Validate { get; }
    public UiButton More { get; }
    public UiButton Run { get; }
    public UiButton Pause { get; }
    public UiButton Reset { get; }
    public UiSegmented Tabs { get; }
    public UiPill Pill { get; } = new();
    public OverviewPage Overview { get; } = new();
    public TargetPage Target { get; } = new();
    public SchedulePage Schedule { get; } = new();
    public HistoryPage History { get; } = new();
    public event Action<int>? PageShown;

    public void ShowPage(int index)
    {
        Control page = index switch { 1 => Target, 2 => Schedule, 3 => History, _ => Overview };
        if (Tabs.SelectedIndex != index) Tabs.SetSelectedSilently(index);
        _scroll.Content = page;
        PageShown?.Invoke(index);
    }

    public void RefreshPage() => _scroll.PerformLayout();

    public void SetEmpty(bool empty, bool noTasks = false, bool failed = false)
    {
        if (noTasks != _noTasks || failed != _failed) { _noTasks = noTasks; _failed = failed; Invalidate(); }
        if (empty == _empty) return;
        _empty = empty;
        foreach (Control control in Controls) control.Visible = !empty;
        PerformLayout();
        Invalidate();
    }

    public void SetHeader(string name, string group, string status, TaskStatusKind kind, string hint)
    {
        Pill.Set(status, kind);
        if (name == _name && group == _group && hint == _hint) return;
        _name = name; _group = group; _hint = hint;
        PerformLayout();
        Invalidate();
    }

    private int Pad => S(22);
    private int HeaderTop => S(18);
    private int ActionsTop => S(68);
    private int TabsTop => S(118);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var more = S(32);
        More.SetBounds(Width - Pad - more, HeaderTop, more, more);
        var validate = Validate.GetPreferredSize(Size.Empty);
        Validate.SetBounds(More.Left - S(6) - validate.Width, HeaderTop + (more - validate.Height) / 2, validate.Width, validate.Height);
        var nameWidth = Math.Min(Draw.Measure(_name, F.Title).Width, Math.Max(S(60), Validate.Left - Pad - S(200)));
        var groupWidth = string.IsNullOrEmpty(_group) ? 0 : Draw.Measure(_group, F.Caption).Width + S(18);
        Pill.Location = new Point(Pad + nameWidth + S(10) + (groupWidth > 0 ? groupWidth + S(8) : 0), HeaderTop + (more - Pill.Height) / 2);
        var x = Pad;
        foreach (var button in new[] { Run, Pause, Reset })
        {
            var size = button.GetPreferredSize(Size.Empty);
            button.SetBounds(x, ActionsTop, size.Width, size.Height);
            x += size.Width + S(8);
        }
        Tabs.SetBounds(Pad - S(6), TabsTop, Math.Min(Width - Pad * 2, Tabs.PreferredWidth), Tabs.PreferredHeight);
        var top = TabsTop + Tabs.PreferredHeight + S(16);
        _scroll.SetBounds(Pad, top, Width - Pad - S(10), Math.Max(0, Height - top - S(8)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintPanel(g, Theme.Sf(12), P.Surface, P.Line);
        if (_empty)
        {
            var center = Height / 2;
            var title = _failed ? "无法读取任务配置" : _noTasks ? "从添加一个 API key 开始" : "选择左侧的 key 查看详情";
            var body = _failed ? "原文件已保留，没有启动任何任务。请检查数据目录里的 tasks.json，确认后重新打开应用；不要直接删除数据目录，里面有加密 key 和运行记录。"
                : _noTasks ? "添加后可以先验证连接（只读取模型列表），再设置目标、运行或启用定时。"
                : "这里会显示它的燃烧进度、目标设置、定时和运行记录。";
            Draw.Text(g, title, F.Section, new Rectangle(0, center - S(24), Width, S(26)), _failed ? P.Danger : P.Ink,
                Draw.Single | TextFormatFlags.HorizontalCenter);
            Draw.Text(g, body, F.Body, new Rectangle(S(40), center + S(8), Width - S(80), S(60)), P.Muted,
                Draw.Wrap | TextFormatFlags.HorizontalCenter);
            return;
        }
        var nameWidth = Math.Min(Draw.Measure(_name, F.Title).Width, Math.Max(S(60), Validate.Left - Pad - S(200)));
        Draw.Text(g, _name, F.Title, new Rectangle(Pad, HeaderTop - S(2), nameWidth + S(2), S(36)), P.Ink);
        if (!string.IsNullOrEmpty(_group))
        {
            var width = Draw.Measure(_group, F.Caption).Width + S(18);
            var tag = new Rectangle(Pad + nameWidth + S(10), HeaderTop + (S(32) - S(22)) / 2, width, S(22));
            Draw.Smooth(g);
            Draw.Stroke(g, tag, tag.Height / 2f, P.LineStrong);
            Draw.Text(g, _group, F.Caption, tag, P.Muted, Draw.Single | TextFormatFlags.HorizontalCenter);
        }
        if (_hint.Length > 0)
        {
            var left = Reset.Right + S(16);
            Draw.Text(g, _hint, F.Caption, new Rectangle(left, ActionsTop, Width - Pad - left, Run.Height), P.Muted, Draw.Single | TextFormatFlags.Right);
        }
        using var line = new Pen(P.Line);
        var tabsBottom = TabsTop + Tabs.PreferredHeight - 1;
        g.DrawLine(line, Pad, tabsBottom, Width - Pad, tabsBottom);
    }

    public override Color BackColor { get => Theme.Current.Surface; set { } }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
            // 未挂在滚动容器上的页面不会随控件树释放，这里统一释放。
            foreach (var page in new Control[] { Overview, Target, Schedule, History }) if (page.Parent is null) page.Dispose();
        }
        base.Dispose(disposing);
    }
}
