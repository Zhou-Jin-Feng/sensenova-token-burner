namespace SenseNova.TokenBurner.Core;

public sealed record AppUpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    bool HasUpdate,
    string Title,
    string ReleaseNotes,
    string HtmlUrl,
    string? SetupDownloadUrl,
    string? SetupFileName,
    string? ChecksumDownloadUrl);

public interface IAppUpdateService
{
    Task<AppUpdateInfo> CheckForUpdateAsync(string currentVersion, CancellationToken cancellationToken = default);
    Task<string> DownloadAndVerifySetupAsync(AppUpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

public static class AppVersionComparer
{
    public static bool IsNewer(string currentVersion, string candidateVersion)
    {
        var cur = Normalize(currentVersion);
        var cand = Normalize(candidateVersion);
        return cand > cur;
    }

    public static Version Normalize(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return new Version(0, 0, 0);
        var cleaned = version.Trim().TrimStart('v', 'V');
        var dash = cleaned.IndexOf('-');
        if (dash > 0) cleaned = cleaned[..dash];
        var parts = cleaned.Split('.');
        if (parts.Length == 1 && int.TryParse(parts[0], out var maj)) return new Version(maj, 0, 0);
        if (parts.Length == 2 && int.TryParse(parts[0], out maj) && int.TryParse(parts[1], out var min)) return new Version(maj, min, 0);
        if (Version.TryParse(cleaned, out var parsed)) return parsed;
        return new Version(0, 0, 0);
    }
}
