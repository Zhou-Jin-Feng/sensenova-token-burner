namespace SenseNova.TokenBurner.Desktop.Ui;

/// <summary>自绘控件基类：双缓冲、跟随主题重绘，滚轮转交最近的滚动容器。</summary>
internal class UiControl : Control
{
    public UiControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        Theme.Changed += HandleThemeChanged;
    }

    protected static Palette P => Theme.Current;
    protected static FontSet F => Theme.Fonts;
    protected static int S(int value) => Theme.S(value);

    private void HandleThemeChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(HandleThemeChanged)); return; }
        OnThemeChanged();
    }

    protected virtual void OnThemeChanged() => Invalidate();

    /// <summary>先画父容器的背景（含面板投影），再画自身圆角面板，使圆角外与投影无缝衔接。</summary>
    protected void PaintPanel(Graphics graphics, float radius, Color fill, Color? border)
    {
        if (Parent is { } parent)
        {
            var state = graphics.Save();
            try
            {
                graphics.TranslateTransform(-Left, -Top);
                using var args = new PaintEventArgs(graphics, Bounds);
                InvokePaintBackground(parent, args);
                InvokePaint(parent, args);
            }
            finally { graphics.Restore(state); }
        }
        else graphics.Clear(P.Canvas);
        Draw.Smooth(graphics);
        Draw.Fill(graphics, ClientRectangle, radius, fill);
        if (border is { } line) Draw.Stroke(graphics, ClientRectangle, radius, line);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e is HandledMouseEventArgs { Handled: true }) return;
        for (var parent = Parent; parent is not null; parent = parent.Parent)
            if (parent is ScrollHost host) { host.ScrollBy(-e.Delta); break; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= HandleThemeChanged;
        base.Dispose(disposing);
    }
}

/// <summary>可在固定宽度下报告所需高度的内容，用于滚动容器和自适应布局。</summary>
internal interface IHeightForWidth
{
    int HeightForWidth(int width);
}

/// <summary>纵向滚动容器：只放一个内容控件，右侧画细滚动条，不使用原生滚动条。</summary>
internal sealed class ScrollHost : UiControl
{
    private Control? _content;
    private int _offset;
    private bool _dragging;
    private int _dragStartY;
    private int _dragStartOffset;
    private bool _hover;

    public Control? Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            if (_content is not null) Controls.Remove(_content);
            _content = value;
            _offset = 0;
            if (value is not null) Controls.Add(value);
            PerformLayout();
            Invalidate();
        }
    }

    private int Gutter => S(10);
    private int ContentHeight => _content is IHeightForWidth measured ? measured.HeightForWidth(Math.Max(1, Width - Gutter)) : _content?.Height ?? 0;
    private int MaxOffset => Math.Max(0, ContentHeight - Height);

    public void ScrollBy(int delta) => ScrollTo(_offset + delta * S(48) / 120);
    public void ScrollTo(int offset)
    {
        var clamped = Math.Clamp(offset, 0, MaxOffset);
        if (clamped == _offset) return;
        _offset = clamped;
        PerformLayout();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_content is null) return;
        _offset = Math.Clamp(_offset, 0, MaxOffset);
        var height = Math.Max(Height, ContentHeight);
        _content.SetBounds(0, -_offset, Math.Max(1, Width - Gutter), height);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollBy(-e.Delta);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    private Rectangle Thumb()
    {
        var content = ContentHeight;
        if (content <= Height || Height <= 0) return Rectangle.Empty;
        var length = Math.Max(S(32), Height * Height / content);
        var top = (int)((long)(Height - length) * _offset / Math.Max(1, MaxOffset));
        var width = _hover || _dragging ? S(6) : S(4);
        return new Rectangle(Width - Gutter / 2 - width / 2, top + S(2), width, length - S(4));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var thumb = Thumb();
        if (thumb.IsEmpty) return;
        Draw.Smooth(e.Graphics);
        Draw.Fill(e.Graphics, thumb, thumb.Width / 2f, _dragging ? P.Subtle : Draw.Mix(P.LineStrong, P.Subtle, _hover ? 0.4f : 0));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var thumb = Thumb();
        if (thumb.IsEmpty || e.X < Width - Gutter) return;
        if (e.Y < thumb.Top || e.Y > thumb.Bottom) { ScrollTo(_offset + Math.Sign(e.Y - thumb.Top) * Height); return; }
        _dragging = true; _dragStartY = e.Y; _dragStartOffset = _offset; Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = e.X >= Width - Gutter;
        if (hover != _hover) { _hover = hover; Invalidate(); }
        if (!_dragging) return;
        var thumb = Thumb();
        var travel = Math.Max(1, Height - thumb.Height - S(4));
        ScrollTo(_dragStartOffset + (int)((long)(e.Y - _dragStartY) * MaxOffset / travel));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging) { _dragging = false; Capture = false; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover) { _hover = false; Invalidate(); }
    }
}

/// <summary>自动换行的文字块，按宽度计算高度。</summary>
internal sealed class TextBlock : UiControl, IHeightForWidth
{
    private Func<Font> _font = () => Theme.Fonts.Body;
    private Func<Color> _color = () => Theme.Current.Ink;
    public TextBlock() { TabStop = false; }
    public TextBlock(Func<Font> font, Func<Color> color, string text = "") : this() { _font = font; _color = color; Text = text; }
    public void Style(Func<Font> font, Func<Color> color) { _font = font; _color = color; Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Parent?.PerformLayout(); Invalidate(); }
    public int HeightForWidth(int width) => string.IsNullOrEmpty(Text) ? 0 : Draw.Measure(Text, _font(), width, wrap: true).Height;
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        Draw.Text(e.Graphics, Text, _font(), ClientRectangle, Enabled ? _color() : P.Subtle, Draw.Wrap);
    }
}
