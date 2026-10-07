using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SenseNova.TokenBurner.Desktop.Ui;

/// <summary>圆角输入框，内部是无边框原生 TextBox（输入法、复制粘贴、密码遮蔽保持系统行为）。</summary>
internal sealed class UiInput : UiControl
{
    private readonly UiButton? _reveal;
    private bool _hover;

    public UiInput(string? placeholder = null, bool password = false, int maxLength = 200, string? accessibleName = null)
    {
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None, PlaceholderText = placeholder ?? "", MaxLength = maxLength,
            UseSystemPasswordChar = password, AccessibleName = accessibleName ?? placeholder
        };
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Box.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        Box.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        Box.MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        Controls.Add(Box);
        if (password)
        {
            _reveal = new UiButton("", ButtonKind.Ghost, Glyphs.Eye) { Compact = true, TabStop = false, AccessibleName = "显示或隐藏 API key" };
            _reveal.Click += (_, _) => { Box.UseSystemPasswordChar = !Box.UseSystemPasswordChar; Box.Focus(); };
            Controls.Add(_reveal);
        }
        Cursor = Cursors.IBeam;
        ApplyColors();
    }

    public TextBox Box { get; }
    [AllowNull] public override string Text { get => Box.Text; set => Box.Text = value ?? ""; }
    public string? Suffix { get; set { field = value; PerformLayout(); Invalidate(); } }
    public int PreferredHeight => S(34);

    protected override void OnThemeChanged() { ApplyColors(); base.OnThemeChanged(); }
    private void ApplyColors()
    {
        Box.BackColor = P.Input; Box.ForeColor = P.Ink; Box.Font = F.Body;
        Theme.ApplyNativeTheme(Box);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.ApplyNativeTheme(Box); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Box.Enabled = Enabled; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Box.Focus(); }

    private int SuffixWidth => string.IsNullOrEmpty(Suffix) ? 0 : Draw.Measure(Suffix, F.Caption).Width + S(10);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var right = S(10) + SuffixWidth;
        if (_reveal is not null)
        {
            var size = S(26);
            _reveal.SetBounds(Width - size - S(4), (Height - size) / 2, size, size);
            right += size;
        }
        var height = Box.PreferredHeight;
        Box.SetBounds(S(10), (Height - height) / 2, Math.Max(10, Width - S(10) - right), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var outside = Parent?.BackColor ?? P.Surface;
        Draw.Panel(e.Graphics, ClientRectangle, Theme.Sf(6), outside, Enabled ? P.Input : Draw.Mix(P.Input, outside, 0.5f), null);
        var focused = Box.Focused;
        Draw.Stroke(e.Graphics, ClientRectangle, Theme.Sf(6), focused ? P.Primary : _hover && Enabled ? P.Subtle : P.LineStrong,
            focused ? Math.Max(1.5f, Theme.Sf(1.5f)) : 1);
        if (!string.IsNullOrEmpty(Suffix))
        {
            var right = Width - S(10) - (_reveal is null ? 0 : S(26));
            var width = SuffixWidth - S(10);
            Draw.Text(e.Graphics, Suffix, F.Caption, new Rectangle(right - width, 0, width, Height), P.Muted);
        }
    }
}

/// <summary>数字输入：文本框 + 减/加按钮，失焦或回车时校验，非法值回退。</summary>
internal sealed class UiNumber : UiControl
{
    private readonly TextBox _box = new() { BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Left };
    private readonly UiButton _minus = new("", ButtonKind.Ghost, Glyphs.Minus) { Compact = true, TabStop = false, AccessibleName = "减少" };
    private readonly UiButton _plus = new("", ButtonKind.Ghost, Glyphs.Add) { Compact = true, TabStop = false, AccessibleName = "增加" };
    private decimal _value;
    private bool _hover;

    public UiNumber(decimal minimum, decimal maximum, decimal value, int decimals = 0, decimal increment = 1, string? suffix = null, string? accessibleName = null)
    {
        Minimum = minimum; Maximum = maximum; DecimalPlaces = decimals; Increment = increment; Suffix = suffix;
        _box.AccessibleName = accessibleName;
        _value = Math.Clamp(value, minimum, maximum);
        _box.Text = Display(_value);
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => { Commit(); Invalidate(); };
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Up) { Value += Increment; e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Value -= Increment; e.Handled = true; }
        };
        _box.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        _box.MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        _box.TextChanged += (_, _) => { if (!_formatting) Edited?.Invoke(this, EventArgs.Empty); };
        _minus.Click += (_, _) => Value -= Increment;
        _plus.Click += (_, _) => Value += Increment;
        Controls.AddRange([_box, _minus, _plus]);
        ApplyColors();
    }

    public decimal Minimum { get; set; }
    public decimal Maximum { get; set; }
    public int DecimalPlaces { get; }
    public decimal Increment { get; set; }
    public bool Thousands { get; set { field = value; _box.Text = Display(_value); } }
    public string? Suffix { get; }
    public event EventHandler? ValueChanged;
    /// <summary>用户正在输入（尚未提交）时触发，用于即时显示“未保存”。</summary>
    public event EventHandler? Edited;
    public int PreferredHeight => S(34);
    private bool _formatting;

    /// <summary>当前输入框里的值（不提交、不改写文字）；无法解析时返回已提交的值。</summary>
    public decimal PendingValue
    {
        get
        {
            var raw = _box.Text.Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim();
            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Round(Math.Clamp(parsed, Minimum, Maximum), DecimalPlaces) : _value;
        }
    }

    public decimal Value
    {
        get => _value;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, Minimum, Maximum), DecimalPlaces);
            var changed = clamped != _value;
            _value = clamped;
            var text = Display(clamped);
            if (_box.Text != text)
            {
                _formatting = true;
                try { _box.Text = text; } finally { _formatting = false; }
            }
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>把正在输入的文字立即提交（保存按钮在失焦前点击时使用）。</summary>
    public void Commit()
    {
        var raw = _box.Text.Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim();
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) Value = parsed;
        else
        {
            _formatting = true;
            try { _box.Text = Display(_value); } finally { _formatting = false; }
        }
    }

    private string Display(decimal value) => value.ToString(
        (Thousands ? "#,0" : "0") + (DecimalPlaces > 0 ? "." + new string('#', DecimalPlaces) : ""), CultureInfo.InvariantCulture);

    protected override void OnThemeChanged() { ApplyColors(); base.OnThemeChanged(); }
    private void ApplyColors() { _box.BackColor = P.Input; _box.ForeColor = P.Ink; _box.Font = F.Number; }
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _box.Enabled = _minus.Enabled = _plus.Enabled = Enabled;
        Invalidate();
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    private int SuffixWidth => string.IsNullOrEmpty(Suffix) ? 0 : Draw.Measure(Suffix, F.Caption).Width + S(8);
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var button = S(26);
        _plus.SetBounds(Width - button - S(4), (Height - button) / 2, button, button);
        _minus.SetBounds(_plus.Left - button - S(2), (Height - button) / 2, button, button);
        var height = _box.PreferredHeight;
        _box.SetBounds(S(10), (Height - height) / 2, Math.Max(10, _minus.Left - S(10) - SuffixWidth - S(4)), height);
        _box.Font = F.Number;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var outside = Parent?.BackColor ?? P.Surface;
        Draw.Panel(e.Graphics, ClientRectangle, Theme.Sf(6), outside, Enabled ? P.Input : Draw.Mix(P.Input, outside, 0.5f), null);
        var focused = _box.Focused;
        Draw.Stroke(e.Graphics, ClientRectangle, Theme.Sf(6), focused ? P.Primary : _hover && Enabled ? P.Subtle : P.LineStrong,
            focused ? Math.Max(1.5f, Theme.Sf(1.5f)) : 1);
        if (!string.IsNullOrEmpty(Suffix))
            Draw.Text(e.Graphics, Suffix, F.Caption, new Rectangle(_minus.Left - SuffixWidth, 0, SuffixWidth, Height), Enabled ? P.Muted : P.Subtle);
    }
}

/// <summary>目标比例滑条：0–95，预设刻度，可键盘调整。</summary>
internal sealed class UiSlider : UiControl
{
    private int _value;
    private bool _dragging;
    private bool _hover;
    public UiSlider(int minimum, int maximum, int value, params int[] ticks)
    {
        Minimum = minimum; Maximum = maximum; _value = Math.Clamp(value, minimum, maximum); Ticks = ticks;
        TabStop = true; AccessibleRole = AccessibleRole.Slider; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.Selectable, true);
    }
    public int Minimum { get; }
    public int Maximum { get; }
    public int[] Ticks { get; }
    public event EventHandler? ValueChanged;
    public int PreferredHeight => S(30);
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (clamped == _value) return;
            _value = clamped; AccessibleDescription = clamped + "%"; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private Rectangle Track => new(S(9), Height / 2 - S(2), Width - S(18), S(4));
    private float X(int value) => Track.X + Track.Width * (value - Minimum) / (float)Math.Max(1, Maximum - Minimum);
    private void SetFrom(int x) => Value = Minimum + (int)Math.Round((x - Track.X) / (float)Math.Max(1, Track.Width) * (Maximum - Minimum));
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        Value += e.KeyCode switch { Keys.Left or Keys.Down => -1, Keys.Right or Keys.Up => 1, Keys.PageDown => -5, Keys.PageUp => 5, _ => 0 };
        if (e.KeyCode == Keys.Home) Value = Minimum;
        if (e.KeyCode == Keys.End) Value = Maximum;
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (!Enabled) return; Focus(); _dragging = true; Capture = true; SetFrom(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_dragging) SetFrom(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; Capture = false; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Draw.Smooth(g);
        var track = Track;
        var accent = Enabled ? P.Primary : P.LineStrong;
        Draw.Fill(g, track, track.Height / 2f, P.Track);
        var x = X(_value);
        Draw.Fill(g, new RectangleF(track.X, track.Y, x - track.X, track.Height), track.Height / 2f, accent);
        using (var tick = new SolidBrush(P.LineStrong))
        using (var tickOn = new SolidBrush(Enabled ? P.OnPrimary : P.Surface))
            foreach (var value in Ticks)
            {
                var tx = X(value);
                var size = S(4);
                g.FillEllipse(value <= _value ? tickOn : tick, tx - size / 2f, track.Y + track.Height / 2f - size / 2f, size, size);
            }
        var knob = (_hover || _dragging) && Enabled ? S(18) : S(16);
        var knobRect = new RectangleF(x - knob / 2f, Height / 2f - knob / 2f, knob, knob);
        using (var fill = new SolidBrush(P.Surface)) g.FillEllipse(fill, knobRect);
        using (var pen = new Pen(accent, Math.Max(2, Theme.Sf(2.5f)))) g.DrawEllipse(pen, knobRect);
        if (Focused && ShowFocusCues)
            using (var focus = new Pen(Color.FromArgb(90, P.Primary), Math.Max(2, Theme.Sf(3))))
                g.DrawEllipse(focus, RectangleF.Inflate(knobRect, S(3), S(3)));
    }
}

internal enum SegmentedStyle { Tabs, Pills }

/// <summary>分段选择：详情页签（下划线）或预设/主题选择（胶囊）。</summary>
internal sealed class UiSegmented : UiControl
{
    private int _selected;
    private int _hover = -1;
    public UiSegmented(SegmentedStyle style, params string[] items)
    {
        Style = style; Items = items; TabStop = true; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.Selectable, true);
        AccessibleRole = style == SegmentedStyle.Tabs ? AccessibleRole.PageTabList : AccessibleRole.List;
    }
    public SegmentedStyle Style { get; }
    public string[] Items { get; }
    /// <summary>-1 表示没有选中（例如自定义目标时比例预设全部不高亮）。</summary>
    public int SelectedIndex
    {
        get => _selected;
        set { if (value == _selected) return; _selected = value; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
    }
    public void SetSelectedSilently(int index) { _selected = index; Invalidate(); }
    public event EventHandler? SelectedIndexChanged;
    public int PreferredHeight => Style == SegmentedStyle.Tabs ? S(38) : S(32);
    public int PreferredWidth => Widths().Sum() + (Style == SegmentedStyle.Pills ? S(6) : 0);
    private int[] Widths() => Items.Select(item => Draw.Measure(item, Style == SegmentedStyle.Tabs ? F.BodyBold : F.Body).Width + S(Style == SegmentedStyle.Tabs ? 28 : 24)).ToArray();
    private Rectangle[] Slots()
    {
        var widths = Widths();
        var slots = new Rectangle[Items.Length];
        if (Style == SegmentedStyle.Pills)
        {
            var available = Width - S(6);
            var total = widths.Sum();
            var x = S(3);
            for (var i = 0; i < widths.Length; i++)
            {
                var w = i == widths.Length - 1 ? Width - S(3) - x : (int)((long)widths[i] * available / Math.Max(1, total));
                slots[i] = new Rectangle(x, S(3), w, Height - S(6)); x += w;
            }
        }
        else
        {
            var x = 0;
            for (var i = 0; i < widths.Length; i++) { slots[i] = new Rectangle(x, 0, widths[i], Height); x += widths[i]; }
        }
        return slots;
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) SelectedIndex = Math.Max(0, _selected - 1);
        if (e.KeyCode == Keys.Right) SelectedIndex = Math.Min(Items.Length - 1, _selected + 1);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = Array.FindIndex(Slots(), slot => slot.Contains(e.Location));
        if (index != _hover) { _hover = index; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled) return;
        var index = Array.FindIndex(Slots(), slot => slot.Contains(e.Location));
        if (index >= 0) { Focus(); SelectedIndex = index; }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Draw.Smooth(g);
        var slots = Slots();
        if (Style == SegmentedStyle.Pills)
        {
            Draw.Fill(g, ClientRectangle, Theme.Sf(7), P.SurfaceAlt);
            Draw.Stroke(g, ClientRectangle, Theme.Sf(7), P.Line);
        }
        else
            using (var line = new Pen(P.Line)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        for (var i = 0; i < Items.Length; i++)
        {
            var slot = slots[i];
            var selected = i == _selected;
            Color fore;
            if (Style == SegmentedStyle.Pills)
            {
                if (selected)
                {
                    Draw.Fill(g, slot, Theme.Sf(5), P.Surface);
                    Draw.Stroke(g, slot, Theme.Sf(5), P.LineStrong);
                }
                else if (i == _hover && Enabled) Draw.Fill(g, slot, Theme.Sf(5), P.SurfaceHover);
                fore = !Enabled ? P.Subtle : selected ? P.Ink : P.Muted;
                Draw.Text(g, Items[i], selected ? F.BodyBold : F.Body, slot, fore,
                    Draw.Single | TextFormatFlags.HorizontalCenter);
            }
            else
            {
                fore = !Enabled ? P.Subtle : selected ? P.Ink : i == _hover ? P.Ink : P.Muted;
                Draw.Text(g, Items[i], F.BodyBold, slot, fore, Draw.Single | TextFormatFlags.HorizontalCenter);
                if (selected) Draw.Fill(g, new Rectangle(slot.X + S(12), Height - S(3), slot.Width - S(24), S(3)), S(1), P.Primary);
            }
            if (selected && Focused && ShowFocusCues) Draw.Stroke(g, Rectangle.Inflate(slot, -S(1), -S(1)), Theme.Sf(5), P.Primary, Theme.Sf(1.5f));
        }
    }
}
