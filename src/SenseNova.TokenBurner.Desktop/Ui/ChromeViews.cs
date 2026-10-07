using SenseNova.TokenBurner.Desktop.ViewModels;

namespace SenseNova.TokenBurner.Desktop.Ui;

/// <summary>顶栏：品牌标识、环境标记、主题切换和设置。</summary>
internal sealed class HeaderBar : UiControl
{
    private readonly bool _mock;
    public HeaderBar(bool mock)
    {
        _mock = mock;
        ThemeButton = new UiButton("", ButtonKind.Ghost, Glyphs.Moon, "ThemeToggle") { AccessibleName = "切换深浅色" };
        SettingsButton = new UiButton("", ButtonKind.Ghost, Glyphs.Settings, "Settings") { AccessibleName = "设置" };
        Controls.AddRange([ThemeButton, SettingsButton]);
    }
    public UiButton ThemeButton { get; }
    public UiButton SettingsButton { get; }
    public bool Burning { get; set { if (field == value) return; field = value; Invalidate(); } }

    protected override void OnThemeChanged()
    {
        ThemeButton.Glyph = P.IsDark ? Glyphs.Sun : Glyphs.Moon;
        base.OnThemeChanged();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var size = S(34);
        SettingsButton.SetBounds(Width - size, (Height - size) / 2, size, size);
        ThemeButton.SetBounds(SettingsButton.Left - size - S(4), (Height - size) / 2, size, size);
        ThemeButton.Glyph = P.IsDark ? Glyphs.Sun : Glyphs.Moon;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var mark = S(32);
        BrandMark.Paint(g, new RectangleF(0, (Height - mark) / 2f, mark, mark), Burning);
        var x = mark + S(12);
        var word = "Token Burner";
        var wordSize = Draw.Measure(word, F.Brand);
        Draw.Text(g, word, F.Brand, new Rectangle(x, 0, wordSize.Width + S(2), Height), P.Ink);
        x += wordSize.Width + S(8);
        var brand = "SenseNova";
        var brandSize = Draw.Measure(brand, F.BrandLight);
        Draw.Text(g, brand, F.BrandLight, new Rectangle(x, 0, brandSize.Width + S(2), Height), P.Muted);
        x += brandSize.Width + S(16);
        var label = _mock ? "模拟环境，不产生真实消耗" : "官方 API";
        var size = Draw.PillSize(label, F.Caption);
        var pill = new Rectangle(x, (Height - size.Height) / 2, size.Width, size.Height);
        if (_mock) Draw.Pill(g, pill, label, F.Caption, P.Warn, P.WarnSoft);
        else Draw.Pill(g, pill, label, F.Caption, P.Primary, P.PrimarySoft);
    }
}

internal sealed record OverviewData(long Confirmed, long Target, int Burning, int Paused, int Review, int Total,
    int ActiveRequests, int MaximumRequests, int PerKey, string? NextTime, string? NextName, string? Batch);

/// <summary>全局状态条：一条面板分四格，不做卡片堆叠。在途请求用槽位点表示共享并发。</summary>
internal sealed class OverviewStrip : UiControl
{
    private OverviewData _data = new(0, 0, 0, 0, 0, 0, 0, 3, 3, null, null, null);
    private Rectangle _pulseArea;
    /// <summary>呼吸相位；只重画运行中圆点所在的小区域。</summary>
    public float Pulse { get; set { field = value; if (_data.Burning > 0 && !_pulseArea.IsEmpty) Invalidate(_pulseArea); } }
    public void SetData(OverviewData data)
    {
        if (data == _data) return;
        _data = data;
        AccessibleDescription = $"本轮已确认 {Format.Exact(data.Confirmed)} tokens，运行中 {data.Burning}，在途请求 {data.ActiveRequests}/{data.MaximumRequests}";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintPanel(g, Theme.Sf(12), P.Surface, P.Line);
        var cell = Width / 4;
        using (var line = new Pen(P.Line))
            for (var i = 1; i < 4; i++) g.DrawLine(line, cell * i, S(14), cell * i, Height - S(14));
        var pad = S(18);
        Rectangle Cell(int index) => new(cell * index + pad, S(12), cell - pad * 2, Height - S(24));

        // 1. 本轮已确认（全部任务）
        var c1 = Cell(0);
        Caption(g, c1, _data.Batch ?? "本轮已确认");
        var value = Format.Compact(_data.Confirmed);
        var valueSize = Draw.Measure(value, F.NumberLarge);
        Draw.Text(g, value, F.NumberLarge, new Rectangle(c1.X, c1.Y + S(16), valueSize.Width + S(2), S(30)), P.Ink);
        if (_data.Target > 0)
            Draw.Text(g, "/ " + Format.Compact(_data.Target), F.Number, new Rectangle(c1.X + valueSize.Width + S(6), c1.Y + S(22), c1.Width - valueSize.Width - S(6), S(22)), P.Muted);
        var fraction = _data.Target <= 0 ? 0 : (double)_data.Confirmed / _data.Target;
        Draw.Meter(g, new Rectangle(c1.X, c1.Bottom - S(5), c1.Width, S(4)), fraction, 0, _data.Burning > 0 ? P.Ember : P.Primary, P.Track);

        // 2. 运行状态
        var c2 = Cell(1);
        Caption(g, c2, "运行中");
        var burning = _data.Burning.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var burningSize = Draw.Measure(burning, F.NumberLarge);
        if (_data.Burning > 0)
        {
            var dot = S(8);
            using var halo = new SolidBrush(Color.FromArgb((int)(40 + 60 * Pulse), P.Ember));
            using var core = new SolidBrush(P.Ember);
            var cx = c2.X + dot / 2f; var cy = c2.Y + S(31);
            _pulseArea = Rectangle.Inflate(new Rectangle((int)(cx - dot), (int)(cy - dot), dot * 2, dot * 2), S(2), S(2));
            Draw.Smooth(g);
            g.FillEllipse(halo, cx - dot, cy - dot, dot * 2, dot * 2);
            g.FillEllipse(core, cx - dot / 2f, cy - dot / 2f, dot, dot);
        }
        var offset = _data.Burning > 0 ? S(18) : 0;
        Draw.Text(g, burning, F.NumberLarge, new Rectangle(c2.X + offset, c2.Y + S(16), burningSize.Width + S(2), S(30)), _data.Burning > 0 ? P.Ember : P.Ink);
        var details = new List<(string Text, Color Color)> { ($"暂停 {_data.Paused}", P.Muted) };
        if (_data.Review > 0) details.Add(($"待核查 {_data.Review}", P.Warn));
        details.Add(($"共 {_data.Total}", P.Muted));
        var x = c2.X + offset + burningSize.Width + S(10);
        foreach (var (text, color) in details)
        {
            var width = Draw.Measure(text, F.Caption).Width;
            if (x + width > c2.Right) break;
            Draw.Text(g, text, F.Caption, new Rectangle(x, c2.Y + S(22), width + S(2), S(22)), color);
            x += width + S(10);
        }

        // 3. 在途请求：槽位点
        var c3 = Cell(2);
        Caption(g, c3, "在途请求");
        var requests = $"{_data.ActiveRequests}/{_data.MaximumRequests}";
        var requestSize = Draw.Measure(requests, F.NumberLarge);
        Draw.Text(g, requests, F.NumberLarge, new Rectangle(c3.X, c3.Y + S(16), requestSize.Width + S(2), S(30)), P.Ink);
        var pip = S(8); var gap = S(5);
        var px = c3.X + requestSize.Width + S(12);
        Draw.Smooth(g);
        for (var i = 0; i < _data.MaximumRequests && px + pip <= c3.Right; i++, px += pip + gap)
        {
            var rect = new RectangleF(px, c3.Y + S(27), pip, pip);
            if (i < _data.ActiveRequests)
            {
                using var brush = new SolidBrush(P.Ember); g.FillEllipse(brush, rect);
            }
            else
            {
                using var pen = new Pen(P.LineStrong, Math.Max(1, Theme.Sf(1.25f))); g.DrawEllipse(pen, rect);
            }
        }
        Draw.Text(g, $"单个 key 最多 {_data.PerKey} 笔", F.Caption, new Rectangle(c3.X, c3.Bottom - S(18), c3.Width, S(18)), P.Muted);

        // 4. 下一次定时
        var c4 = Cell(3);
        Caption(g, c4, "下一次定时");
        if (_data.NextTime is null)
            Draw.Text(g, "未启用", F.Body, new Rectangle(c4.X, c4.Y + S(18), c4.Width, S(28)), P.Muted);
        else
        {
            var timeSize = Draw.Measure(_data.NextTime, F.NumberLarge);
            Draw.Text(g, _data.NextTime, F.NumberLarge, new Rectangle(c4.X, c4.Y + S(16), Math.Min(c4.Width, timeSize.Width + S(2)), S(30)), P.Ink);
            Draw.Text(g, _data.NextName, F.Caption, new Rectangle(c4.X, c4.Bottom - S(18), c4.Width, S(18)), P.Muted);
        }
    }

    private static void Caption(Graphics g, Rectangle cell, string text)
        => Draw.Text(g, text, F.Caption, new Rectangle(cell.X, cell.Y - S(2), cell.Width, S(18)), P.Muted);
}

/// <summary>底部状态栏：最近一条通知（带级别图标和时间）和“动态”入口。</summary>
internal sealed class StatusLine : UiControl
{
    private PanelNotice? _notice;
    public StatusLine()
    {
        ActivityButton = new UiButton("动态", ButtonKind.Ghost, Glyphs.History, "Activity") { Compact = true };
        Controls.Add(ActivityButton);
    }
    public UiButton ActivityButton { get; }
    public string Text2 => _notice?.Text ?? "";
    public void SetNotice(PanelNotice notice) { _notice = notice; AccessibleName = notice.Text; Invalidate(); }
    public void SetActivityCount(int count)
    {
        var text = count > 0 ? $"动态 {count}" : "动态";
        if (ActivityButton.Text != text) { ActivityButton.Text = text; PerformLayout(); }
    }
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var size = ActivityButton.GetPreferredSize(Size.Empty);
        ActivityButton.SetBounds(Width - size.Width, (Height - size.Height) / 2, size.Width, size.Height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_notice is null) return;
        var (fore, _, glyph) = Draw.NoticeColors(_notice.Level);
        Draw.Glyph(g, glyph, F.Icon, new Rectangle(S(2), 0, S(18), Height), fore);
        var time = _notice.Time.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var timeWidth = Draw.Measure(time, F.NumberCaption).Width;
        var right = ActivityButton.Left - S(12);
        Draw.Text(g, time, F.NumberCaption, new Rectangle(right - timeWidth, 0, timeWidth + S(2), Height), P.Subtle);
        Draw.Text(g, _notice.Text, F.Body, new Rectangle(S(28), 0, right - timeWidth - S(40), Height), P.Ink);
    }
}

/// <summary>动态记录列表（仅本次运行内存中，最多 100 条）。</summary>
internal sealed class ActivityList : UiControl
{
    private IReadOnlyList<PanelNotice> _items = [];
    private int _offset;
    public void SetItems(IReadOnlyList<PanelNotice> items) { _items = items; _offset = 0; Invalidate(); }
    private int RowHeight(PanelNotice notice, int width) => Math.Max(S(44), Draw.Measure(notice.Text, F.Body, width - S(110), true).Height + S(20));
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var total = _items.Sum(item => RowHeight(item, Width));
        _offset = Math.Clamp(_offset - e.Delta * S(48) / 120, 0, Math.Max(0, total - Height));
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(P.Surface);
        if (_items.Count == 0)
        {
            Draw.Text(g, "暂无动态。运行、暂停、重置和验证结果会记录在这里。", F.Body, Rectangle.Inflate(ClientRectangle, -S(20), -S(20)), P.Muted,
                Draw.Wrap | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var y = -_offset;
        using var line = new Pen(P.Line);
        foreach (var item in _items)
        {
            var height = RowHeight(item, Width);
            if (y + height > 0 && y < Height)
            {
                var (fore, _, glyph) = Draw.NoticeColors(item.Level);
                Draw.Glyph(g, glyph, F.Icon, new Rectangle(S(14), y + S(12), S(18), S(20)), fore);
                Draw.Text(g, item.Text, F.Body, new Rectangle(S(42), y + S(10), Width - S(110), height - S(16)), P.Ink, Draw.Wrap);
                var time = item.Time.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                Draw.Text(g, time, F.NumberCaption, new Rectangle(Width - S(64), y + S(10), S(56), S(20)), P.Subtle);
                g.DrawLine(line, S(14), y + height - 1, Width - S(14), y + height - 1);
            }
            y += height;
        }
    }
}
