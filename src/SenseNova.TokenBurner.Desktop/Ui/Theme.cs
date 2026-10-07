using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SenseNova.TokenBurner.Desktop.Ui;

internal enum ThemeMode { System, Light, Dark }

/// <summary>一套主题色。余烬色只用于“正在消耗”的状态，其它强调统一用窑青主色。</summary>
internal sealed class Palette
{
    public required bool IsDark { get; init; }
    public required Color Canvas { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceAlt { get; init; }
    public required Color SurfaceHover { get; init; }
    public required Color SurfaceSelected { get; init; }
    public required Color Line { get; init; }
    public required Color LineStrong { get; init; }
    public required Color Ink { get; init; }
    public required Color Muted { get; init; }
    public required Color Subtle { get; init; }
    public required Color Primary { get; init; }
    public required Color PrimaryHover { get; init; }
    public required Color PrimaryPressed { get; init; }
    public required Color OnPrimary { get; init; }
    public required Color PrimarySoft { get; init; }
    public required Color Ember { get; init; }
    public required Color EmberSoft { get; init; }
    public required Color Ok { get; init; }
    public required Color OkSoft { get; init; }
    public required Color Warn { get; init; }
    public required Color WarnSoft { get; init; }
    public required Color Danger { get; init; }
    public required Color DangerSoft { get; init; }
    public required Color Track { get; init; }
    public required Color Input { get; init; }

    public static Palette Light { get; } = new()
    {
        IsDark = false,
        Canvas = Hex(0xECF0EE), Surface = Hex(0xFFFFFF), SurfaceAlt = Hex(0xF5F7F6), SurfaceHover = Hex(0xEFF3F2),
        SurfaceSelected = Hex(0xE2EFED), Line = Hex(0xDCE3E1), LineStrong = Hex(0xC4CECB),
        Ink = Hex(0x16201F), Muted = Hex(0x5A6765), Subtle = Hex(0x8B9795),
        Primary = Hex(0x0E6B66), PrimaryHover = Hex(0x0B5D59), PrimaryPressed = Hex(0x094C49), OnPrimary = Hex(0xFFFFFF),
        PrimarySoft = Hex(0xDCEEEB), Ember = Hex(0xE4572E), EmberSoft = Hex(0xFCE8E0),
        Ok = Hex(0x2B8A55), OkSoft = Hex(0xE0F2E7), Warn = Hex(0xA4670B), WarnSoft = Hex(0xFBEFD8),
        Danger = Hex(0xC0392B), DangerSoft = Hex(0xFBE4E1), Track = Hex(0xE2E8E6), Input = Hex(0xFFFFFF)
    };

    public static Palette Dark { get; } = new()
    {
        IsDark = true,
        Canvas = Hex(0x111918), Surface = Hex(0x172120), SurfaceAlt = Hex(0x1B2625), SurfaceHover = Hex(0x202D2B),
        SurfaceSelected = Hex(0x1F3633), Line = Hex(0x283533), LineStrong = Hex(0x3A4A47),
        Ink = Hex(0xE3ECEA), Muted = Hex(0x97A9A6), Subtle = Hex(0x6E807D),
        Primary = Hex(0x36B7AA), PrimaryHover = Hex(0x48C6B9), PrimaryPressed = Hex(0x2A9B90), OnPrimary = Hex(0x062220),
        PrimarySoft = Hex(0x183C38), Ember = Hex(0xFF7448), EmberSoft = Hex(0x3B231A),
        Ok = Hex(0x4CC48B), OkSoft = Hex(0x183327), Warn = Hex(0xE6AB49), WarnSoft = Hex(0x372B15),
        Danger = Hex(0xF06A5E), DangerSoft = Hex(0x3C1E1B), Track = Hex(0x25312F), Input = Hex(0x131C1B)
    };

    private static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}

/// <summary>按 Windows 当前 DPI 预先创建的字体；字号以 96 DPI 像素为基准再缩放。</summary>
internal sealed class FontSet : IDisposable
{
    private readonly List<Font> _all = [];
    public FontSet(float scale)
    {
        var text = Pick("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI");
        var numerals = Pick("Bahnschrift", "Segoe UI");
        var numeralsBold = Pick("Bahnschrift SemiBold", "Segoe UI Semibold", "Segoe UI");
        var numeralsLight = Pick("Bahnschrift Light", "Bahnschrift SemiLight", "Segoe UI Light", "Segoe UI");
        var icons = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");
        Font Make(string family, float px, FontStyle style = FontStyle.Regular)
        {
            var font = new Font(family, Math.Max(1f, px * scale), style, GraphicsUnit.Pixel);
            _all.Add(font);
            return font;
        }
        Caption = Make(text, 12); Body = Make(text, 13); BodyBold = Make(text, 13, FontStyle.Bold);
        Section = Make(text, 15, FontStyle.Bold); Title = Make(text, 20, FontStyle.Bold);
        Number = Make(numerals, 13); NumberBold = Make(numeralsBold, 15); NumberMedium = Make(numeralsBold, 18); NumberLarge = Make(numeralsBold, 22);
        NumberHuge = Make(numeralsLight, 44); NumberCaption = Make(numerals, 12);
        Brand = Make(numeralsBold, 19); BrandLight = Make(numeralsLight, 19);
        Icon = Make(icons, 14); IconSmall = Make(icons, 12); IconLarge = Make(icons, 18);
    }
    public Font Caption { get; }
    public Font Body { get; }
    public Font BodyBold { get; }
    public Font Section { get; }
    public Font Title { get; }
    public Font Number { get; }
    public Font NumberBold { get; }
    public Font NumberMedium { get; }
    public Font NumberLarge { get; }
    public Font NumberHuge { get; }
    public Font NumberCaption { get; }
    public Font Brand { get; }
    public Font BrandLight { get; }
    public Font Icon { get; }
    public Font IconSmall { get; }
    public Font IconLarge { get; }
    public void Dispose() { foreach (var font in _all) font.Dispose(); _all.Clear(); }

    private static HashSet<string>? _installed;
    private static string Pick(params string[] families)
    {
        if (_installed is null)
        {
            using var collection = new InstalledFontCollection();
            _installed = collection.Families.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return families.FirstOrDefault(_installed.Contains) ?? families[^1];
    }
}

/// <summary>全局主题、缩放与字体。应用按 SystemAware 运行，一个进程只有一个 DPI。</summary>
internal static class Theme
{
    private static FontSet? _fonts;
    // 缩放变化时旧字体可能仍被原生输入框引用；保留到进程结束，避免句柄重建时使用已释放的字体。
    private static readonly List<FontSet> Retired = [];
    private static bool _listening;
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static Palette Current { get; private set; } = Palette.Light;
    public static float ScaleFactor { get; private set; } = 1f;
    /// <summary>仅截图/测试用：模拟其他 DPI 下的布局。</summary>
    internal static float? ScaleOverride { get; set; }
    public static FontSet Fonts => _fonts ??= new FontSet(ScaleFactor);
    public static event Action? Changed;

    private static readonly object Gate = new();

    public static void Initialize(int deviceDpi, ThemeMode mode)
    {
        lock (Gate) InitializeLocked(deviceDpi, mode);
    }

    private static void InitializeLocked(int deviceDpi, ThemeMode mode)
    {
        var scale = ScaleOverride ?? Math.Max(1f, deviceDpi / 96f);
        if (_fonts is null || Math.Abs(scale - ScaleFactor) > 0.001f)
        {
            ScaleFactor = scale;
            if (_fonts is not null) Retired.Add(_fonts);
            _fonts = new FontSet(scale);
        }
        Mode = mode;
        Current = Resolve(mode);
        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, args) =>
            {
                if (Mode != ThemeMode.System || args.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
                var resolved = Resolve(ThemeMode.System);
                if (resolved == Current) return;
                Current = resolved;
                Changed?.Invoke();
            };
        }
    }

    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        var resolved = Resolve(mode);
        if (resolved == Current) return;
        Current = resolved;
        Changed?.Invoke();
    }

    public static int S(int value) => (int)Math.Round(value * ScaleFactor);
    public static float Sf(float value) => value * ScaleFactor;
    public static Padding S(Padding padding) => new(S(padding.Left), S(padding.Top), S(padding.Right), S(padding.Bottom));

    private static Palette Resolve(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => Palette.Light,
        ThemeMode.Dark => Palette.Dark,
        _ => SystemPrefersDark() ? Palette.Dark : Palette.Light
    };

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    /// <summary>Windows 11 标题栏跟随主题和画布色；Windows 10 只切换深浅色。失败时保持系统默认。</summary>
    public static void ApplyWindowChrome(Form form)
    {
        if (!form.IsHandleCreated) return;
        var dark = Current.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
        var caption = ColorRef(Current.Canvas);
        _ = DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));
        var text = ColorRef(Current.Ink);
        _ = DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int));
        var border = ColorRef(Current.LineStrong);
        _ = DwmSetWindowAttribute(form.Handle, 34, ref border, sizeof(int));
    }

    /// <summary>原生滚动条/输入框跟随深浅色。</summary>
    public static void ApplyNativeTheme(Control control)
    {
        if (!control.IsHandleCreated) return;
        _ = SetWindowTheme(control.Handle, Current.IsDark ? "DarkMode_Explorer" : "Explorer", null);
    }

    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subApplication, string? idList);
}
