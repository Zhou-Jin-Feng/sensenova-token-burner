using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Desktop.Ui;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class PanelFormattingTests
{
    [TestMethod]
    public void CompactTokensAndPercentTruncateInsteadOfRoundingUpToTarget()
    {
        Assert.AreEqual("1.19亿", Format.Compact(119_999_999), "未达 1.2 亿时不能显示成 1.2 亿。");
        Assert.AreEqual("1.2亿", Format.Compact(120_000_000));
        Assert.AreEqual("1亿", Format.Compact(100_000_000));
        Assert.AreEqual("1.23亿", Format.Compact(123_456_789));
        Assert.AreEqual("7,440万", Format.Compact(74_400_000));
        Assert.AreEqual("21.8万", Format.Compact(218_852));
        Assert.AreEqual("9,876", Format.Compact(9_876));
        Assert.AreEqual("99.9%", Format.Percent(0.99996), "未完成时不能显示 100%。");
        Assert.AreEqual("100%", Format.Percent(1));
        Assert.AreEqual("0%", Format.Percent(0));
        Assert.AreEqual("37.9%", Format.Percent(0.3797));
    }

    [TestMethod]
    public async Task UiPreferencesFallBackToSystemAndRoundTrip()
    {
        using var folder = new TestFolder();
        var store = new JsonUiPreferencesStore(folder.Path);
        var path = Path.Combine(folder.Path, "ui-preferences.json");
        Assert.AreEqual(UiPreferences.ThemeSystem, store.Load().Theme);
        await File.WriteAllTextAsync(path, "{not json");
        Assert.AreEqual(UiPreferences.ThemeSystem, store.Load().Theme, "损坏的偏好文件应回退默认值。");
        Assert.IsTrue(await store.SaveAsync(new UiPreferences { Theme = UiPreferences.ThemeDark }));
        Assert.AreEqual(UiPreferences.ThemeDark, store.Load().Theme);
        await File.WriteAllTextAsync(path, "{\"Theme\":\"neon\"}");
        Assert.AreEqual(UiPreferences.ThemeSystem, store.Load().Theme, "未知主题值应回退跟随系统。");
    }

    [TestMethod]
    public void BurnTrackerReportsRatePerMinuteBucketsAndResetsWithProgress()
    {
        var tracker = new BurnTracker();
        var id = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.FromHours(8));
        for (var i = 0; i <= 10; i++) tracker.Observe(id, i * 1_000_000L, burning: true, start.AddSeconds(30 * i));
        var now = start.AddSeconds(300);
        Assert.AreEqual(2_000_000d, tracker.Rate(id, now), 1, "每 30 秒确认 100 万，应为每分钟 200 万。");
        var buckets = tracker.PerMinute(id, now, 10);
        Assert.AreEqual(10_000_000L, buckets.Sum());
        Assert.AreEqual(1_000_000L, buckets[^1], "当前分钟只含最后一次增长。");
        tracker.Observe(id, 0, burning: false, now.AddSeconds(5));
        Assert.AreEqual(0d, tracker.Rate(id, now.AddSeconds(5)), "进度归零（重置）后速率从头计算。");
    }
}
