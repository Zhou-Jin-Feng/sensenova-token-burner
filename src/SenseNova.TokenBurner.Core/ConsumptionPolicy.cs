namespace SenseNova.TokenBurner.Core;

/// <summary>用户已确认的经验校准表；不表示官方积分计费公式。</summary>
public static class ConsumptionPolicy
{
    public const int DefaultPercentage = 95;
    public const int MaximumPercentage = 95;

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
