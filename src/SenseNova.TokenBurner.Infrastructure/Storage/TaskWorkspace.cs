using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Storage;

/// <summary>用户显式添加/保存入口。初始化不读取key、不启动任务，事务失败不丢旧配置。</summary>
public sealed class TaskWorkspace(JsonTaskCatalogStore store)
{
    private readonly SemaphoreSlim _changes = new(1, 1);
    private TaskCatalog? _catalog;
    public TaskCatalog Catalog => _catalog ?? throw new InvalidOperationException("请先初始化任务配置。");
    public JsonTaskCatalogStore Storage => store;

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _changes.WaitAsync(token).ConfigureAwait(false);
        try { _catalog ??= await store.LoadAsync(token).ConfigureAwait(false); }
        finally { _changes.Release(); }
    }

    public async Task<TaskConfiguration> AddAsync(string credential, TaskConfiguration? configuration = null,
        CancellationToken token = default)
    {
        DpapiCredentialStore.ValidateCredential(credential);
        await _changes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = Catalog;
            var task = configuration ?? new();
            task.Validate();
            if (task.UsesLegacyStorage || current.Tasks.Any(existing => existing.Id == task.Id))
                throw new ArgumentException("新任务标识无效或重复。");
            task = task with
            {
                DisplayName = string.IsNullOrWhiteSpace(task.DisplayName) ? $"key-{current.Tasks.Length + 1}" : task.DisplayName.Trim(),
                QuotaGroup = task.QuotaGroup.Trim(), CredentialSaved = true, NextRunUtc = null, PlanRequested = false
            };
            task.Validate();
            if (task.DisplayName.Contains(credential, StringComparison.Ordinal) || task.QuotaGroup.Contains(credential, StringComparison.Ordinal))
                throw new ArgumentException("名称和额度组不能包含API key。");
            foreach (var existing in current.Tasks.Where(existing => existing.CredentialSaved))
            {
                // 只在显式添加时解密比较，不持久化key摘要、明文或原始异常。
                var saved = await store.Credentials(existing).LoadAsync(token).ConfigureAwait(false);
                if (saved == credential) throw new ArgumentException("该API key已有任务，请使用原任务；相同key不得重复运行。");
                RejectCredentialInLabels(task, saved);
            }
            var updated = current with { Tasks = [.. current.Tasks, task] };
            updated.Validate();
            await store.Credentials(task).SaveAsync(credential, token).ConfigureAwait(false);
            // 若索引失败，仅留下未引用的新密文，不覆盖任何旧凭据；内存索引也保持原值。
            await store.SaveAsync(updated, token).ConfigureAwait(false);
            _catalog = updated;
            return task;
        }
        finally { _changes.Release(); }
    }

    public async Task SaveTaskAsync(TaskConfiguration task, CancellationToken token = default)
    {
        task.Validate();
        await _changes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var previous = Catalog.Tasks.SingleOrDefault(item => item.Id == task.Id)
                ?? throw new ArgumentException("任务不存在。");
            if (task.UsesLegacyStorage != previous.UsesLegacyStorage || task.CredentialSaved != previous.CredentialSaved)
                throw new ArgumentException("不能通过普通配置修改凭据存储位置。");
            if (task.DisplayName != previous.DisplayName || task.QuotaGroup != previous.QuotaGroup)
                foreach (var item in Catalog.Tasks.Where(item => item.CredentialSaved))
                    RejectCredentialInLabels(task, await store.Credentials(item).LoadAsync(token).ConfigureAwait(false));
            var updated = Catalog with { Tasks = Catalog.Tasks.Select(item => item.Id == task.Id ? task : item).ToArray() };
            await store.SaveAsync(updated, token).ConfigureAwait(false);
            _catalog = updated;
        }
        finally { _changes.Release(); }
    }

    public async Task SetConcurrencyAsync(int maximum, CancellationToken token = default)
    {
        await _changes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var updated = Catalog with { MaximumConcurrency = maximum };
            updated.Validate();
            await store.SaveAsync(updated, token).ConfigureAwait(false);
            _catalog = updated;
        }
        finally { _changes.Release(); }
    }

    public async Task<TaskConfiguration> UpdatePlanStateAsync(Guid id, bool requested, DateTimeOffset? next,
        CancellationToken token = default)
    {
        await _changes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // 在同一配置锁内基于最新值只更新计划状态，不拿旧整项快照覆盖目标/间隔/启用。
            var task = Catalog.Tasks.Single(item => item.Id == id) with { PlanRequested = requested, NextRunUtc = next };
            var updated = Catalog with { Tasks = Catalog.Tasks.Select(item => item.Id == id ? task : item).ToArray() };
            await store.SaveAsync(updated, token).ConfigureAwait(false);
            _catalog = updated;
            return task;
        }
        finally { _changes.Release(); }
    }

    private static void RejectCredentialInLabels(TaskConfiguration task, string? credential)
    {
        if (!string.IsNullOrEmpty(credential) && (task.DisplayName.Contains(credential, StringComparison.Ordinal)
            || task.QuotaGroup.Contains(credential, StringComparison.Ordinal)))
            throw new ArgumentException("名称和额度组不能包含API key。");
    }
}
