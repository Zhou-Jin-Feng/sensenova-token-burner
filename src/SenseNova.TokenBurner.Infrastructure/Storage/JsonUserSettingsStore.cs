using System.Text.Json;
using System.Text.Json.Serialization;
using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Storage;

public sealed class JsonUserSettingsStore(string directory) : IUserSettingsStore
{
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "mock-settings.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<AppSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await AtomicUserFile.ReadAsync(_path, cancellationToken);
            if (data is null) return null;
            var settings = JsonSerializer.Deserialize<AppSettings>(data, Options) ?? throw new JsonException();
            settings.Validate();
            return settings;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        { throw new LocalStorageException("配置文件无法读取或格式无效，请检查后重新保存。"); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        try { await AtomicUserFile.WriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(settings, Options), cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new LocalStorageException("配置无法保存，请检查数据目录的可写权限。"); }
    }
}
