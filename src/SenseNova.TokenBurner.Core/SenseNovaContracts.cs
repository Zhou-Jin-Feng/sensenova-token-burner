namespace SenseNova.TokenBurner.Core;

public sealed record ModelInfo(string Id, long? ContextLength, long? MaxOutputLength, bool IsRebateEligible)
{
    public string DisplayName => IsRebateEligible ? $"{Id}（返赠活动）" : $"{Id}（未确认返赠资格）";
}

public sealed record TokenUsage(long InputTokens, long OutputTokens, long TotalTokens);

public interface ISenseNovaClient
{
    Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string credential, CancellationToken cancellationToken = default);
    Task<TokenUsage> ProbeAsync(string credential, ModelInfo model, CancellationToken cancellationToken = default);
}

public enum ApiFailureKind { Authentication, RateOrQuotaLimit, InvalidRequest, ModelUnavailable, Transient, Protocol, Network }

// 只能由可信适配器依据执行阶段赋值，不能相信错误正文中的“未扣费”等声明。
public enum RequestDelivery { Unknown, NotSent, Rejected }

public sealed class SenseNovaApiException : Exception
{
    public SenseNovaApiException(ApiFailureKind kind, string message,
        RequestDelivery delivery = RequestDelivery.Unknown, TimeSpan? retryAfter = null) : base(message)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(delivery)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (retryAfter is { } delay && (delay < TimeSpan.Zero || delay > TimeSpan.FromMinutes(5)))
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        Kind = kind;
        Delivery = delivery;
        RetryAfter = retryAfter;
    }

    public ApiFailureKind Kind { get; }
    public RequestDelivery Delivery { get; }
    public TimeSpan? RetryAfter { get; }
}

public static class RequestBaseline
{
    public const int Concurrency = 10;
    public const int DefaultConcurrency = 3;
    public const int InputCharacterTarget = 340_000;
    public const int MaximumOutputTokens = 1024;
    public const int ProbeOutputTokens = 32;
}
