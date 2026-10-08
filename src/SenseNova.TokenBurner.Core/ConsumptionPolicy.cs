namespace SenseNova.TokenBurner.Core;

/// <summary>用户已确认的经验校准表；不表示官方积分计费公式。</summary>
public static class ConsumptionPolicy
{
    public const int DefaultPercentage = 95;
    public const int MaximumPercentage = 95;
    // 自定义目标独立于比例档位；范围仅用于数值/累计保护，不代表积分安全上限。
    public const long MaximumTargetTokens = 1_000_000_000_000;
    /// <summary>周专属积分经验换算比例：6 万分约对应 1.2 亿 tokens，即 1 积分 = 2,000 tokens。</summary>
    public const long TokensPerWeeklyPoint = 2_000;
    public const long DefaultWeeklyQuotaPoints = 600_000;

    public static long TokensToPoints(long tokens) => tokens / TokensPerWeeklyPoint;
    public static long PointsToTokens(long points) => points * TokensPerWeeklyPoint;

    private static readonly (int Percentage, long Tokens)[] Calibration =
    [
        (0, 0),
        (25, 30_000_000),
        (50, 60_000_000),
        (75, 90_000_000),
        (95, 120_000_000)
    ];

    public static long GetTargetTokens(int percentage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percentage);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentage, MaximumPercentage);

        for (var index = 1; index < Calibration.Length; index++)
        {
            var lower = Calibration[index - 1];
            var upper = Calibration[index];
            if (percentage <= upper.Percentage)
            {
                return lower.Tokens + (upper.Tokens - lower.Tokens)
                    * (percentage - lower.Percentage) / (upper.Percentage - lower.Percentage);
            }
        }

        throw new InvalidOperationException("校准表未覆盖已允许的比例。");
    }
}
