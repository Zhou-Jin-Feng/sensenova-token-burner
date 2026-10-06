using System.Text.Json.Serialization;

namespace SenseNova.TokenBurner.Core;

/// <summary>只含公开任务设置；凭据独立加密保存，不进入配置或运行记录。</summary>
public sealed record TaskConfiguration
{
    [JsonRequired] public Guid Id { get; init; } = Guid.NewGuid();
    public string DisplayName { get; init; } = "";
    public string QuotaGroup { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int Percentage { get; init; } = ConsumptionPolicy.DefaultPercentage;
    public long? CustomTargetTokens { get; init; }
    public decimal IntervalHours { get; init; } = RunDefaults.IntervalHours;
    public bool PlanRequested { get; init; }
    public DateTimeOffset? NextRunUtc { get; init; }
    public string ModelId { get; init; } = "sensenova-6.8-flash-lite";
    // 旧版任务继续使用原数据目录，升级不移动/覆盖既有文件。
    [JsonRequired] public bool UsesLegacyStorage { get; init; }
    [JsonRequired] public bool CredentialSaved { get; init; } = true;
    public long TargetTokens => CustomTargetTokens ?? ConsumptionPolicy.GetTargetTokens(Percentage);

    public void Validate()
    {
        if (Id == Guid.Empty || DisplayName is null || QuotaGroup is null || DisplayName.Length > 80 || QuotaGroup.Length > 80
            || DisplayName.Any(char.IsControl) || QuotaGroup.Any(char.IsControl))
            throw new ArgumentException("任务名称、额度组或标识无效。");
        _ = ConsumptionPolicy.GetTargetTokens(Percentage);
        if (CustomTargetTokens is < 0 or > ConsumptionPolicy.MaximumTargetTokens)
            throw new ArgumentException("自定义目标须为0至1万亿tokens。");
        new AppSettings { ModelId = ModelId, Percentage = Percentage, IntervalHours = IntervalHours }.Validate();
        _ = new ScheduleConfiguration(new(ModelId, TargetTokens), IntervalHours).Interval;
        if (NextRunUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new ArgumentException("计划时间须使用UTC。");
    }
}

public sealed record TaskCatalog(int SchemaVersion, int MaximumConcurrency, TaskConfiguration[] Tasks)
{
    public static TaskCatalog Empty => new(1, RequestBaseline.Concurrency, []);
    public void Validate()
    {
        if (SchemaVersion != 1 || MaximumConcurrency is < 1 or > RequestBaseline.Concurrency || Tasks is null)
            throw new ArgumentException("任务配置版本或全局并发无效。");
        var ids = new HashSet<Guid>();
        foreach (var task in Tasks)
        {
            if (task is null || !ids.Add(task.Id)) throw new ArgumentException("任务标识重复。");
            task.Validate();
        }
        if (Tasks.Count(task => task.UsesLegacyStorage) > 1) throw new ArgumentException("旧版任务重复。");
    }
}

public interface ITaskCatalogStore
{
    Task<TaskCatalog> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TaskCatalog catalog, CancellationToken cancellationToken = default);
}

public static class TargetRisk
{
    public static string Describe(long target) => target > ConsumptionPolicy.GetTargetTokens(95)
        ? "超过1.2亿经验目标，可能扣除通用积分。实际扣减与返赠以官方记录为准。"
        : target < ConsumptionPolicy.GetTargetTokens(95)
            ? "低于1.2亿经验目标，可能没有用满专属积分；仍不能保证只扣专属积分或精确返赠。"
            : "1.2亿tokens/约58,000点是特定条件的经验估算，不能保证只扣专属积分或精确返赠。";
}
