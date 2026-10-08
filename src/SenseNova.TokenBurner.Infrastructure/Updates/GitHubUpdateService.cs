using System.Security.Cryptography;
using System.Text.Json;
using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Updates;

public sealed class GitHubUpdateService : IAppUpdateService
{
    private readonly HttpClient _http;

    public GitHubUpdateService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<AppUpdateInfo> CheckForUpdateAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SenseNovaDefaults.LatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd($"SenseNova-TokenBurner/{currentVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var title = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? SenseNovaDefaults.RepositoryUrl : SenseNovaDefaults.RepositoryUrl;

        string? setupUrl = null;
        string? setupFileName = null;
        string? checksumUrl = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                var downloadUrl = asset.TryGetProperty("browser_download_url", out var ad) ? ad.GetString() : null;

                if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    setupUrl = downloadUrl;
                    setupFileName = name;
                }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = downloadUrl;
                }
            }
        }

        var hasUpdate = AppVersionComparer.IsNewer(currentVersion, tagName);
        return new AppUpdateInfo(
            currentVersion,
            tagName,
            hasUpdate,
            title,
            body,
            htmlUrl,
            setupUrl,
            setupFileName,
            checksumUrl);
    }

    public async Task<string> DownloadAndVerifySetupAsync(AppUpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(update.SetupDownloadUrl))
            throw new InvalidOperationException("该版本未随附 Windows 安装包。");

        var tempDir = Path.Combine(Path.GetTempPath(), "SenseNova.TokenBurner", "Updates", update.LatestVersion.TrimStart('v', 'V'));
        Directory.CreateDirectory(tempDir);
        var setupPath = Path.Combine(tempDir, update.SetupFileName ?? "setup.exe");

        string? expectedHash = null;
        if (!string.IsNullOrEmpty(update.ChecksumDownloadUrl))
        {
            try
            {
                using var csReq = new HttpRequestMessage(HttpMethod.Get, update.ChecksumDownloadUrl);
                csReq.Headers.UserAgent.ParseAdd($"SenseNova-TokenBurner/{update.CurrentVersion}");
                using var csResp = await _http.SendAsync(csReq, cancellationToken).ConfigureAwait(false);
                if (csResp.IsSuccessStatusCode)
                {
                    var checksumContent = await csResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    expectedHash = ParseChecksum(checksumContent, update.SetupFileName);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 哈希列表拉取失败不强行阻断，视网络情况继续
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, update.SetupDownloadUrl);
        request.Headers.UserAgent.ParseAdd($"SenseNova-TokenBurner/{update.CurrentVersion}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var fileStream = new FileStream(setupPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long readBytes = 0;
            int bytes;
            while ((bytes = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytes), cancellationToken).ConfigureAwait(false);
                readBytes += bytes;
                if (totalBytes > 0)
                {
                    progress?.Report(Math.Clamp((double)readBytes / totalBytes, 0, 1));
                }
            }
            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(expectedHash))
        {
            await using var verifyStream = File.OpenRead(setupPath);
            var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(verifyStream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(setupPath); } catch { }
                throw new InvalidOperationException($"安装包 SHA-256 校验失败（预期 {expectedHash}，实际 {actualHash}），文件已移除。");
            }
        }

        return setupPath;
    }

        public static string? ParseChecksum(string checksumText, string? targetFileName)
    {
        if (string.IsNullOrWhiteSpace(checksumText)) return null;
        using var reader = new StringReader(checksumText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 64) continue;
            var parts = trimmed.Split(new[] { ' ', '	', '*' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 64)
            {
                var hash = parts[0].ToLowerInvariant();
                var fileName = parts[^1];
                if (string.IsNullOrEmpty(targetFileName) || fileName.Equals(targetFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return hash;
                }
            }
        }
        return null;
    }
}

public sealed class MockUpdateService : IAppUpdateService
{
    public AppUpdateInfo? MockResult { get; set; }
    public string? MockDownloadedPath { get; set; }

    public Task<AppUpdateInfo> CheckForUpdateAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(MockResult ?? new AppUpdateInfo(
            currentVersion,
            currentVersion,
            false,
            "当前已是最新版本",
            "没有检测到更新",
            SenseNovaDefaults.RepositoryUrl,
            null,
            null,
            null));
    }

    public Task<string> DownloadAndVerifySetupAsync(AppUpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(1.0);
        return Task.FromResult(MockDownloadedPath ?? Path.Combine(Path.GetTempPath(), "mock-setup.exe"));
    }
}
