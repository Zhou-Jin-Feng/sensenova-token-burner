using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class HttpClientTests
{
    private static readonly ModelInfo EligibleModel = new(SenseNovaDefaults.RebateModel, 262144, 65536, true);
    private static HttpClient Http(HttpMessageHandler handler) => new(handler)
        { BaseAddress = new Uri(SenseNovaDefaults.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(5) };
    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [TestMethod]
    public async Task RequestsUseOfficialPathsBearerAndShortNonStreamingPayload()
    {
        var calls = 0;
        using var http = Http(new TestHttpHandler(async (request, token) =>
        {
            calls++;
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual(MockCredential.Value, request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Get)
            {
                Assert.AreEqual(SenseNovaDefaults.BaseUrl + "/models", request.RequestUri?.AbsoluteUri);
                return Reply("{\"data\":[{\"id\":\"sensenova-6.8-flash-lite\",\"context_length\":262144,\"max_output_length\":65536},{\"id\":\"other\"}]}");
            }
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual(SenseNovaDefaults.BaseUrl + "/chat/completions", request.RequestUri?.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.AreEqual(SenseNovaDefaults.RebateModel, body.RootElement.GetProperty("model").GetString());
            Assert.AreEqual(32, body.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.IsFalse(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.AreEqual("user", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
            return Reply("{\"usage\":{\"prompt_tokens\":128,\"completion_tokens\":8,\"total_tokens\":136}}");
        }));
        var client = new SenseNovaHttpClient(http);
        var models = await client.GetModelsAsync(MockCredential.Value);
        Assert.IsTrue(models[0].IsRebateEligible);
        Assert.IsFalse(models[1].IsRebateEligible);
        Assert.IsNull(models[1].ContextLength);
        Assert.AreEqual(new TokenUsage(128, 8, 136), await client.ProbeAsync(MockCredential.Value, models[0]));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"data\":{}}")]
    [DataRow("{\"data\":[{\"id\":\"\"}]}")]
    [DataRow("{\"data\":[{\"id\":\"a\"},{\"id\":\"a\"}]}")]
    [DataRow("{\"data\":[{\"id\":\"a\",\"context_length\":-1}]}")]
    public async Task MalformedModelListIsRejected(string body)
    {
        using var http = Http(new TestHttpHandler((_, _) => Task.FromResult(Reply(body))));
        var failure = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() => new SenseNovaHttpClient(http).GetModelsAsync(MockCredential.Value));
        Assert.AreEqual(ApiFailureKind.Protocol, failure.Kind);
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("{}")]
    [DataRow("{\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}")]
    [DataRow("{\"usage\":{\"prompt_tokens\":-1,\"completion_tokens\":2,\"total_tokens\":1}}")]
    [DataRow("{\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":4}}")]
    [DataRow("{\"usage\":{\"prompt_tokens\":1.5,\"completion_tokens\":2,\"total_tokens\":3}}")]
    [DataRow("{\"usage\":{\"prompt_tokens\":9223372036854775807,\"completion_tokens\":1,\"total_tokens\":0}}")]
    public async Task UnusableUsageNeverBecomesSuccessfulTokens(string body)
    {
        using var http = Http(new TestHttpHandler((_, _) => Task.FromResult(Reply(body))));
        var failure = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() => new SenseNovaHttpClient(http).ProbeAsync(MockCredential.Value, EligibleModel));
        Assert.AreEqual(ApiFailureKind.Protocol, failure.Kind);
        Assert.IsFalse(failure.ToString().Contains(MockCredential.Value, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(401, ApiFailureKind.Authentication)]
    [DataRow(403, ApiFailureKind.Authentication)]
    [DataRow(429, ApiFailureKind.RateOrQuotaLimit)]
    [DataRow(503, ApiFailureKind.Transient)]
    [DataRow(404, ApiFailureKind.ModelUnavailable)]
    [DataRow(400, ApiFailureKind.InvalidRequest)]
    public async Task StatusClassificationDoesNotEchoServerSecretsOrMark429Permanent(int status, ApiFailureKind kind)
    {
        using var http = Http(new TestHttpHandler((_, _) => Task.FromResult(Reply(MockCredential.Value, (HttpStatusCode)status))));
        var failure = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() => new SenseNovaHttpClient(http).GetModelsAsync(MockCredential.Value));
        Assert.AreEqual(kind, failure.Kind);
        Assert.IsFalse(failure.ToString().Contains(MockCredential.Value, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnknownModelUnconfirmedEligibilityOrInsufficientOutputAreRejectedBeforeSend()
    {
        using var handler = new MockSenseNovaHandler();
        using var http = Http(handler);
        var client = new SenseNovaHttpClient(http);
        foreach (var model in new[] { EligibleModel with { Id = "other" }, EligibleModel with { IsRebateEligible = false }, EligibleModel with { MaxOutputLength = 1 } })
            await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() => client.ProbeAsync(MockCredential.Value, model));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public void NonOfficialBaseAddressIsRejected()
    {
        using var http = new HttpClient(new MockSenseNovaHandler()) { BaseAddress = new Uri("https://example.invalid/v1/") };
        Assert.ThrowsExactly<ArgumentException>(() => new SenseNovaHttpClient(http));
    }

    [TestMethod]
    public async Task CancellationEndsPendingRequestAndNextRequestWorks()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        using var http = Http(new TestHttpHandler(async (_, token) =>
        {
            if (first) { first = false; entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return Reply("{\"data\":[]}");
        }));
        var client = new SenseNovaHttpClient(http);
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetModelsAsync(MockCredential.Value, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual(0, (await client.GetModelsAsync(MockCredential.Value)).Count);
    }

    [TestMethod]
    public async Task TimeoutAndNetworkErrorsAreSanitized()
    {
        using var http = Http(new MockSenseNovaHandler(delay: TimeSpan.FromSeconds(10)));
        http.Timeout = TimeSpan.FromMilliseconds(50);
        var timeout = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() => new SenseNovaHttpClient(http).GetModelsAsync(MockCredential.Value));
        Assert.AreEqual(ApiFailureKind.Network, timeout.Kind);
        StringAssert.Contains(timeout.Message, "超时");
        using var brokenHttp = Http(new TestHttpHandler((_, _) => throw new HttpRequestException(MockCredential.Value)));
        var failure = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() => new SenseNovaHttpClient(brokenHttp).GetModelsAsync(MockCredential.Value));
        Assert.AreEqual(ApiFailureKind.Network, failure.Kind);
        Assert.IsFalse(failure.ToString().Contains(MockCredential.Value, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OversizeResponseIsRejected()
    {
        using var http = Http(new TestHttpHandler((_, _) => Task.FromResult(Reply(new string(' ', 1_048_577)))));
        var failure = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() => new SenseNovaHttpClient(http).GetModelsAsync(MockCredential.Value));
        Assert.AreEqual(ApiFailureKind.Protocol, failure.Kind);
    }

    [TestMethod]
    public async Task BuiltInMockCompletesWithoutNetworkAndProvidesDeterministicUsage()
    {
        using var handler = new MockSenseNovaHandler();
        using var http = Http(handler);
        var client = new SenseNovaHttpClient(http);
        var models = await client.GetModelsAsync(MockCredential.Value);
        Assert.AreEqual(2, models.Count);
        Assert.AreEqual(new TokenUsage(128, 8, 136), await client.ProbeAsync(MockCredential.Value, models[0]));
        Assert.AreEqual(2, handler.RequestCount);
    }
}

internal sealed class TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => send(request, cancellationToken);
}
