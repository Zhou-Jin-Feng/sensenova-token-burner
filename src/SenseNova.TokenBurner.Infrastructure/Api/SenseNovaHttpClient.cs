using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Infrastructure.Api;

public sealed class SenseNovaHttpClient : ISenseNovaClient, ISenseNovaCompletionClient
{
    private const int MaximumResponseBytes = 1_048_576;
    private readonly HttpClient _httpClient;

    public SenseNovaHttpClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (httpClient.BaseAddress != new Uri(SenseNovaDefaults.BaseUrl + "/"))
            throw new ArgumentException("HTTP 客户端必须使用固定官方服务地址。");
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string credential, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "models", credential);
        using var document = await SendAsync(request, cancellationToken);
        try
        {
            var data = document.RootElement.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Array) throw new JsonException();
            var models = new List<ModelInfo>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || !ids.Add(id)) throw new JsonException();
                models.Add(new ModelInfo(id, OptionalPositiveNumber(item, "context_length"),
                    OptionalPositiveNumber(item, "max_output_length"), id == SenseNovaDefaults.RebateModel));
            }
            return models;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw ProtocolFailure(); }
    }

    public async Task<TokenUsage> ProbeAsync(string credential, ModelInfo model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.IsRebateEligible || model.Id != SenseNovaDefaults.RebateModel)
            throw new SenseNovaApiException(ApiFailureKind.ModelUnavailable, "当前返赠模式只允许已确认的 Flash-Lite 模型。", RequestDelivery.NotSent);
        if (model.MaxOutputLength is < RequestBaseline.ProbeOutputTokens)
            throw new SenseNovaApiException(ApiFailureKind.InvalidRequest, "模型输出上限不足以执行当前测试请求。", RequestDelivery.NotSent);
        using var request = CreateRequest(HttpMethod.Post, "chat/completions", credential);
        request.Content = JsonContent.Create(new
        {
            model = model.Id,
            messages = new[] { new { role = "user", content = "请只回复 OK。" } },
            max_tokens = RequestBaseline.ProbeOutputTokens,
            stream = false
        });
        using var document = await SendAsync(request, cancellationToken);
        return ReadUsage(document);
    }

    public async Task<TokenUsage> CompleteAsync(string credential, ModelInfo model, CompletionRequest completion,
        CancellationToken cancellationToken = default)
    {
        CompletionRunExecutor.ValidateRequest(model, completion);
        using var request = CreateRequest(HttpMethod.Post, "chat/completions", credential);
        request.Content = JsonContent.Create(new
        {
            model = model.Id,
            messages = new[] { new { role = "user", content = completion.Input } },
            max_tokens = completion.MaximumOutputTokens,
            stream = false
        });
        using var document = await SendAsync(request, cancellationToken);
        return ReadUsage(document);
    }

    private static TokenUsage ReadUsage(JsonDocument document)
    {
        try
        {
            var usage = document.RootElement.GetProperty("usage");
            var input = usage.GetProperty("prompt_tokens").GetInt64();
            var output = usage.GetProperty("completion_tokens").GetInt64();
            var total = usage.GetProperty("total_tokens").GetInt64();
            if (input < 0 || output < 0 || total != checked(input + output)) throw new JsonException();
            return new TokenUsage(input, output, total);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw ProtocolFailure(); }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string credential)
    {
        DpapiCredentialStore.ValidateCredential(credential);
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) throw StatusFailure(response);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw ProtocolFailure();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, cancellationToken)) != 0)
            {
                if (buffer.Length + count > MaximumResponseBytes) throw ProtocolFailure();
                buffer.Write(chunk, 0, count);
            }
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { throw new SenseNovaApiException(ApiFailureKind.Network, "请求超时，用量可能未知，请先核查后再试。"); }
        catch (HttpRequestException)
        { throw new SenseNovaApiException(ApiFailureKind.Network, "无法完成请求，请检查服务连接。"); }
        catch (IOException)
        { throw new SenseNovaApiException(ApiFailureKind.Network, "响应读取中断，用量可能未知，请先核查。"); }
        catch (JsonException) { throw ProtocolFailure(); }
    }

    private static long? OptionalPositiveNumber(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var number = value.GetInt64();
        if (number <= 0) throw new JsonException();
        return number;
    }

    private static SenseNovaApiException ProtocolFailure()
        => new(ApiFailureKind.Protocol, "响应格式或用量信息无效，未将其记为成功用量。");

    private static SenseNovaApiException StatusFailure(HttpResponseMessage response)
        => (int)response.StatusCode switch
        {
            401 or 403 => new(ApiFailureKind.Authentication, "认证失败，请检查凭据。", RequestDelivery.Rejected),
            429 => new(ApiFailureKind.RateOrQuotaLimit, "请求限流或额度受限，请暂停并核查账户后再试。",
                RequestDelivery.Unknown, ReadRetryHint(response)),
            404 => new(ApiFailureKind.ModelUnavailable, "模型或接口当前不可用。", RequestDelivery.Rejected),
            400 or 405 or 413 or 422 => new(ApiFailureKind.InvalidRequest, "请求被拒绝，请检查模型与参数。", RequestDelivery.Rejected),
            408 => new(ApiFailureKind.Network, "服务返回请求超时，用量可能未知，请先核查。"),
            >= 500 => new(ApiFailureKind.Transient, "服务暂时不可用，用量可能未知，请先核查。"),
            _ => new(ApiFailureKind.InvalidRequest, "请求未被接受，请检查模型与参数。")
        };

    private static TimeSpan? ReadRetryHint(HttpResponseMessage response)
    {
        // 提示不证明是纯速率限制，也不证明未执行。不解析/记录服务端错误正文。
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        return delay >= TimeSpan.Zero && delay <= TimeSpan.FromMinutes(5) ? delay : null;
    }
}
