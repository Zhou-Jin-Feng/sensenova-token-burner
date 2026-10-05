namespace SenseNova.TokenBurner.Core;

/// <summary>应用内保护时限，不能保证服务端停止或积分硬上限。</summary>
public sealed record RunProtectionOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(15);
    public int MaximumRetryAttempts { get; init; } = 3;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan MaximumTotalRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ValidateTime(RequestTimeout, TimeSpan.FromMinutes(10));
        ValidateTime(StopGracePeriod, TimeSpan.FromMinutes(1));
        ValidateTime(InitialRetryDelay, TimeSpan.FromMinutes(1));
        ValidateTime(MaximumRetryDelay, TimeSpan.FromMinutes(1));
        ValidateTime(MaximumTotalRetryDelay, TimeSpan.FromMinutes(5));
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumRetryAttempts);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumRetryAttempts, 10);
        if (InitialRetryDelay > MaximumRetryDelay)
            throw new ArgumentException("首次退避不能超过单次退避上限。");
    }

    private static void ValidateTime(TimeSpan value, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum)
            throw new ArgumentOutOfRangeException(nameof(value), "保护时限必须为范围内的正数。");
    }
}
