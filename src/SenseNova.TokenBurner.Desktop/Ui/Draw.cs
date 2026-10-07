using System.Drawing.Drawing2D;
using SenseNova.TokenBurner.Desktop.ViewModels;

namespace SenseNova.TokenBurner.Desktop.Ui;

/// <summary>Segoe Fluent Icons / MDL2 共用码位。</summary>
internal static class Glyphs
{
    public const string Play = "";
    public const string Pause = "";
    public const string Reset = "";
    public const string Add = "";
    public const string Settings = "";
    public const string More = "";
    public const string Delete = "";
    public const string Warning = "";
    public const string Info = "";
    public const string Check = "";
    public const string Error = "";
    public const string Success = "";
    public const string Clock = "";
    public const string Calendar = "";
    public const string Folder = "";
    public const string Copy = "";
    public const string Plug = "";
    public const string Key = "";
    public const string Eye = "";
    public const string Minus = "";
    public const string History = "";
    public const string Power = "";
    public const string Bell = "";
    public const string ChevronDown = "";
    public const string Shield = "";
    public const string Sun = "";
    public const string Moon = "";
    public const string Edit = "";
}

internal static class Draw
{
    public static GraphicsPath Round(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0.5f) { path.AddRectangle(bounds); return path; }
        var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter; path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter; path.AddArc(arc, 0, 90);
        arc.X = bounds.X; path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Smooth(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static void Fill(Graphics graphics, RectangleF bounds, float radius, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var brush = new SolidBrush(color);
        using var path = Round(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void Stroke(Graphics graphics, RectangleF bounds, float radius, Color color, float width = 1)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        var inset = width / 2f;
        using var pen = new Pen(color, width);
        using var path = Round(new RectangleF(bounds.X + inset, bounds.Y + inset, bounds.Width - width, bounds.Height - width), radius);
        graphics.DrawPath(pen, path);
    }

    /// <summary>圆角容器：先用父背景填角，再画面和边框，避免锯齿和透出底色。</summary>
    public static void Panel(Graphics graphics, Rectangle bounds, float radius, Color outside, Color fill, Color? border)
    {
        graphics.Clear(outside);
        Smooth(graphics);
        Fill(graphics, bounds, radius, fill);
        if (border is { } line) Stroke(graphics, bounds, radius, line);
    }

    public const TextFormatFlags Single = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
        | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
    public const TextFormatFlags Wrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl
        | TextFormatFlags.NoPrefix;

    public static void Text(Graphics graphics, string? text, Font font, Rectangle bounds, Color color, TextFormatFlags flags = Single)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;
        TextRenderer.DrawText(graphics, text, font, bounds, color, flags | TextFormatFlags.PreserveGraphicsClipping);
    }

    public static Size Measure(string? text, Font font, int maxWidth = int.MaxValue, bool wrap = false)
    {
        if (string.IsNullOrEmpty(text)) return new Size(0, font.Height);
        var flags = wrap ? Wrap : TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        return TextRenderer.MeasureText(text, font, new Size(Math.Max(1, maxWidth), int.MaxValue), flags);
    }

    public static void Glyph(Graphics graphics, string glyph, Font font, Rectangle bounds, Color color)
        => TextRenderer.DrawText(graphics, glyph, font, bounds, color,
            TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);

    public static Color Mix(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    /// <summary>状态类别对应的前景/底色；余烬色只给正在消耗的任务。</summary>
    public static (Color Fore, Color Back) StatusColors(TaskStatusKind kind)
    {
        var p = Theme.Current;
        return kind switch
        {
            TaskStatusKind.Burning => (p.Ember, p.EmberSoft),
            TaskStatusKind.Success => (p.Ok, p.OkSoft),
            TaskStatusKind.Warning => (p.Warn, p.WarnSoft),
            TaskStatusKind.Danger => (p.Danger, p.DangerSoft),
            TaskStatusKind.Info or TaskStatusKind.Busy => (p.Primary, p.PrimarySoft),
            TaskStatusKind.Paused => (p.Ink, p.SurfaceHover),
            _ => (p.Muted, p.SurfaceAlt)
        };
    }

    public static (Color Fore, Color Back, string Glyph) NoticeColors(NoticeLevel level)
    {
        var p = Theme.Current;
        return level switch
        {
            NoticeLevel.Success => (p.Ok, p.OkSoft, Glyphs.Success),
            NoticeLevel.Warning => (p.Warn, p.WarnSoft, Glyphs.Warning),
            NoticeLevel.Error => (p.Danger, p.DangerSoft, Glyphs.Error),
            _ => (p.Primary, p.PrimarySoft, Glyphs.Info)
        };
    }

    public static Size PillSize(string text, Font font)
    {
        var size = Measure(text, font);
        return new Size(size.Width + Theme.S(22), Math.Max(Theme.S(22), size.Height + Theme.S(6)));
    }

    /// <summary>状态胶囊：圆点 + 文字，圆点在运行时为余烬色。</summary>
    public static void Pill(Graphics graphics, Rectangle bounds, string text, Font font, Color fore, Color back)
    {
        Smooth(graphics);
        Fill(graphics, bounds, bounds.Height / 2f, back);
        var dot = Theme.S(6);
        using (var brush = new SolidBrush(fore))
            graphics.FillEllipse(brush, bounds.X + Theme.S(9), bounds.Y + (bounds.Height - dot) / 2f, dot, dot);
        Text(graphics, text, font, new Rectangle(bounds.X + Theme.S(19), bounds.Y, bounds.Width - Theme.S(22), bounds.Height), fore);
    }

    /// <summary>用量条：实色为已确认，斜纹为在途预留，可选参考刻度。</summary>
    public static void Meter(Graphics graphics, Rectangle bounds, double confirmed, double reserved, Color fill, Color track,
        double? reference = null)
    {
        Smooth(graphics);
        var radius = bounds.Height / 2f;
        Fill(graphics, bounds, radius, track);
        confirmed = Math.Clamp(confirmed, 0, 1);
        reserved = Math.Clamp(reserved, 0, 1 - confirmed);
        using var clip = Round(bounds, radius);
        var state = graphics.Save();
        graphics.SetClip(clip, CombineMode.Intersect);
        if (reserved > 0)
        {
            var start = bounds.X + (float)(bounds.Width * confirmed);
            var width = (float)Math.Max(bounds.Width * reserved, Theme.S(3));
            using var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(150, fill), Color.FromArgb(55, fill));
            graphics.RenderingOrigin = new Point(bounds.X, bounds.Y);
            graphics.FillRectangle(hatch, start, bounds.Y, width, bounds.Height);
        }
        if (confirmed > 0)
        {
            var width = (float)Math.Max(bounds.Width * confirmed, bounds.Height);
            using var brush = new SolidBrush(fill);
            graphics.FillRectangle(brush, bounds.X, bounds.Y, width, bounds.Height);
        }
        graphics.Restore(state);
        if (reference is > 0 and < 1)
        {
            var x = bounds.X + (float)(bounds.Width * reference.Value);
            using var pen = new Pen(Theme.Current.Ink, Math.Max(1, Theme.Sf(1.5f)));
            graphics.DrawLine(pen, x, bounds.Y - Theme.S(2), x, bounds.Bottom + Theme.S(1));
        }
    }

    public static Color Disabled(Color color, Color background) => Mix(color, background, 0.55f);
}
