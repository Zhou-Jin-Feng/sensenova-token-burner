using System.Globalization;

namespace SenseNova.TokenBurner.Desktop.Ui;

/// <summary>面板数字格式。紧凑写法一律截断而不四舍五入，避免未达目标时显示成“1.2亿”。</summary>
internal static class Format
{
    public static string Compact(long value)
    {
        if (value < 0) return "-" + Compact(-value);
        if (value >= 100_000_000)
        {
            var hundredths = value / 1_000_000; // 0.01亿
            return (hundredths / 100m).ToString(hundredths % 100 == 0 ? "0" : hundredths % 10 == 0 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + "亿";
        }
        if (value >= 10_000)
        {
            var tenths = value / 1_000; // 0.1万
            return (tenths / 10m).ToString(tenths % 10 == 0 ? "#,0" : "#,0.0", CultureInfo.InvariantCulture) + "万";
        }
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static string Exact(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>百分比同样截断：99.96% 显示 99.9%，只有达到目标才显示 100%。</summary>
    public static string Percent(double fraction)
    {
        var tenths = Math.Floor(Math.Clamp(fraction, 0, 1) * 1000) / 10;
        return tenths.ToString(tenths % 1 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";
    }

    public static string Rate(double tokensPerMinute) => tokensPerMinute <= 0 ? "—" : Compact((long)tokensPerMinute) + "/分钟";

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return $"{Math.Max(0, (int)span.TotalSeconds)} 秒";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
    }

    /// <summary>今天只显示时刻，其他日期带月日。</summary>
    public static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today) return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today.AddDays(1)) return "明天 " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Hours(decimal hours) => hours.ToString("0.##", CultureInfo.InvariantCulture);
}
