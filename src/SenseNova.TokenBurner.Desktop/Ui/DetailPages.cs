using System.Globalization;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;

namespace SenseNova.TokenBurner.Desktop.Ui;

internal sealed record Readout(string Caption, string Value, string? Sub = null, TaskStatusKind? Tone = null);

/// <summary>读数网格：标题 + 大号数字 + 补充说明，两列或四列排布。</summary>
internal sealed class ReadoutGrid : UiControl, IHeightForWidth
{
    private IReadOnlyList<Readout> _items = [];
    public void SetItems(IReadOnlyList<Readout> items)
    {
        if (items.SequenceEqual(_items)) return;
        _items = items; Invalidate();
    }
    private int Columns(int width) => width >= S(560) ? 4 : 2;
    private int CellHeight => S(58);
    public int HeightForWidth(int width) => (int)Math.Ceiling(_items.Count / (double)Columns(width)) * CellHeight;
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var columns = Columns(Width);
        var cellWidth = Width / columns;
        using var line = new Pen(P.Line);
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var x = (i % columns) * cellWidth;
            var y = (i / columns) * CellHeight;
            if (i % columns > 0) g.DrawLine(line, x, y + S(10), x, y + CellHeight - S(10));
            if (i >= columns) g.DrawLine(line, x + (i % columns == 0 ? 0 : S(14)), y, x + cellWidth - S(14), y);
            var left = x + (i % columns == 0 ? 0 : S(16));
            var width = cellWidth - S(20);
            Draw.Text(g, item.Caption, F.Caption, new Rectangle(left, y + S(8), width, S(18)), P.Muted);
            var color = item.Tone switch
            {
                TaskStatusKind.Warning => P.Warn, TaskStatusKind.Danger => P.Danger, TaskStatusKind.Burning => P.Ember,
                TaskStatusKind.Success => P.Ok, _ => P.Ink
            };
            var valueSize = Draw.Measure(item.Value, F.NumberMedium);
            Draw.Text(g, item.Value, F.NumberMedium, new Rectangle(left, y + S(25), Math.Min(width, valueSize.Width + S(2)), S(26)), color);
            // 补充说明只在完整放得下时显示，不出现被截断的“…”。
            if (!string.IsNullOrEmpty(item.Sub) && valueSize.Width + S(6) + Draw.Measure(item.Sub, F.Caption).Width <= width)
                Draw.Text(g, item.Sub, F.Caption, new Rectangle(left + valueSize.Width + S(6), y + S(30), width - valueSize.Width - S(6), S(20)), P.Muted);
        }
    }
}

/// <summary>每分钟确认 tokens 柱状图（最近 30 分钟，仅本次打开期间的采样）。</summary>
internal sealed class BurnChart : UiControl, IHeightForWidth
{
    private long[] _buckets = new long[30];
    private bool _burning;
    public void SetData(long[] buckets, bool burning)
    {
        if (burning == _burning && buckets.SequenceEqual(_buckets)) return;
        _buckets = buckets; _burning = burning; Invalidate();
    }
    public int HeightForWidth(int width) => S(132);
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Draw.Text(g, "每分钟确认 tokens", F.BodyBold, new Rectangle(0, 0, Width / 2, S(22)), P.Ink);
        var plot = new Rectangle(0, S(32), Width, Height - S(52));
        var max = _buckets.DefaultIfEmpty(0).Max();
        if (max > 0)
            Draw.Text(g, "峰值 " + Format.Compact(max) + "/分钟", F.NumberCaption, new Rectangle(Width / 2, 0, Width / 2, S(22)), P.Muted, Draw.Single | TextFormatFlags.Right);
        using (var axis = new Pen(P.Line)) g.DrawLine(axis, plot.X, plot.Bottom, plot.Right, plot.Bottom);
        Draw.Text(g, "30 分钟前", F.NumberCaption, new Rectangle(0, plot.Bottom + S(4), S(80), S(16)), P.Subtle);
        Draw.Text(g, "现在", F.Caption, new Rectangle(Width - S(60), plot.Bottom + S(4), S(60), S(16)), P.Subtle, Draw.Single | TextFormatFlags.Right);
        if (max <= 0)
        {
            Draw.Text(g, "运行后这里显示每分钟确认的用量。", F.Caption, plot, P.Subtle, Draw.Single | TextFormatFlags.HorizontalCenter);
            return;
        }
        using (var grid = new Pen(P.Line) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
            g.DrawLine(grid, plot.X, plot.Y, plot.Right, plot.Y);
        Draw.Smooth(g);
        var slot = plot.Width / (float)_buckets.Length;
        var bar = Math.Max(S(2), slot * 0.62f);
        for (var i = 0; i < _buckets.Length; i++)
        {
            var value = _buckets[i];
            if (value <= 0) continue;
            var height = Math.Max(S(3), (float)(plot.Height * value / (double)max));
            var current = i == _buckets.Length - 1;
            var color = _burning ? P.Ember : P.Primary;
            if (current && _burning) color = Draw.Mix(color, P.Surface, 0.35f);
            Draw.Fill(g, new RectangleF(plot.X + slot * i + (slot - bar) / 2, plot.Bottom - height, bar, height), Math.Min(bar / 2, Theme.Sf(3)), color);
        }
    }
}

/// <summary>概览页：仪表 + 读数 + 速率图 + 需要处理的提示。</summary>
internal sealed class OverviewPage : UiControl, IHeightForWidth
{
    public OverviewPage()
    {
        Review.Level = NoticeLevel.Warning;
        Review.Title = "有请求结果未知";
        Review.Content = ReviewConfirm;
        Message.Level = NoticeLevel.Info;
        Note.Level = NoticeLevel.Info;
        Controls.AddRange([Gauge, Readouts, Chart, Review, Message, Note]);
    }
    public UiGauge Gauge { get; } = new();
    public ReadoutGrid Readouts { get; } = new();
    public BurnChart Chart { get; } = new();
    public UiCallout Review { get; } = new();
    public UiToggle ReviewConfirm { get; } = new("我已核查官方用量和剩余额度，接受未知记录", ToggleStyle.Check, "ReviewConfirm");
    public UiCallout Message { get; } = new();
    public UiCallout Note { get; } = new();

    public int HeightForWidth(int width) => Arrange(width, apply: false);
    protected override void OnLayout(LayoutEventArgs levent) { base.OnLayout(levent); Arrange(Width, apply: true); }

    private int Arrange(int width, bool apply)
    {
        var y = S(4);
        var wide = width >= S(500);
        var gaugeSize = wide ? Math.Clamp((int)(width * 0.36), S(176), S(220)) : S(220);
        var gaugeHeight = gaugeSize * 204 / 220;
        if (wide)
        {
            var readoutsWidth = width - gaugeSize - S(24);
            var readoutsHeight = Readouts.HeightForWidth(readoutsWidth);
            if (apply)
            {
                Gauge.SetBounds(0, y, gaugeSize, gaugeHeight);
                Readouts.SetBounds(gaugeSize + S(24), y + Math.Max(0, (gaugeHeight - readoutsHeight) / 2), readoutsWidth, readoutsHeight);
            }
            y += Math.Max(gaugeHeight, readoutsHeight) + S(18);
        }
        else
        {
            if (apply) Gauge.SetBounds((width - gaugeSize) / 2, y, gaugeSize, S(204));
            y += S(212);
            var readoutsHeight = Readouts.HeightForWidth(width);
            if (apply) Readouts.SetBounds(0, y, width, readoutsHeight);
            y += readoutsHeight + S(16);
        }
        foreach (var callout in new[] { Review, Message, Note })
        {
            if (!callout.Visible) continue;
            var height = callout.HeightForWidth(width);
            if (apply) callout.SetBounds(0, y, width, height);
            y += height + S(10);
        }
        y += S(6);
        var chartHeight = Chart.HeightForWidth(width);
        if (apply) Chart.SetBounds(0, y, width, chartHeight);
        return y + chartHeight + S(8);
    }
}

/// <summary>目标设置页：名称、额度组、比例/自定义目标、启用状态；修改先标记未保存，显式保存。</summary>
internal sealed class TargetPage : UiControl, IHeightForWidth
{
    private TaskConfiguration? _baseline;
    private bool _loading;
    private readonly List<(string Text, Rectangle Bounds, bool Strong)> _labels = [];
    private string _targetSummary = "";

    public TargetPage()
    {
        NameInput = new UiInput("例如 key-1", maxLength: 80, accessibleName: "名称") { Name = "TaskName" };
        GroupInput = new UiInput("例如 账户A（选填）", maxLength: 80, accessibleName: "额度组") { Name = "QuotaGroup" };
        Presets = new UiSegmented(SegmentedStyle.Pills, "25%", "50%", "75%", "95%") { Name = "Presets", AccessibleName = "目标比例预设" };
        Slider = new UiSlider(0, ConsumptionPolicy.MaximumPercentage, ConsumptionPolicy.DefaultPercentage, 25, 50, 75, 95) { Name = "Percentage", AccessibleName = "目标比例" };
        Custom = new UiToggle("使用自定义 tokens 目标", ToggleStyle.Switch, "CustomTarget");
        Tokens = new UiNumber(0, ConsumptionPolicy.MaximumTargetTokens, 120_000_000, 0, 1_000_000, "tokens", "自定义目标 tokens") { Name = "CustomTokens", Thousands = true };
        EnabledToggle = new UiToggle("启用此 key", ToggleStyle.Switch, "TaskEnabled");
        EnabledNote = new TextBlock(() => Theme.Fonts.Caption, () => Theme.Current.Muted, "停用后不参与批量运行，也不能单独运行；记录和设置保留。");
        Save = new UiButton("保存设置", ButtonKind.Primary, Glyphs.Check, "SaveSettings");
        Revert = new UiButton("撤销修改", ButtonKind.Secondary, null, "RevertSettings");
        Footnote = new TextBlock(() => Theme.Fonts.Caption, () => Theme.Current.Muted, "运行中的本轮目标不变，保存后的设置从下一轮开始生效。");
        Risk.Level = NoticeLevel.Info;
        Controls.AddRange([NameInput, GroupInput, Presets, Slider, Custom, Tokens, Risk, EnabledToggle, EnabledNote, Save, Revert, Footnote]);

        NameInput.TextChanged += (_, _) => Changed();
        GroupInput.TextChanged += (_, _) => Changed();
        Slider.ValueChanged += (_, _) => { if (!_loading) { Custom.Checked = false; } SyncPresets(); Changed(); };
        Presets.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || Presets.SelectedIndex < 0) return;
            Custom.Checked = false;
            Slider.Value = new[] { 25, 50, 75, 95 }[Presets.SelectedIndex];
        };
        Custom.CheckedChanged += (_, _) => { Tokens.Enabled = Custom.Checked; SyncPresets(); Changed(); };
        Tokens.ValueChanged += (_, _) => Changed();
        Tokens.Edited += (_, _) => Changed();
        EnabledToggle.CheckedChanged += (_, _) => Changed();
        Revert.Click += (_, _) => { if (_baseline is not null) Load(_baseline); };
    }

    public UiInput NameInput { get; }
    public UiInput GroupInput { get; }
    public UiSegmented Presets { get; }
    public UiSlider Slider { get; }
    public UiToggle Custom { get; }
    public UiNumber Tokens { get; }
    public UiCallout Risk { get; } = new();
    public UiToggle EnabledToggle { get; }
    public TextBlock EnabledNote { get; }
    public UiButton Save { get; }
    public UiButton Revert { get; }
    public TextBlock Footnote { get; }
    public bool IsDirty { get; private set; }
    public Guid? TaskId => _baseline?.Id;
    public event Action? DirtyChanged;

    public void Load(TaskConfiguration configuration)
    {
        _loading = true;
        try
        {
            _baseline = configuration;
            NameInput.Text = configuration.DisplayName;
            GroupInput.Text = configuration.QuotaGroup;
            Slider.Value = configuration.Percentage;
            Custom.Checked = configuration.CustomTargetTokens is not null;
            Tokens.Value = configuration.CustomTargetTokens ?? configuration.TargetTokens;
            Tokens.Enabled = Custom.Checked;
            EnabledToggle.Checked = configuration.Enabled;
            SyncPresets();
        }
        finally { _loading = false; }
        Changed();
    }

    /// <summary>把界面上的值套到已保存配置上（只改本页负责的字段）。commit 为真时先提交正在输入的数字（保存时使用）。</summary>
    public TaskConfiguration Apply(TaskConfiguration saved, bool commit = false)
    {
        if (commit) Tokens.Commit();
        return saved with
        {
            DisplayName = NameInput.Text.Trim(), QuotaGroup = GroupInput.Text.Trim(), Percentage = Slider.Value,
            CustomTargetTokens = Custom.Checked ? (long)Tokens.PendingValue : null, Enabled = EnabledToggle.Checked
        };
    }

    /// <summary>已保存配置的可编辑字段是否与本页载入时不同（例如从菜单停用了 key）。</summary>
    public bool BaselineDiffers(TaskConfiguration saved) => _baseline is null || _baseline.Id != saved.Id
        || _baseline.DisplayName != saved.DisplayName || _baseline.QuotaGroup != saved.QuotaGroup || _baseline.Percentage != saved.Percentage
        || _baseline.CustomTargetTokens != saved.CustomTargetTokens || _baseline.Enabled != saved.Enabled;

    public long CurrentTarget => Custom.Checked ? (long)Tokens.PendingValue : ConsumptionPolicy.GetTargetTokens(Slider.Value);

    private void SyncPresets()
    {
        var index = Custom.Checked ? -1 : Array.IndexOf(new[] { 25, 50, 75, 95 }, Slider.Value);
        Presets.SetSelectedSilently(index);
    }

    private void Changed()
    {
        if (_loading) return;
        var target = CurrentTarget;
        _targetSummary = Custom.Checked ? $"自定义 {Format.Exact(target)} tokens" : $"{Slider.Value}% 对应 {Format.Exact(target)} tokens";
        Risk.Text = TargetRisk.Describe(target);
        Risk.Level = target > ConsumptionPolicy.GetTargetTokens(95) ? NoticeLevel.Warning : NoticeLevel.Info;
        var dirty = _baseline is not null && !Equals(Apply(_baseline), _baseline);
        Save.Enabled = Revert.Enabled = dirty;
        if (dirty != IsDirty) { IsDirty = dirty; DirtyChanged?.Invoke(); }
        PerformLayout();
        Invalidate();
    }

    public int HeightForWidth(int width) => Arrange(width, apply: false);
    protected override void OnLayout(LayoutEventArgs levent) { base.OnLayout(levent); Arrange(Width, apply: true); }

    private int Arrange(int width, bool apply)
    {
        if (apply) _labels.Clear();
        var y = S(4);
        var labelHeight = S(20);
        void Label(string text, int x, int top, int w, bool strong = false) { if (apply) _labels.Add((text, new Rectangle(x, top, w, labelHeight), strong)); }
        var twoColumns = width >= S(460);
        var column = twoColumns ? (width - S(16)) / 2 : width;
        Label("名称", 0, y, column);
        if (twoColumns) Label("额度组（选填，同一账户的 key 填相同名称）", column + S(16), y, column);
        y += labelHeight + S(4);
        if (apply) NameInput.SetBounds(0, y, column, NameInput.PreferredHeight);
        if (twoColumns)
        {
            if (apply) GroupInput.SetBounds(column + S(16), y, column, GroupInput.PreferredHeight);
            y += NameInput.PreferredHeight + S(18);
        }
        else
        {
            y += NameInput.PreferredHeight + S(12);
            Label("额度组（选填，同一账户的 key 填相同名称）", 0, y, width);
            y += labelHeight + S(4);
            if (apply) GroupInput.SetBounds(0, y, width, GroupInput.PreferredHeight);
            y += GroupInput.PreferredHeight + S(18);
        }

        // 标题行：左侧“本轮目标 + 当前目标值”，右侧比例预设；放不下时预设换到下一行。
        var presetsWidth = Math.Min(width, Math.Max(Presets.PreferredWidth, S(240)));
        var summaryWidth = Draw.Measure("本轮目标", F.BodyBold).Width + S(12) + Draw.Measure(_targetSummary, F.Number).Width;
        if (width - presetsWidth - S(16) >= summaryWidth)
        {
            if (apply) _labels.Add(("本轮目标", new Rectangle(0, y, width - presetsWidth - S(16), Presets.PreferredHeight), true));
            if (apply) Presets.SetBounds(width - presetsWidth, y, presetsWidth, Presets.PreferredHeight);
            y += Presets.PreferredHeight + S(4);
        }
        else
        {
            Label("本轮目标", 0, y, width, strong: true);
            y += labelHeight + S(6);
            if (apply) Presets.SetBounds(0, y, presetsWidth, Presets.PreferredHeight);
            y += Presets.PreferredHeight + S(6);
        }
        if (apply) Slider.SetBounds(0, y, width, Slider.PreferredHeight);
        y += Slider.PreferredHeight + S(8);
        var customSize = Custom.GetPreferredSize(Size.Empty);
        if (twoColumns)
        {
            if (apply)
            {
                Custom.SetBounds(0, y + (Tokens.PreferredHeight - customSize.Height) / 2, customSize.Width, customSize.Height);
                Tokens.SetBounds(Math.Max(customSize.Width + S(16), column + S(16)), y, width - Math.Max(customSize.Width + S(16), column + S(16)), Tokens.PreferredHeight);
            }
            y += Tokens.PreferredHeight + S(12);
        }
        else
        {
            if (apply) Custom.SetBounds(0, y, customSize.Width, customSize.Height);
            y += customSize.Height + S(8);
            if (apply) Tokens.SetBounds(0, y, width, Tokens.PreferredHeight);
            y += Tokens.PreferredHeight + S(12);
        }
        var riskHeight = Risk.HeightForWidth(width);
        if (apply) Risk.SetBounds(0, y, width, riskHeight);
        y += riskHeight + S(16);

        // 启用开关与说明同一行；窄时说明换到开关下方。
        var enabledSize = EnabledToggle.GetPreferredSize(Size.Empty);
        var noteLeft = enabledSize.Width + S(16);
        if (width - noteLeft >= S(240))
        {
            var noteHeight = EnabledNote.HeightForWidth(width - noteLeft);
            var rowHeight = Math.Max(enabledSize.Height, noteHeight);
            if (apply)
            {
                EnabledToggle.SetBounds(0, y + (rowHeight - enabledSize.Height) / 2, enabledSize.Width, enabledSize.Height);
                EnabledNote.SetBounds(noteLeft, y + (rowHeight - noteHeight) / 2, width - noteLeft, noteHeight);
            }
            y += rowHeight + S(18);
        }
        else
        {
            if (apply) EnabledToggle.SetBounds(0, y, enabledSize.Width, enabledSize.Height);
            y += enabledSize.Height + S(2);
            var noteHeight = EnabledNote.HeightForWidth(width - S(42));
            if (apply) EnabledNote.SetBounds(S(42), y, width - S(42), noteHeight);
            y += noteHeight + S(18);
        }

        var save = Save.GetPreferredSize(Size.Empty);
        var revert = Revert.GetPreferredSize(Size.Empty);
        if (apply)
        {
            Save.SetBounds(0, y, save.Width, save.Height);
            Revert.SetBounds(save.Width + S(8), y, revert.Width, revert.Height);
        }
        y += save.Height + S(6);
        var footHeight = Footnote.HeightForWidth(width);
        if (apply) Footnote.SetBounds(0, y, width, footHeight);
        return y + footHeight + S(12);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        foreach (var (text, bounds, strong) in _labels)
        {
            Draw.Text(g, text, strong ? F.BodyBold : F.Caption, bounds, strong ? P.Ink : P.Muted);
            if (strong && _targetSummary.Length > 0)
            {
                var left = bounds.X + Draw.Measure(text, F.BodyBold).Width + S(12);
                Draw.Text(g, _targetSummary, F.Number, new Rectangle(left, bounds.Y, Math.Max(0, bounds.Right - left), bounds.Height),
                    Custom.Checked && CurrentTarget > ConsumptionPolicy.GetTargetTokens(95) ? P.Warn : P.Muted);
            }
        }
        if (IsDirty)
        {
            var text = "有未保存的修改";
            var x = Revert.Right + S(14);
            Draw.Glyph(g, Glyphs.Edit, F.IconSmall, new Rectangle(x, Save.Top, S(16), Save.Height), P.Warn);
            Draw.Text(g, text, F.Caption, new Rectangle(x + S(20), Save.Top, Width - x - S(20), Save.Height), P.Warn);
        }
    }
}

/// <summary>定时页：间隔、启用/关闭和下一次时间。</summary>
internal sealed class SchedulePage : UiControl, IHeightForWidth
{
    private Rectangle _intervalLabel;
    public SchedulePage()
    {
        Interval = new UiNumber(0.01m, 8760, 5, 2, 0.5m, "小时", "运行间隔小时") { Name = "IntervalHours" };
        Enable = new UiButton("启用定时", ButtonKind.Primary, Glyphs.Clock, "EnablePlan");
        Disable = new UiButton("关闭定时", ButtonKind.Secondary, null, "DisablePlan");
        Notes = new TextBlock(() => Theme.Fonts.Caption, () => Theme.Current.Muted,
            "启用后会立即开始第一轮，之后每隔设定时间自动开始新一轮；关闭应用后定时不会继续，重开需要再次启用。\n"
            + "这只是本地计划间隔，不代表官方额度的刷新时间（官方额度按滚动窗口计算）。暂停或重置任务也会关闭定时。");
        Controls.AddRange([State, Interval, Enable, Disable, Notes]);
    }
    public UiCallout State { get; } = new();
    public UiNumber Interval { get; }
    public UiButton Enable { get; }
    public UiButton Disable { get; }
    public TextBlock Notes { get; }

    public int HeightForWidth(int width) => Arrange(width, false);
    protected override void OnLayout(LayoutEventArgs levent) { base.OnLayout(levent); Arrange(Width, true); }
    private int Arrange(int width, bool apply)
    {
        var y = S(4);
        var stateHeight = State.HeightForWidth(width);
        if (apply) State.SetBounds(0, y, width, stateHeight);
        y += stateHeight + S(18);
        if (apply) _intervalLabel = new Rectangle(0, y, width, S(22));
        y += S(26);
        var intervalWidth = Math.Min(width, S(220));
        if (apply) Interval.SetBounds(0, y, intervalWidth, Interval.PreferredHeight);
        var enable = Enable.GetPreferredSize(Size.Empty);
        var disable = Disable.GetPreferredSize(Size.Empty);
        if (width >= intervalWidth + enable.Width + disable.Width + S(32))
        {
            if (apply)
            {
                Enable.SetBounds(intervalWidth + S(12), y + (Interval.PreferredHeight - enable.Height) / 2, enable.Width, enable.Height);
                Disable.SetBounds(Enable.Right + S(8), Enable.Top, disable.Width, disable.Height);
            }
            y += Interval.PreferredHeight + S(18);
        }
        else
        {
            y += Interval.PreferredHeight + S(10);
            if (apply)
            {
                Enable.SetBounds(0, y, enable.Width, enable.Height);
                Disable.SetBounds(Enable.Right + S(8), y, disable.Width, disable.Height);
            }
            y += enable.Height + S(18);
        }
        var notes = Notes.HeightForWidth(width);
        if (apply) Notes.SetBounds(0, y, width, notes);
        return y + notes + S(12);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        Draw.Text(e.Graphics, "运行间隔", F.Caption, _intervalLabel, P.Muted);
    }
}

internal sealed record HistoryEntry(DateTimeOffset Started, DateTimeOffset Updated, string Status, TaskStatusKind Kind,
    long Confirmed, long Target, long Requests, int Unknown, bool Current);

/// <summary>运行记录：当前轮 + 最多 20 条历史，新的在上。</summary>
internal sealed class HistoryPage : UiControl, IHeightForWidth
{
    private IReadOnlyList<HistoryEntry> _items = [];
    public void SetItems(IReadOnlyList<HistoryEntry> items)
    {
        if (items.SequenceEqual(_items)) return;
        _items = items; Parent?.PerformLayout(); Invalidate();
    }
    private int RowHeight => S(64);
    public int HeightForWidth(int width) => Math.Max(S(120), _items.Count * RowHeight + S(48));
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_items.Count == 0)
        {
            Draw.Text(g, "还没有运行记录。完成、停止或重置的轮次会显示在这里。", F.Body, new Rectangle(0, S(20), Width, S(60)), P.Muted,
                Draw.Wrap | TextFormatFlags.HorizontalCenter);
            return;
        }
        using var line = new Pen(P.Line);
        var y = 0;
        foreach (var item in _items)
        {
            var row = new Rectangle(0, y, Width, RowHeight);
            var date = item.Started.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            Draw.Text(g, date, F.NumberBold, new Rectangle(0, y + S(12), S(120), S(22)), P.Ink);
            Draw.Text(g, (item.Current ? "当前轮，用时 " : "用时 ") + Format.Duration(item.Updated - item.Started),
                F.Caption, new Rectangle(0, y + S(36), S(170), S(18)), P.Muted);
            var pillSize = Draw.PillSize(item.Status, F.Caption);
            var (fore, back) = Draw.StatusColors(item.Kind);
            Draw.Pill(g, new Rectangle(S(176), y + (RowHeight - pillSize.Height) / 2, pillSize.Width, pillSize.Height), item.Status, F.Caption, fore, back);
            var amount = $"{Format.Compact(item.Confirmed)} / {Format.Compact(item.Target)}";
            var amountWidth = Draw.Measure(amount, F.NumberBold).Width;
            Draw.Text(g, amount, F.NumberBold, new Rectangle(row.Right - amountWidth - S(2), y + S(12), amountWidth + S(2), S(22)), P.Ink);
            var detail = $"{item.Requests} 笔请求" + (item.Unknown > 0 ? $"，{item.Unknown} 笔未知" : "");
            var detailWidth = Draw.Measure(detail, F.Caption).Width;
            Draw.Text(g, detail, F.Caption, new Rectangle(row.Right - detailWidth - S(2), y + S(36), detailWidth + S(2), S(18)), item.Unknown > 0 ? P.Warn : P.Muted);
            y += RowHeight;
            g.DrawLine(line, 0, y - 1, Width, y - 1);
        }
        Draw.Text(g, "最多保留 20 条历史记录；重置会把当前轮归档到这里。", F.Caption, new Rectangle(0, y + S(14), Width, S(20)), P.Subtle);
    }

    public static IReadOnlyList<HistoryEntry> From(TaskRuntime row)
    {
        var document = row.Session.Recovery.Document;
        var list = new List<HistoryEntry>();
        if (document?.CurrentRun is { } current) list.Add(Entry(current, row.Session.CurrentRun.Parameters is not null ? row.Session.CurrentRun : current.Snapshot, true));
        if (document?.History is { } history)
            list.AddRange(history.OrderByDescending(record => record.StartedUtc).Select(record => Entry(record, record.Snapshot, false)));
        return list;
    }

    private static HistoryEntry Entry(RunRecord record, RunSnapshot snapshot, bool current)
    {
        var (status, kind) = snapshot.State switch
        {
            RunState.Completed => (snapshot.EndReason == RunEndReason.BudgetRemainder ? "已完成（余量收尾）" : "已完成", TaskStatusKind.Success),
            RunState.Stopped => ("已停止", TaskStatusKind.Idle),
            RunState.Faulted => ("异常停止", TaskStatusKind.Danger),
            RunState.Running => ("运行中", TaskStatusKind.Burning),
            RunState.Paused => ("已暂停", TaskStatusKind.Paused),
            RunState.AwaitingReview => ("待核查", TaskStatusKind.Warning),
            RunState.Stopping => ("停止中", TaskStatusKind.Busy),
            _ => ("未运行", TaskStatusKind.Idle)
        };
        if (snapshot.ReviewRequired && kind != TaskStatusKind.Burning) (status, kind) = ("待核查", TaskStatusKind.Warning);
        var updated = current && snapshot.State is RunState.Running or RunState.Paused ? DateTimeOffset.UtcNow : record.UpdatedUtc;
        return new(record.StartedUtc, updated, status, kind, snapshot.ConfirmedUsage.TotalTokens, snapshot.Parameters?.TargetTokens ?? 0,
            snapshot.CompletedRequests, snapshot.UnknownUsageRequests, current);
    }
}
