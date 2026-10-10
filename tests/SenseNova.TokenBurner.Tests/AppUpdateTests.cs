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

    private const string SetupName = "SenseNova.TokenBurner-9.9.9-win-x64-setup.exe";
    private const string SetupUrl = "https://example.test/setup.exe";
    private const string ChecksumUrl = "https://example.test/SHA256SUMS.txt";
    private static readonly byte[] SetupBytes = "fake installer payload"u8.ToArray();
    private static readonly string SetupHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(SetupBytes));

    [TestMethod]
    public async Task DownloadRejectsReleaseWithoutChecksumAsset()
    {
        await AssertDownloadRejectedAsync(checksumUrl: null, checksumResponse: null, "SHA256SUMS.txt");
    }

    [TestMethod]
    public async Task DownloadRejectsWhenChecksumFetchThrows()
    {
        await AssertDownloadRejectedAsync(ChecksumUrl, () => throw new HttpRequestException("network down"), "SHA256SUMS.txt");
    }

    [TestMethod]
    public async Task DownloadRejectsWhenChecksumFetchReturnsErrorStatus()
    {
        await AssertDownloadRejectedAsync(ChecksumUrl, () => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound), "HTTP 404");
    }

    [TestMethod]
    public async Task DownloadRejectsWhenSetupIsNotListedInChecksums()
    {
        var content = $"{SetupHash}  SomeOtherFile.exe\n";
        await AssertDownloadRejectedAsync(ChecksumUrl, () => new HttpResponseMessage { Content = new StringContent(content) }, SetupName);
    }

    [TestMethod]
    public async Task DownloadDeletesSetupOnHashMismatch()
    {
        var version = NewTestVersion();
        var content = $"{new string('0', 64)}  {SetupName}\n";
        var (service, setupRequests) = CreateService(() => new HttpResponseMessage { Content = new StringContent(content) });
        try
        {
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.DownloadAndVerifySetupAsync(CreateUpdate(version, ChecksumUrl)));
            StringAssert.Contains(ex.Message, "校验失败");
            Assert.AreEqual(1, setupRequests());
            Assert.IsFalse(File.Exists(SetupPathFor(version)), "哈希不匹配时必须删除已下载的安装包。");
        }
        finally
        {
            CleanupVersionDir(version);
        }
    }

    [TestMethod]
    public async Task DownloadReturnsVerifiedSetupOnHashMatch()
    {
        var version = NewTestVersion();
        var content = $"{SetupHash.ToUpperInvariant()} *{SetupName}\n";
        var (service, setupRequests) = CreateService(() => new HttpResponseMessage { Content = new StringContent(content) });
        try
        {
            var path = await service.DownloadAndVerifySetupAsync(CreateUpdate(version, ChecksumUrl));
            Assert.AreEqual(SetupPathFor(version), path);
            Assert.AreEqual(1, setupRequests());
            CollectionAssert.AreEqual(SetupBytes, File.ReadAllBytes(path));
        }
        finally
        {
            CleanupVersionDir(version);
        }
    }

    private static async Task AssertDownloadRejectedAsync(string? checksumUrl, Func<HttpResponseMessage>? checksumResponse, string expectedMessagePart)
    {
        var version = NewTestVersion();
        var (service, setupRequests) = CreateService(checksumResponse ?? (() => throw new AssertFailedException("不应请求哈希列表。")));
        try
        {
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.DownloadAndVerifySetupAsync(CreateUpdate(version, checksumUrl)));
            StringAssert.Contains(ex.Message, expectedMessagePart);
            StringAssert.Contains(ex.Message, "无法核验安装包");
            Assert.AreEqual(0, setupRequests(), "拿不到预期哈希时不应下载安装包。");
            Assert.IsFalse(File.Exists(SetupPathFor(version)), "拿不到预期哈希时不应留下安装包。");
        }
        finally
        {
            CleanupVersionDir(version);
        }
    }

    private static (GitHubUpdateService Service, Func<int> SetupRequests) CreateService(Func<HttpResponseMessage> checksumResponse)
    {
        var setupRequests = 0;
        var handler = new TestHttpHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url == ChecksumUrl) return Task.FromResult(checksumResponse());
            if (url == SetupUrl)
            {
                Interlocked.Increment(ref setupRequests);
                return Task.FromResult(new HttpResponseMessage { Content = new ByteArrayContent(SetupBytes) });
            }
            throw new AssertFailedException($"意外请求：{url}");
        });
        return (new GitHubUpdateService(new HttpClient(handler)), () => Volatile.Read(ref setupRequests));
    }

    private static AppUpdateInfo CreateUpdate(string version, string? checksumUrl) => new(
        "1.0.0",
        version,
        true,
        version,
        "",
        "https://example.test/release",
        SetupUrl,
        SetupName,
        checksumUrl);

    private static string NewTestVersion() => $"v9.9.9-test-{Guid.NewGuid():N}";

    private static string VersionDir(string version)
        => Path.Combine(Path.GetTempPath(), "SenseNova.TokenBurner", "Updates", version.TrimStart('v', 'V'));

    private static string SetupPathFor(string version) => Path.Combine(VersionDir(version), SetupName);

    private static void CleanupVersionDir(string version)
    {
        try { Directory.Delete(VersionDir(version), recursive: true); } catch (DirectoryNotFoundException) { }
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
