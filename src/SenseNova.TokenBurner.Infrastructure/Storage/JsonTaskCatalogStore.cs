using System.Text.Json;
using System.Text.Json.Serialization;
using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Storage;

/// <summary>首次升级只创建任务索引；旧settings/key/run-state保留并继续使用原路径。</summary>
public sealed class JsonTaskCatalogStore(string directory, StorageProfile profile = StorageProfile.User) : ITaskCatalogStore
{
    private readonly string _directory = Path.GetFullPath(directory);
    public string RootDirectory => _directory;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private string CatalogPath => Path.Combine(_directory, "tasks.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, IgnoreReadOnlyProperties = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string TaskDirectory(TaskConfiguration task)
    {
        task.Validate();
        return task.UsesLegacyStorage ? _directory : Path.Combine(_directory, "Tasks", task.Id.ToString("N"));
    }
    public ICredentialStore Credentials(TaskConfiguration task) => new DpapiCredentialStore(TaskDirectory(task), profile);
    public IRunStateStore RunState(TaskConfiguration task) => new JsonRunStateStore(TaskDirectory(task));

    public async Task<TaskCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await AtomicUserFile.ReadAsync(CatalogPath, cancellationToken).ConfigureAwait(false);
            if (bytes is not null)
            {
                var catalog = JsonSerializer.Deserialize<TaskCatalog>(bytes, Options) ?? throw new JsonException();
                catalog.Validate();
                return catalog;
            }
            var settings = await new JsonUserSettingsStore(_directory, profile).LoadAsync(cancellationToken).ConfigureAwait(false);
            var credentialPath = Path.Combine(_directory, profile == StorageProfile.User ? "api-key.dat" : "mock-credential.dat");
            var legacy = settings is not null || File.Exists(credentialPath) || File.Exists(Path.Combine(_directory, "run-state.json"));
            if (!legacy) return TaskCatalog.Empty;
            settings ??= new();
            var task = new TaskConfiguration
            {
                DisplayName = "原有任务", UsesLegacyStorage = true, Percentage = settings.Percentage,
                IntervalHours = settings.IntervalHours, ModelId = settings.ModelId,
                CredentialSaved = settings.RememberCredential && File.Exists(credentialPath)
            };
            var migrated = TaskCatalog.Empty with { Tasks = [task] };
            // 不解密、不复制、不改写旧凭据和历史；只新增索引，失败时旧版本仍可读取原文件。
            await WriteAsync(migrated, cancellationToken).ConfigureAwait(false);
            return migrated;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        { throw new LocalStorageException("任务配置不可读；原文件已保留，未启动任何任务。"); }
        finally { _writes.Release(); }
    }

    public async Task SaveAsync(TaskCatalog catalog, CancellationToken cancellationToken = default)
    {
        catalog.Validate();
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteAsync(catalog, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new LocalStorageException("任务配置无法保存，请检查数据目录。"); }
        finally { _writes.Release(); }
    }

    private Task WriteAsync(TaskCatalog catalog, CancellationToken token)
        => AtomicUserFile.WriteAsync(CatalogPath, JsonSerializer.SerializeToUtf8Bytes(catalog, Options), token, waitForReplacement: true);
}
