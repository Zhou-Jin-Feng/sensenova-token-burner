namespace SenseNova.TokenBurner.Desktop.Ui;

internal enum ButtonKind { Secondary, Primary, Ghost, Danger }

/// <summary>自绘按钮；仍继承 Button，保留键盘、无障碍和 PerformClick 行为。</summary>
internal sealed class UiButton : Button
{
    private bool _hover;
    private bool _pressed;
    private ButtonKind _kind;
    private string? _glyph;
    private bool _compact;

    public UiButton(string text, ButtonKind kind = ButtonKind.Secondary, string? glyph = null, string? name = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        Text = text; _kind = kind; _glyph = glyph;
        Name = name ?? "";
        AccessibleName = string.IsNullOrEmpty(text) ? name : text;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseMnemonic = false;
        Theme.Changed += OnThemeChanged;
    }

    public ButtonKind Kind { get => _kind; set { _kind = value; Invalidate(); } }
    public string? Glyph { get => _glyph; set { _glyph = value; Invalidate(); } }
    /// <summary>紧凑按钮高 28，常规 32。</summary>
    public bool Compact { get => _compact; set { _compact = value; Invalidate(); } }
    public int PreferredHeight => Theme.S(_compact ? 28 : 32);

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(new Action(Invalidate)); else Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var fonts = Theme.Fonts;
        var hasText = !string.IsNullOrEmpty(Text);
        var height = PreferredHeight;
        if (!hasText) return new Size(height, height);
        var width = Draw.Measure(Text, fonts.BodyBold).Width + Theme.S(_compact ? 20 : 28);
        if (_glyph is not null) width += Theme.S(22);
        return new Size(width, height);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs mevent) { base.OnMouseDown(mevent); if (mevent.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs mevent) { base.OnMouseUp(mevent); _pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var p = Theme.Current;
        var fonts = Theme.Fonts;
        var g = pevent.Graphics;
        var outside = Parent?.BackColor ?? p.Surface;
        g.Clear(outside);
        Draw.Smooth(g);
        var bounds = new Rectangle(0, 0, Width, Height);
        var radius = Theme.Sf(6);
        Color fill, fore, border;
        switch (_kind)
        {
            case ButtonKind.Primary:
                fill = _pressed ? p.PrimaryPressed : _hover ? p.PrimaryHover : p.Primary; fore = p.OnPrimary; border = fill; break;
            case ButtonKind.Danger:
                fill = _pressed ? Draw.Mix(p.DangerSoft, p.Danger, 0.18f) : _hover ? p.DangerSoft : outside;
                fore = p.Danger; border = _hover ? p.DangerSoft : Draw.Mix(p.Danger, outside, 0.6f); break;
            case ButtonKind.Ghost:
                fill = _pressed ? p.SurfaceSelected : _hover ? p.SurfaceHover : outside; fore = p.Ink; border = fill; break;
            default:
                fill = _pressed ? p.SurfaceSelected : _hover ? p.SurfaceHover : p.Surface; fore = p.Ink; border = _hover ? p.LineStrong : p.Line; break;
        }
        if (!Enabled)
        {
            fill = _kind == ButtonKind.Primary ? Draw.Mix(p.Primary, outside, 0.6f) : _kind == ButtonKind.Ghost ? outside : Draw.Mix(fill, outside, 0.4f);
            fore = _kind == ButtonKind.Primary ? Draw.Mix(p.OnPrimary, fill, 0.25f) : p.Subtle;
            border = _kind == ButtonKind.Ghost ? outside : Draw.Mix(border, outside, 0.5f);
        }
        Draw.Fill(g, bounds, radius, fill);
        if (border != fill) Draw.Stroke(g, bounds, radius, border);
        if (Focused && ShowFocusCues)
            Draw.Stroke(g, bounds, radius, p.Primary, Math.Max(1.5f, Theme.Sf(2)));

        var hasText = !string.IsNullOrEmpty(Text);
        if (!hasText)
        {
            if (_glyph is not null) Draw.Glyph(g, _glyph, fonts.Icon, bounds, fore);
            return;
        }
        var textSize = Draw.Measure(Text, fonts.BodyBold);
        var glyphWidth = _glyph is null ? 0 : Theme.S(22);
        var x = (Width - textSize.Width - glyphWidth) / 2;
        if (_glyph is not null)
            Draw.Glyph(g, _glyph, fonts.IconSmall, new Rectangle(x, 0, Theme.S(16), Height), fore);
        Draw.Text(g, Text, fonts.BodyBold, new Rectangle(x + glyphWidth, 0, Width - x - glyphWidth, Height), fore);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}

internal enum ToggleStyle { Switch, Check }

/// <summary>开关或复选框；继承 CheckBox 保留 Checked 语义和键盘空格切换。</summary>
internal sealed class UiToggle : CheckBox
{
    private bool _hover;
    public UiToggle(string text, ToggleStyle style = ToggleStyle.Switch, string? name = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        Text = text; ToggleStyle = style; Name = name ?? ""; AccessibleName = text; AutoSize = false;
        Cursor = Cursors.Hand; UseMnemonic = false;
        Theme.Changed += OnThemeChanged;
    }
    public ToggleStyle ToggleStyle { get; }
    /// <summary>复选框的“部分选中”外观（全选用）。</summary>
    public bool Mixed { get; set { field = value; Invalidate(); } }
    private void OnThemeChanged() { if (IsDisposed) return; if (InvokeRequired) BeginInvoke(new Action(Invalidate)); else Invalidate(); }
    private int BoxWidth => ToggleStyle == ToggleStyle.Switch ? Theme.S(34) : Theme.S(16);
    private int BoxHeight => ToggleStyle == ToggleStyle.Switch ? Theme.S(18) : Theme.S(16);
    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = string.IsNullOrEmpty(Text) ? 0 : Draw.Measure(Text, Theme.Fonts.Body).Width + Theme.S(8);
        return new Size(BoxWidth + text + Theme.S(2), Theme.S(24));
    }
    protected override void OnMouseEnter(EventArgs eventargs) { base.OnMouseEnter(eventargs); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs eventargs) { base.OnMouseLeave(eventargs); _hover = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs pevent)
    {
        var p = Theme.Current;
        var g = pevent.Graphics;
        var outside = Parent?.BackColor ?? p.Surface;
        g.Clear(outside);
        Draw.Smooth(g);
        var box = new Rectangle(Theme.S(1), (Height - BoxHeight) / 2, BoxWidth, BoxHeight);
        var on = Checked || Mixed;
        var accent = Enabled ? p.Primary : Draw.Mix(p.Primary, outside, 0.6f);
        if (ToggleStyle == ToggleStyle.Switch)
        {
            Draw.Fill(g, box, box.Height / 2f, on ? accent : Enabled ? (_hover ? p.Subtle : p.LineStrong) : p.Line);
            var knob = box.Height - Theme.S(6);
            var knobX = on ? box.Right - knob - Theme.S(3) : box.X + Theme.S(3);
            using var brush = new SolidBrush(on ? p.OnPrimary : p.Surface);
            g.FillEllipse(brush, knobX, box.Y + Theme.S(3), knob, knob);
        }
        else
        {
            if (on) Draw.Fill(g, box, Theme.Sf(4), accent);
            else
            {
                Draw.Fill(g, box, Theme.Sf(4), p.Input);
                Draw.Stroke(g, box, Theme.Sf(4), Enabled ? (_hover ? p.Primary : p.LineStrong) : p.Line, Math.Max(1, Theme.Sf(1.25f)));
            }
            using var pen = new Pen(p.OnPrimary, Math.Max(1.5f, Theme.Sf(1.75f))) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            if (Mixed && !Checked)
                g.DrawLine(pen, box.X + box.Width * 0.28f, box.Y + box.Height / 2f, box.Right - box.Width * 0.28f, box.Y + box.Height / 2f);
            else if (Checked)
                g.DrawLines(pen, [new PointF(box.X + box.Width * 0.24f, box.Y + box.Height * 0.53f),
                    new PointF(box.X + box.Width * 0.43f, box.Y + box.Height * 0.71f), new PointF(box.X + box.Width * 0.77f, box.Y + box.Height * 0.32f)]);
        }
        if (Focused && ShowFocusCues) Draw.Stroke(g, Rectangle.Inflate(box, Theme.S(2), Theme.S(2)), box.Height / 2f + Theme.S(2), p.Primary, Theme.Sf(1.5f));
        if (!string.IsNullOrEmpty(Text))
            Draw.Text(g, Text, Theme.Fonts.Body, new Rectangle(box.Right + Theme.S(8), 0, Width - box.Right - Theme.S(8), Height), Enabled ? p.Ink : p.Subtle);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
