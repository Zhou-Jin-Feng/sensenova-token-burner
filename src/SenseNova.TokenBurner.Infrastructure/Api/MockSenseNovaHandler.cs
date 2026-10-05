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
    public int RequestCount => Volatile.Read(ref _requestCount);
    public int SuccessfulProbeCount => Volatile.Read(ref _successfulProbeCount);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        await Task.Delay(delay ?? TimeSpan.FromMilliseconds(80), cancellationToken);
        if (request.RequestUri is not { Scheme: "https", Host: "token.sensenova.cn" })
            return Reply(HttpStatusCode.BadRequest, "{}");
        if (request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Authorization.Parameter != MockCredential.Value
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
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (payload.RootElement.GetProperty("model").GetString() != SenseNovaDefaults.RebateModel
                || payload.RootElement.GetProperty("stream").GetBoolean()
                || payload.RootElement.GetProperty("max_tokens").GetInt32() != Core.RequestBaseline.ProbeOutputTokens)
                return Reply(HttpStatusCode.BadRequest, "{}");
            if (scenario != MockScenario.MissingUsage) Interlocked.Increment(ref _successfulProbeCount);
            return Reply(HttpStatusCode.OK, scenario == MockScenario.MissingUsage ? "{\"choices\":[]}" :
                "{\"choices\":[],\"usage\":{\"prompt_tokens\":128,\"completion_tokens\":8,\"total_tokens\":136}}");
        }
        return Reply(HttpStatusCode.NotFound, "{}");
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string payload)
        => new(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
}
