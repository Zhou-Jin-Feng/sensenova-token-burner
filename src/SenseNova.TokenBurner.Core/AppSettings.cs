namespace SenseNova.TokenBurner.Core;

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public int Percentage { get; init; } = ConsumptionPolicy.DefaultPercentage;
    public decimal IntervalHours { get; init; } = RunDefaults.IntervalHours;
    public string ModelId { get; init; } = "sensenova-6.8-flash-lite";
    public bool RememberCredential { get; init; } = true;

    public void Validate()
    {
        if (SchemaVersion != 1) throw new ArgumentException("配置版本不受支持。");
        _ = ConsumptionPolicy.GetTargetTokens(Percentage);
        if (IntervalHours <= 0 || IntervalHours > (decimal)TimeSpan.MaxValue.TotalHours)
            throw new ArgumentException("运行间隔必须为有效的正数小时。");
        if (string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 200)
            throw new ArgumentException("模型标识无效。");
    }
}

public interface IUserSettingsStore
{
    Task<AppSettings?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ICredentialStore
{
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string credential, CancellationToken cancellationToken = default);
}

public sealed class LocalStorageException(string message) : Exception(message);
