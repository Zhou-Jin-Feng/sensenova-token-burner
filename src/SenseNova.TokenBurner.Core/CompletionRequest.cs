namespace SenseNova.TokenBurner.Core;

/// <summary>完整请求；输入token预估必须显式提供，不能由字符数冒充。</summary>
public sealed record CompletionRequest(string Input, int MaximumOutputTokens, long EstimatedInputTokens)
{
    public long EstimatedTotalTokens => checked(EstimatedInputTokens + MaximumOutputTokens);

    public void Validate(ModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(Input) || Input.Length > RequestBaseline.InputCharacterTarget
            || MaximumOutputTokens <= 0 || MaximumOutputTokens > RequestBaseline.MaximumOutputTokens
            || EstimatedInputTokens <= 0 || EstimatedInputTokens > ConsumptionPolicy.GetTargetTokens(95))
            throw new SenseNovaApiException(ApiFailureKind.InvalidRequest, "完整请求输入、输出上限或输入 token 预估无效。", RequestDelivery.NotSent);
        if (model.ContextLength is null or <= 0 || model.MaxOutputLength is null or <= 0)
            throw new SenseNovaApiException(ApiFailureKind.InvalidRequest, "缺少模型上下文或输出上限，无法预检完整请求。", RequestDelivery.NotSent);
        if (MaximumOutputTokens > model.MaxOutputLength || EstimatedTotalTokens > model.ContextLength)
            throw new SenseNovaApiException(ApiFailureKind.InvalidRequest, "完整请求预估超出模型上下文或输出上限。", RequestDelivery.NotSent);
    }
}

public interface ISenseNovaCompletionClient
{
    Task<TokenUsage> CompleteAsync(string credential, ModelInfo model, CompletionRequest request,
        CancellationToken cancellationToken = default);
}
