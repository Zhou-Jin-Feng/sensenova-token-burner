using System.Net;
using System.Text;
using System.Text.Json;

namespace SenseNova.TokenBurner.Infrastructure.Api;

public enum MockScenario { Success, AuthenticationFailure, QuotaOrRateLimit, ServerFailure, MissingUsage, InvalidJson }

public static class MockCredential
{
    public const string Value = "mock-only-not-a-real-key";
}

/// <summary>终端处理器，不含 InnerHandler、网络连接或监听端口。</summary>
public sealed class MockSenseNovaHandler(MockScenario scenario = MockScenario.Success, TimeSpan? delay = null) : HttpMessageHandler
{
    private int _requestCount;
    private int _successfulProbeCount;
    private int _successfulCompletionCount;
    public int RequestCount => Volatile.Read(ref _requestCount);
    public int SuccessfulProbeCount => Volatile.Read(ref _successfulProbeCount);
    public int SuccessfulCompletionCount => Volatile.Read(ref _successfulCompletionCount);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        await Task.Delay(delay ?? TimeSpan.FromMilliseconds(80), cancellationToken);
        if (request.RequestUri is not { Scheme: "https", Host: "token.sensenova.cn" })
            return Reply(HttpStatusCode.BadRequest, "{}");
        if (request.Headers.Authorization?.Scheme != "Bearer" || !(request.Headers.Authorization.Parameter == MockCredential.Value
            || request.Headers.Authorization.Parameter?.StartsWith(MockCredential.Value + "-", StringComparison.Ordinal) == true)
            || scenario == MockScenario.AuthenticationFailure)
            return Reply(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"mock secret must not be echoed\"}}");
        if (scenario == MockScenario.QuotaOrRateLimit)
            return Reply(HttpStatusCode.TooManyRequests, "{\"error\":{\"type\":\"quota_exceeded_error\"}}");
        if (scenario == MockScenario.ServerFailure) return Reply(HttpStatusCode.ServiceUnavailable, "{}");
        if (scenario == MockScenario.InvalidJson) return Reply(HttpStatusCode.OK, "not-json");
        if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/v1/models")
            return Reply(HttpStatusCode.OK, """
                {"data":[
                  {"id":"sensenova-6.8-flash-lite","context_length":262144,"max_output_length":65536},
                  {"id":"mock-unverified-model","context_length":8192,"max_output_length":512}
                ]}
                """);
        if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/v1/chat/completions")
        {
            // 直接读UTF-8 JSON，避免完整负载先复制为大UTF-16文本再重新编码。
            await using var stream = await request.Content!.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = payload.RootElement;
            var outputLimit = root.GetProperty("max_tokens").GetInt32();
            var messages = root.GetProperty("messages");
            if (messages.GetArrayLength() != 1) return Reply(HttpStatusCode.BadRequest, "{}");
            var input = messages[0].GetProperty("content").GetString();
            var probe = outputLimit == Core.RequestBaseline.ProbeOutputTokens && input == "请只回复 OK。";
            if (root.GetProperty("model").GetString() != SenseNovaDefaults.RebateModel
                || root.GetProperty("stream").GetBoolean()
                || messages[0].GetProperty("role").GetString() != "user"
                || string.IsNullOrWhiteSpace(input) || input.Length > Core.RequestBaseline.InputCharacterTarget
                || outputLimit <= 0 || outputLimit > Core.RequestBaseline.MaximumOutputTokens)
                return Reply(HttpStatusCode.BadRequest, "{}");
            if (scenario != MockScenario.MissingUsage)
            {
                if (probe) Interlocked.Increment(ref _successfulProbeCount);
                else Interlocked.Increment(ref _successfulCompletionCount);
            }
            // 仅mock约定，绝非官方tokenizer：完整请求虚拟输入usage为字符数的一半。
            var inputTokens = probe ? 128 : (input.Length + 1) / 2;
            var outputTokens = Math.Min(8, outputLimit);
            var success = JsonSerializer.Serialize(new
            {
                choices = Array.Empty<object>(),
                usage = new { prompt_tokens = inputTokens, completion_tokens = outputTokens, total_tokens = inputTokens + outputTokens }
            });
            return Reply(HttpStatusCode.OK, scenario == MockScenario.MissingUsage ? "{\"choices\":[]}" :
                success);
        }
        return Reply(HttpStatusCode.NotFound, "{}");
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string payload)
        => new(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
}
