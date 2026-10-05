using System.Text.Json;
using System.Text.Json.Serialization;
using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Storage;

public sealed class JsonRunStateStore(string directory) : IRunStateStore
{
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "run-state.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public async Task<RunStateDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await AtomicUserFile.ReadAsync(_path, cancellationToken);
            if (data is null) return null;
            var document = JsonSerializer.Deserialize<RunStateDocument>(data, Options) ?? throw new JsonException();
            document.Validate();
            return document;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        { throw new LocalStorageException("运行记录无法读取或格式无效；请核查记录，未恢复运行。"); }
    }

    public async Task SaveAsync(RunStateDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        try { await AtomicUserFile.WriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(document, Options), cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new LocalStorageException("运行记录无法保存，已阻止新增请求。"); }
    }
}
