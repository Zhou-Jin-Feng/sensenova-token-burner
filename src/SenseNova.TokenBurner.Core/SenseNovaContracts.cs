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

public sealed class SenseNovaApiException(ApiFailureKind kind, string message) : Exception(message)
{
    public ApiFailureKind Kind { get; } = kind;
}

public static class RequestBaseline
{
    public const int Concurrency = 3;
    public const int InputCharacterTarget = 340_000;
    public const int MaximumOutputTokens = 1024;
    public const int ProbeOutputTokens = 32;
}
