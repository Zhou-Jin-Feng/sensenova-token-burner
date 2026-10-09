using System.Text;
using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Api;

/// <summary>复用同一不可变输入；调用方负责凭据作用域和显式启动，不写入快照。</summary>
public sealed class CompletionRunExecutor : IRunRequestExecutor
{
    private readonly ISenseNovaCompletionClient _client;
    private readonly string _credential;
    private readonly ModelInfo _model;
    private readonly CompletionRequest _request;


    public CompletionRunExecutor(ISenseNovaCompletionClient client, string credential, ModelInfo model, CompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateRequest(model, request);
        Storage.DpapiCredentialStore.ValidateCredential(credential);
        _client = client;
        _credential = credential;
        _model = model;
        _request = request;
    }

    public void ValidateParameters(RunParameters parameters)
    {
        if (parameters.ModelId != _model.Id || parameters.RequestTokenReservation < _request.EstimatedTotalTokens)
            throw new ArgumentException("运行模型或每请求预留量与完整请求不匹配。");
    }

    public bool AutoRetryTransientErrors { get; set; } = false;

    public async Task<TokenUsage> ExecuteAsync(RunParameters parameters, CancellationToken cancellationToken)
    {
        ValidateParameters(parameters);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await _client.CompleteAsync(_credential, _model, _request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SenseNovaApiException ex) when (!AutoRetryTransientErrors || ex.Kind is ApiFailureKind.Authentication or ApiFailureKind.InvalidRequest or ApiFailureKind.ModelUnavailable)
            {
                throw;
            }
            catch (Exception)
            {
                if (!AutoRetryTransientErrors) throw;
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static void ValidateRequest(ModelInfo model, CompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        if (!model.IsRebateEligible || model.Id != SenseNovaDefaults.RebateModel)
            throw new SenseNovaApiException(ApiFailureKind.ModelUnavailable, "完整返赠请求只允许已确认的 Flash-Lite 模型。", RequestDelivery.NotSent);
        request.Validate(model);
    }
}

/// <summary>合成开发输入，不继承旧脚本内容或计费校准；调用方须在界面线程外生成。</summary>
public static class MockCompletionInput
{
    public static CompletionRequest Create(long estimatedInputTokens, int characterTarget = RequestBaseline.InputCharacterTarget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(characterTarget, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(characterTarget, RequestBaseline.InputCharacterTarget);
        ArgumentOutOfRangeException.ThrowIfLessThan(estimatedInputTokens, 1);
        const string text = "这是一段用于开发验证的合成文本。请阅读并简要概括。";
        var input = new StringBuilder(characterTarget);
        while (input.Length < characterTarget)
            input.Append(text.AsSpan(0, Math.Min(text.Length, characterTarget - input.Length)));
        return new(input.ToString(), RequestBaseline.MaximumOutputTokens, estimatedInputTokens);
    }
}
