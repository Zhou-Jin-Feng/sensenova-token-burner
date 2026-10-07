using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SenseNova.TokenBurner.Desktop.ViewModels;

namespace SenseNova.TokenBurner.Desktop.Ui;

/// <summary>燃烧仪表：270° 弧，实色为已确认，斜纹为在途预留，终点即本轮目标。</summary>
internal sealed class UiGauge : UiControl
{
    public double Confirmed { get; private set; }
    public double Reserved { get; private set; }
    public double? Reference { get; private set; }
    public TaskStatusKind Kind { get; private set; }
    public string Center { get; private set; } = "0%";
    public string Caption { get; private set; } = "";
    public string Detail { get; private set; } = "";
    /// <summary>0–1 的呼吸相位；只在正在消耗时用于余烬光晕。</summary>
    public float Pulse { get; set { if (Math.Abs(field - value) < 0.001f) return; field = value; if (Kind == TaskStatusKind.Burning) Invalidate(); } }

    public void Set(double confirmed, double reserved, double? reference, TaskStatusKind kind, string center, string caption, string detail)
    {
        if (Confirmed == confirmed && Reserved == reserved && Reference == reference && Kind == kind && Center == center
            && Caption == caption && Detail == detail) return;
        Confirmed = confirmed; Reserved = reserved; Reference = reference; Kind = kind; Center = center; Caption = caption; Detail = detail;
        AccessibleName = $"本轮进度 {center}";
        Invalidate();
    }

    private Color FillColor => Kind switch
    {
        TaskStatusKind.Burning => P.Ember,
        TaskStatusKind.Success => P.Ok,
        TaskStatusKind.Warning => P.Warn,
        TaskStatusKind.Danger => P.Danger,
        TaskStatusKind.Paused or TaskStatusKind.Idle => Draw.Mix(P.Primary, P.Surface, 0.15f),
        _ => P.Primary
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Draw.Smooth(g);
        var thickness = Theme.Sf(12);
        var side = Math.Min(Width, (int)(Height * 1.12f)) - S(8);
        var arc = new RectangleF((Width - side) / 2f + thickness / 2, S(4) + thickness / 2, side - thickness, side - thickness);
        const float start = 135f, sweep = 270f;
        using (var track = new Pen(P.Track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(track, arc, start, sweep);

        // 外圈刻度：0/25/50/75/100%，只作仪表读数参考。
        using (var tick = new Pen(P.LineStrong, Math.Max(1, Theme.Sf(1.25f))))
        {
            var cx = arc.X + arc.Width / 2; var cy = arc.Y + arc.Height / 2;
            var r1 = arc.Width / 2 + thickness / 2 + S(4); var r2 = r1 + S(5);
            foreach (var fraction in new[] { 0, 0.25, 0.5, 0.75, 1 })
            {
                var angle = (start + sweep * fraction) * Math.PI / 180;
                g.DrawLine(tick, cx + (float)(Math.Cos(angle) * r1), cy + (float)(Math.Sin(angle) * r1),
                    cx + (float)(Math.Cos(angle) * r2), cy + (float)(Math.Sin(angle) * r2));
            }
        }
        var confirmed = Math.Clamp(Confirmed, 0, 1);
        var reserved = Math.Clamp(Reserved, 0, 1 - confirmed);
        var fill = FillColor;
        if (reserved > 0.0005)
        {
            using var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(170, fill), Color.FromArgb(60, fill));
            using var pen = new Pen(hatch, thickness) { StartCap = LineCap.Flat, EndCap = LineCap.Round };
            g.DrawArc(pen, arc, start + (float)(sweep * confirmed), (float)Math.Max(sweep * reserved, 2));
        }
        if (confirmed > 0.0005)
        {
            if (Kind == TaskStatusKind.Burning)
            {
                // 余烬光晕：沿已确认弧外扩的几层半透明描边，随呼吸相位轻微起伏。
                for (var layer = 3; layer >= 1; layer--)
                {
                    var alpha = (int)((P.IsDark ? 34 : 22) * (0.55f + 0.45f * Pulse) / layer);
                    using var glow = new Pen(Color.FromArgb(alpha, fill), thickness + Theme.Sf(5) * layer) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawArc(glow, arc, start, (float)Math.Max(sweep * confirmed, 0.5));
                }
            }
            using var pen = new Pen(fill, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, arc, start, (float)Math.Max(sweep * confirmed, 0.5));
            if (Kind == TaskStatusKind.Burning)
            {
                // 弧头的亮点：表示正在推进的位置。
                var cx = arc.X + arc.Width / 2; var cy = arc.Y + arc.Height / 2;
                var angle = (start + sweep * confirmed) * Math.PI / 180;
                var head = new PointF(cx + (float)(Math.Cos(angle) * arc.Width / 2), cy + (float)(Math.Sin(angle) * arc.Height / 2));
                var radius = thickness * (0.28f + 0.1f * Pulse);
                using var spark = new SolidBrush(Color.FromArgb(235, 255, 236, 200));
                g.FillEllipse(spark, head.X - radius, head.Y - radius, radius * 2, radius * 2);
            }
        }
        if (Reference is > 0 and < 1)
        {
            var cx = arc.X + arc.Width / 2; var cy = arc.Y + arc.Height / 2;
            var angle = (start + sweep * Reference.Value) * Math.PI / 180;
            var r1 = arc.Width / 2 - thickness / 2 - S(2); var r2 = arc.Width / 2 + thickness / 2 + S(2);
            using var mark = new Pen(P.Ink, Math.Max(1.5f, Theme.Sf(2)));
            g.DrawLine(mark, cx + (float)(Math.Cos(angle) * r1), cy + (float)(Math.Sin(angle) * r1),
                cx + (float)(Math.Cos(angle) * r2), cy + (float)(Math.Sin(angle) * r2));
        }
        var centerY = (int)(arc.Y + arc.Height / 2);
        var number = Draw.Measure(Center, F.NumberHuge);
        Draw.Text(g, Center, F.NumberHuge, new Rectangle(0, centerY - number.Height / 2 - S(8), Width, number.Height),
            P.Ink, Draw.Single | TextFormatFlags.HorizontalCenter);
        var captionTop = centerY + number.Height / 2 - S(6);
        Draw.Text(g, Caption, F.Caption, new Rectangle(0, captionTop, Width, F.Caption.Height + S(2)), P.Muted,
            Draw.Single | TextFormatFlags.HorizontalCenter);
        Draw.Text(g, Detail, F.Number, new Rectangle(0, captionTop + F.Caption.Height + S(2), Width, F.Number.Height + S(4)), P.Ink,
            Draw.Single | TextFormatFlags.HorizontalCenter);
    }
}

/// <summary>提示框：带级别图标和底色，可附一个子控件（如核查确认勾选）。</summary>
internal sealed class UiCallout : UiControl, IHeightForWidth
{
    private Control? _content;
    public NoticeLevel Level { get; set { field = value; Invalidate(); } }
    public string? Title { get; set { field = value; Parent?.PerformLayout(); Invalidate(); } }
    public Control? Content
    {
        get => _content;
        set
        {
            if (_content is not null) Controls.Remove(_content);
            _content = value;
            if (value is not null) Controls.Add(value);
            PerformLayout();
        }
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Parent?.PerformLayout(); Invalidate(); }
    private int TextLeft => S(40);
    private int TextWidth(int width) => Math.Max(10, width - TextLeft - S(14));
    private int TitleHeight(int width) => string.IsNullOrEmpty(Title) ? 0 : Draw.Measure(Title, F.BodyBold, TextWidth(width), true).Height + S(3);
    public int HeightForWidth(int width)
    {
        var text = string.IsNullOrEmpty(Text) ? 0 : Draw.Measure(Text, F.Body, TextWidth(width), true).Height;
        var content = _content is null || !_content.Visible ? 0 : S(8) + (_content is IHeightForWidth measured ? measured.HeightForWidth(TextWidth(width)) : _content.Height);
        return S(11) + TitleHeight(width) + text + content + S(11);
    }
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_content is null) return;
        var top = S(11) + TitleHeight(Width) + (string.IsNullOrEmpty(Text) ? 0 : Draw.Measure(Text, F.Body, TextWidth(Width), true).Height) + S(8);
        var height = _content is IHeightForWidth measured ? measured.HeightForWidth(TextWidth(Width)) : _content.Height;
        _content.SetBounds(TextLeft, top, TextWidth(Width), height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var (fore, back, glyph) = Draw.NoticeColors(Level);
        var outside = Parent?.BackColor ?? P.Surface;
        Draw.Panel(e.Graphics, ClientRectangle, Theme.Sf(8), outside, back, null);
        Draw.Glyph(e.Graphics, glyph, F.Icon, new Rectangle(S(12), S(10), S(18), S(20)), fore);
        var y = S(11);
        if (!string.IsNullOrEmpty(Title))
        {
            var h = TitleHeight(Width);
            Draw.Text(e.Graphics, Title, F.BodyBold, new Rectangle(TextLeft, y, TextWidth(Width), h), P.Ink, Draw.Wrap);
            y += h;
        }
        Draw.Text(e.Graphics, Text, F.Body, new Rectangle(TextLeft, y, TextWidth(Width), Height - y), P.Ink, Draw.Wrap);
    }
    protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); Invalidate(); }
    public override Color BackColor { get => Draw.NoticeColors(Level).Back; set { } }
}

/// <summary>状态胶囊控件（详情标题旁）。</summary>
internal sealed class UiPill : UiControl
{
    public TaskStatusKind Kind { get; private set; }
    public void Set(string text, TaskStatusKind kind)
    {
        if (Text == text && Kind == kind) return;
        Text = text; Kind = kind; AccessibleName = "状态：" + text;
        Size = Draw.PillSize(text, F.Caption);
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var (fore, back) = Draw.StatusColors(Kind);
        Draw.Pill(e.Graphics, ClientRectangle, Text, F.Caption, fore, back);
    }
}

/// <summary>品牌标识：窑青方块里的一簇火焰；运行中火焰为余烬色，空闲为浅色。</summary>
internal static class BrandMark
{
    private static readonly Color Tile = Color.FromArgb(14, 107, 102);
    private static readonly Color TileDark = Color.FromArgb(10, 78, 75);

    public static void Paint(Graphics g, RectangleF bounds, bool burning = true)
    {
        Draw.Smooth(g);
        using (var tile = new LinearGradientBrush(bounds, Tile, TileDark, 90f))
        using (var path = Draw.Round(bounds, bounds.Width * 0.24f))
            g.FillPath(tile, path);
        RectangleF Inner(float x, float y, float w, float h) => new(bounds.X + bounds.Width * x, bounds.Y + bounds.Height * y, bounds.Width * w, bounds.Height * h);
        PointF Pt(float x, float y) => new(bounds.X + bounds.Width * (0.18f + x * 0.64f), bounds.Y + bounds.Height * (0.14f + y * 0.74f));
        using var outer = new GraphicsPath();
        outer.AddBezier(Pt(0.5f, 1f), Pt(0.18f, 1f), Pt(0f, 0.8f), Pt(0.03f, 0.58f));
        outer.AddBezier(Pt(0.03f, 0.58f), Pt(0.06f, 0.38f), Pt(0.22f, 0.3f), Pt(0.26f, 0.1f));
        outer.AddBezier(Pt(0.26f, 0.1f), Pt(0.4f, 0.22f), Pt(0.45f, 0.33f), Pt(0.44f, 0.45f));
        outer.AddBezier(Pt(0.44f, 0.45f), Pt(0.56f, 0.32f), Pt(0.6f, 0.15f), Pt(0.54f, 0f));
        outer.AddBezier(Pt(0.54f, 0f), Pt(0.82f, 0.16f), Pt(1f, 0.4f), Pt(0.98f, 0.62f));
        outer.AddBezier(Pt(0.98f, 0.62f), Pt(0.96f, 0.84f), Pt(0.78f, 1f), Pt(0.5f, 1f));
        outer.CloseFigure();
        var flameBox = Inner(0.15f, 0.12f, 0.7f, 0.78f);
        using (var flame = burning
            ? new LinearGradientBrush(flameBox, Color.FromArgb(255, 196, 92), Color.FromArgb(232, 80, 40), 90f)
            : new LinearGradientBrush(flameBox, Color.FromArgb(236, 247, 245), Color.FromArgb(190, 222, 218), 90f))
            g.FillPath(flame, outer);
        using var inner = new GraphicsPath();
        inner.AddBezier(Pt(0.5f, 0.95f), Pt(0.36f, 0.95f), Pt(0.27f, 0.84f), Pt(0.29f, 0.73f));
        inner.AddBezier(Pt(0.29f, 0.73f), Pt(0.31f, 0.62f), Pt(0.4f, 0.57f), Pt(0.44f, 0.47f));
        inner.AddBezier(Pt(0.44f, 0.47f), Pt(0.52f, 0.56f), Pt(0.58f, 0.6f), Pt(0.63f, 0.55f));
        inner.AddBezier(Pt(0.63f, 0.55f), Pt(0.72f, 0.65f), Pt(0.73f, 0.74f), Pt(0.71f, 0.8f));
        inner.AddBezier(Pt(0.71f, 0.8f), Pt(0.68f, 0.9f), Pt(0.6f, 0.95f), Pt(0.5f, 0.95f));
        inner.CloseFigure();
        using var core = new SolidBrush(burning ? Color.FromArgb(255, 236, 196) : Color.FromArgb(14, 107, 102));
        g.FillPath(core, inner);
    }

    public static Bitmap Render(int size, bool burning = true)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        var pad = size <= 20 ? 0.5f : size * 0.04f;
        Paint(g, new RectangleF(pad, pad, size - pad * 2, size - pad * 2), burning);
        return bitmap;
    }

    /// <summary>生成多尺寸 .ico（≤64 用 32 位 DIB，256 用 PNG），图标对象自持句柄可正常释放。</summary>
    public static byte[] IconBytes(bool burning, params int[] sizes)
    {
        var images = sizes.Select(size =>
        {
            using var bitmap = Render(size, burning);
            return (Size: size, Data: size >= 256 ? Png(bitmap) : Dib(bitmap));
        }).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((short)0); writer.Write((short)1); writer.Write((short)images.Length);
        var offset = 6 + 16 * images.Length;
        foreach (var image in images)
        {
            writer.Write((byte)(image.Size >= 256 ? 0 : image.Size)); writer.Write((byte)(image.Size >= 256 ? 0 : image.Size));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((short)1); writer.Write((short)32);
            writer.Write(image.Data.Length); writer.Write(offset);
            offset += image.Data.Length;
        }
        foreach (var image in images) writer.Write(image.Data);
        writer.Flush();
        return stream.ToArray();
    }

    public static Icon CreateIcon(int size, bool burning)
    {
        using var stream = new MemoryStream(IconBytes(burning, size));
        return new Icon(stream, size, size);
    }

    private static byte[] Png(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] Dib(Bitmap bitmap)
    {
        var size = bitmap.Width;
        var maskStride = ((size + 31) / 32) * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40); writer.Write(size); writer.Write(size * 2); writer.Write((short)1); writer.Write((short)32);
        writer.Write(0); writer.Write(size * size * 4 + maskStride * size); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        var data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[size * 4];
            for (var y = size - 1; y >= 0; y--)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                writer.Write(row);
            }
        }
        finally { bitmap.UnlockBits(data); }
        writer.Write(new byte[maskStride * size]);
        writer.Flush();
        return stream.ToArray();
    }
}

/// <summary>右键菜单/托盘菜单的主题渲染。</summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Theme.Current.Surface);
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Current.LineStrong);
        e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        Draw.Smooth(e.Graphics);
        Draw.Fill(e.Graphics, new RectangleF(Theme.S(4), 1, e.Item.Width - Theme.S(8), e.Item.Height - 2), Theme.Sf(5), Theme.Current.SurfaceHover);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Theme.Current.Subtle : e.Item.Tag as string == "danger" ? Theme.Current.Danger : Theme.Current.Ink;
        e.TextFont = Theme.Fonts.Body;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Current.Line);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, Theme.S(8), y, e.Item.Width - Theme.S(8), y);
    }
    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Current.Surface;
        public override Color MenuBorder => Theme.Current.LineStrong;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color ImageMarginGradientBegin => Theme.Current.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Current.Surface;
        public override Color ImageMarginGradientEnd => Theme.Current.Surface;
    }

    public static ContextMenuStrip Create()
    {
        var menu = new ContextMenuStrip { Renderer = new MenuRenderer(), ShowImageMargin = false, Font = Theme.Fonts.Body, Padding = new Padding(0, Theme.S(4), 0, Theme.S(4)) };
        return menu;
    }

    public static ToolStripMenuItem Item(string text, Action onClick, bool danger = false)
    {
        var item = new ToolStripMenuItem(text) { Padding = new Padding(Theme.S(6), Theme.S(5), Theme.S(18), Theme.S(5)), Tag = danger ? "danger" : null };
        item.Click += (_, _) => onClick();
        return item;
    }
}

/// <summary>主题化提示气泡。</summary>
internal static class Tips
{
    public static ToolTip Create()
    {
        var tip = new ToolTip { OwnerDraw = true, InitialDelay = 400, AutoPopDelay = 15000, ReshowDelay = 100 };
        tip.Popup += (_, e) =>
        {
            var text = tip.GetToolTip(e.AssociatedControl);
            var size = Draw.Measure(text, Theme.Fonts.Caption, Theme.S(320), true);
            e.ToolTipSize = new Size(size.Width + Theme.S(20), size.Height + Theme.S(14));
        };
        tip.Draw += (_, e) =>
        {
            e.Graphics.Clear(Theme.Current.IsDark ? Theme.Current.SurfaceHover : Theme.Current.Ink);
            Draw.Text(e.Graphics, e.ToolTipText, Theme.Fonts.Caption, new Rectangle(Theme.S(10), Theme.S(7), e.Bounds.Width - Theme.S(20), e.Bounds.Height - Theme.S(14)),
                Theme.Current.IsDark ? Theme.Current.Ink : Theme.Current.Surface, Draw.Wrap);
        };
        return tip;
    }
}
