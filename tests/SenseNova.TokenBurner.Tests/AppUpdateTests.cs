using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.Ui;
using SenseNova.TokenBurner.Infrastructure.Updates;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class AppUpdateTests
{
    [TestMethod]
    public void AppVersionComparerDetectsNewerVersions()
    {
        Assert.IsTrue(AppVersionComparer.IsNewer("1.1.0", "v1.1.1"));
        Assert.IsTrue(AppVersionComparer.IsNewer("1.1.0", "1.2.0"));
        Assert.IsTrue(AppVersionComparer.IsNewer("1.1.0", "2.0.0"));
        Assert.IsTrue(AppVersionComparer.IsNewer("0.1.0", "1.0.0"));

        Assert.IsFalse(AppVersionComparer.IsNewer("1.1.1", "v1.1.1"));
        Assert.IsFalse(AppVersionComparer.IsNewer("1.1.1", "1.1.0"));
        Assert.IsFalse(AppVersionComparer.IsNewer("2.0.0", "1.9.9"));
    }

    [TestMethod]
    public void ParseChecksumExtractsMatchingSha256()
    {
        var sample = """
426c725c3e732241fd5ee1cb487deef2753746bd43ab737c64a0cc77f711ef38  SenseNova.TokenBurner-1.1.0-win-x64-setup.exe
aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa *OtherFile.exe
""";

        var hash = GitHubUpdateService.ParseChecksum(sample, "SenseNova.TokenBurner-1.1.0-win-x64-setup.exe");
        Assert.AreEqual("426c725c3e732241fd5ee1cb487deef2753746bd43ab737c64a0cc77f711ef38", hash);

        var otherHash = GitHubUpdateService.ParseChecksum(sample, "OtherFile.exe");
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", otherHash);

        var missing = GitHubUpdateService.ParseChecksum(sample, "Missing.exe");
        Assert.IsNull(missing);
    }

    [TestMethod]
    public async Task MockUpdateServiceReturnsConfiguredUpdate()
    {
        var mockService = new MockUpdateService
        {
            MockResult = new AppUpdateInfo(
                "1.1.0",
                "v1.2.0",
                true,
                "v1.2.0 特性更新",
                "这是测试更新日志",
                "https://github.com/Zhou-Jin-Feng/sensenova-token-burner/releases/tag/v1.2.0",
                "https://example.com/setup.exe",
                "setup.exe",
                null)
        };

        var update = await mockService.CheckForUpdateAsync("1.1.0");
        Assert.IsTrue(update.HasUpdate);
        Assert.AreEqual("v1.2.0", update.LatestVersion);
        Assert.AreEqual("setup.exe", update.SetupFileName);

        var path = await mockService.DownloadAndVerifySetupAsync(update);
        Assert.IsNotNull(path);
    }

    [TestMethod]
    public void SettingsDialogContainsUpdateControls()
    {
        var model = new SettingsModel(
            ThemeMode.System,
            3,
            3,
            true,
            @"C:\data",
            "1.1.1",
            "https://token.sensenova.cn/v1",
            "sensenova-6.8-flash-lite",
            true,
            new MockUpdateService());

        using var dialog = new SettingsDialog(model, _ => { }, (_, _) => Task.FromResult<string?>(null));

        var checkBtn = FindControl<UiButton>(dialog, "CheckUpdate");
        var downloadBtn = FindControl<UiButton>(dialog, "DownloadUpdate");
        var browserBtn = FindControl<UiButton>(dialog, "OpenReleaseBrowser");

        Assert.IsNotNull(checkBtn, "设置窗口必须包含检查更新按钮。");
        Assert.IsTrue(checkBtn.Enabled);

        Assert.IsNotNull(downloadBtn, "设置窗口必须包含下载安装按钮。");
        Assert.IsFalse(downloadBtn.Visible, "初始状态下下载安装按钮应隐藏。");

        Assert.IsNotNull(browserBtn, "设置窗口必须包含在浏览器查看按钮。");
        Assert.IsFalse(browserBtn.Visible, "初始状态下浏览器查看按钮应隐藏。");
    }

    private static T? FindControl<T>(Control parent, string name) where T : Control
    {
        if (parent.Name == name && parent is T match) return match;
        foreach (Control child in parent.Controls)
        {
            var found = FindControl<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }
}
