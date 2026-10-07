using System.Text.Json;
using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Storage;

/// <summary>纯界面偏好（主题）；与任务配置分文件，读写失败只回退默认值，不影响任务与凭据。</summary>
public sealed record UiPreferences
{
    public const string ThemeSystem = "system";
    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";
    public string Theme { get; init; } = ThemeSystem;
    public UiPreferences Normalize() => Theme is ThemeSystem or ThemeLight or ThemeDark ? this : this with { Theme = ThemeSystem };
}

public sealed class JsonUiPreferencesStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private string PreferencesPath => Path.Combine(Path.GetFullPath(directory), "ui-preferences.json");

    public UiPreferences Load()
    {
        try
        {
            // 启动时在界面线程同步读取几十字节；不走异步读取，避免界面同步上下文死锁。
            var info = new FileInfo(PreferencesPath);
            if (!info.Exists || info.Length > 4096) return new();
            return (JsonSerializer.Deserialize<UiPreferences>(File.ReadAllBytes(info.FullName), Options) ?? new()).Normalize();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        { return new(); }
    }

    public async Task<bool> SaveAsync(UiPreferences preferences, CancellationToken token = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetFullPath(directory));
            await AtomicUserFile.WriteAsync(PreferencesPath, JsonSerializer.SerializeToUtf8Bytes(preferences.Normalize(), Options), token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LocalStorageException)
        { return false; }
    }
}
