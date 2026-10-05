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
public sealed class CompletionRequestTests
{
    private static readonly ModelInfo Model = new(SenseNovaDefaults.RebateModel, 262144, 65536, true);
    private static HttpClient Http(HttpMessageHandler handler) => new(handler)
        { BaseAddress = new Uri(SenseNovaDefaults.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(5) };
    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [TestMethod]
    public async Task ConcurrentReusedInputKeepsIndependentCredentialsAndValidUtf8Payload()
    {
        const string input = "中文输入 😀 \"引号\"\n<script>&\u2028";
        var completion = new CompletionRequest(input, 1024, 100);
        var messages = new System.Collections.Concurrent.ConcurrentDictionary<HttpRequestMessage, byte>();
        using var http = Http(new TestHttpHandler(async (request, cancellationToken) =>
        {
            Assert.IsTrue(messages.TryAdd(request, 0));
            Assert.IsTrue(request.Headers.Authorization!.Parameter is "virtual-first-key" or "virtual-second-key");
            Assert.AreEqual("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.AreEqual("utf-8", request.Content.Headers.ContentType.CharSet);
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Assert.IsTrue(Encoding.UTF8.GetString(bytes).Contains("中文输入", StringComparison.Ordinal));
            using var payload = JsonDocument.Parse(bytes);
            Assert.AreEqual(input, payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.IsFalse(Encoding.UTF8.GetString(bytes).Contains("virtual-first-key", StringComparison.Ordinal));
            await Task.Delay(1, cancellationToken);
            return Reply("{\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}");
        }));
        var client = new SenseNovaHttpClient(http);
        var tasks = Enumerable.Range(0, 6).Select(index => client.CompleteAsync(
            index % 2 == 0 ? "virtual-first-key" : "virtual-second-key", Model, completion));
        var results = await Task.WhenAll(tasks);
        Assert.AreEqual(6, messages.Count);
        Assert.IsTrue(results.All(result => result == new TokenUsage(1, 2, 3)));
    }

    [TestMethod]
    public async Task ChangedImmutableRequestDoesNotReusePreviousInputOrOutputLimit()
    {
        var observed = new List<(string? Input, int Output)>();
        using var http = Http(new TestHttpHandler(async (request, token) =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            observed.Add((payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(),
                payload.RootElement.GetProperty("max_tokens").GetInt32()));
            return Reply("{\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}");
        }));
        var client = new SenseNovaHttpClient(http);
        var initial = new CompletionRequest("first", 1024, 100);
        await client.CompleteAsync(MockCredential.Value, Model, initial);
        await client.CompleteAsync(MockCredential.Value, Model, initial with { Input = "第二笔", MaximumOutputTokens = 16 });
        Assert.AreEqual(("first", 1024), observed[0]);
        Assert.AreEqual(("第二笔", 16), observed[1]);
    }

    [TestMethod]
    public async Task FullBaselinePayloadUsesLargeInput1024OutputAndSharedUsageParser()
    {
        var completion = MockCompletionInput.Create(170000);
        var calls = 0;
        using var http = Http(new TestHttpHandler(async (request, cancellationToken) =>
        {
            calls++;
            Assert.AreEqual(SenseNovaDefaults.BaseUrl + "/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual(MockCredential.Value, request.Headers.Authorization?.Parameter);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.AreEqual(Model.Id, payload.RootElement.GetProperty("model").GetString());
            Assert.AreEqual(1024, payload.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.IsFalse(payload.RootElement.GetProperty("stream").GetBoolean());
            var messages = payload.RootElement.GetProperty("messages");
            Assert.AreEqual(1, messages.GetArrayLength());
            Assert.AreEqual("user", messages[0].GetProperty("role").GetString());
            Assert.AreEqual(completion.Input, messages[0].GetProperty("content").GetString());
            Assert.AreEqual(340000, completion.Input.Length);
            // 预估是客户端预算信息，不作为额外字段传给服务器。
            Assert.IsFalse(payload.RootElement.TryGetProperty("estimated_input_tokens", out _));
            return Reply("{\"usage\":{\"prompt_tokens\":170000,\"completion_tokens\":8,\"total_tokens\":170008}}");
        }));
        Assert.AreEqual(new TokenUsage(170000, 8, 170008),
            await new SenseNovaHttpClient(http).CompleteAsync(MockCredential.Value, Model, completion));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow("", 1024, 100L)]
    [DataRow("  ", 1024, 100L)]
    [DataRow("input", 0, 100L)]
    [DataRow("input", 1025, 100L)]
    [DataRow("input", 1024, 0L)]
    [DataRow("input", 1024, -1L)]
    [DataRow("input", 1024, long.MaxValue)]
    public async Task InvalidFullRequestNeverSends(string input, int output, long estimate)
    {
        using var handler = new MockSenseNovaHandler();
        using var http = Http(handler);
        var failure = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() =>
            new SenseNovaHttpClient(http).CompleteAsync(MockCredential.Value, Model, new(input, output, estimate)));
        Assert.AreEqual(ApiFailureKind.InvalidRequest, failure.Kind);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task UnsupportedModelOrUnknownCapabilitiesAndContextAreRejectedBeforeSend()
    {
        using var handler = new MockSenseNovaHandler();
        using var http = Http(handler);
        var client = new SenseNovaHttpClient(http);
        var completion = new CompletionRequest("input", 1024, 2000);
        foreach (var model in new[]
        {
            Model with { Id = "other" }, Model with { IsRebateEligible = false },
            Model with { ContextLength = null }, Model with { MaxOutputLength = null },
            Model with { ContextLength = 3000 }, Model with { MaxOutputLength = 512 }
        })
            await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() => client.CompleteAsync(MockCredential.Value, model, completion));
        await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() =>
            client.CompleteAsync(MockCredential.Value, Model, completion with { Input = new string('x', 340001) }));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public void CompletionExecutorRejectsModelMismatchOrMissingReservationBeforeRun()
    {
        using var handler = new MockSenseNovaHandler();
        using var http = Http(handler);
        var request = new CompletionRequest("input", 1024, 100);
        var executor = new CompletionRunExecutor(new SenseNovaHttpClient(http), MockCredential.Value, Model, request);
        var engine = new SingleRunEngine(executor);
        Assert.Throws<ArgumentException>(() => engine.RunAsync(new(Model.Id, 5000)));
        Assert.Throws<ArgumentException>(() => engine.RunAsync(new("other", 5000) { RequestTokenReservation = 1124 }));
        Assert.AreEqual(RunSnapshot.Idle, engine.Snapshot);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task BuiltInFullHttpMockRunsConservativeScaledRoundWithoutProbeCalls()
    {
        using var handler = new MockSenseNovaHandler(delay: TimeSpan.Zero);
        using var http = Http(handler);
        var client = new SenseNovaHttpClient(http);
        var models = await client.GetModelsAsync(MockCredential.Value);
        var request = MockCompletionInput.Create(170000);
        var engine = new SingleRunEngine(new CompletionRunExecutor(client, MockCredential.Value, models[0], request));
        var result = await engine.RunAsync(new(models[0].Id, 500000)
        {
            MaximumConcurrency = 3,
            RequestTokenReservation = request.EstimatedTotalTokens
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(RunState.Completed, result.State);
        Assert.AreEqual(RunEndReason.BudgetRemainder, result.EndReason);
        Assert.AreEqual(new TokenUsage(340000, 16, 340016), result.ConfirmedUsage);
        Assert.AreEqual(2L, result.CompletedRequests);
        Assert.AreEqual(0, result.UnknownUsageRequests);
        Assert.AreEqual(0L, result.ReservedTokens);
        Assert.AreEqual(2, handler.SuccessfulCompletionCount);
        Assert.AreEqual(0, handler.SuccessfulProbeCount);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("{}")]
    [DataRow("{\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":4}}")]
    [DataRow("{\"usage\":{\"prompt_tokens\":-1,\"completion_tokens\":2,\"total_tokens\":1}}")]
    public async Task InvalidFullUsageUsesSameProtocolFailureAsProbe(string json)
    {
        using var http = Http(new TestHttpHandler((_, _) => Task.FromResult(Reply(json))));
        var failure = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() =>
            new SenseNovaHttpClient(http).CompleteAsync(MockCredential.Value, Model, new("input", 1024, 100)));
        Assert.AreEqual(ApiFailureKind.Protocol, failure.Kind);
    }

    [TestMethod]
    [DataRow(401, ApiFailureKind.Authentication)]
    [DataRow(429, ApiFailureKind.RateOrQuotaLimit)]
    [DataRow(503, ApiFailureKind.Transient)]
    public async Task FullRequestErrorsRemainSanitized(int status, ApiFailureKind expected)
    {
        using var http = Http(new TestHttpHandler((_, _) => Task.FromResult(Reply(MockCredential.Value, (HttpStatusCode)status))));
        var failure = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() =>
            new SenseNovaHttpClient(http).CompleteAsync(MockCredential.Value, Model, new("input", 1024, 100)));
        Assert.AreEqual(expected, failure.Kind);
        Assert.IsFalse(failure.ToString().Contains(MockCredential.Value, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FullRequestCancellationReleasesHttpWaitWithoutRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = Http(new TestHttpHandler(async (_, token) =>
        {
            calls++;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Reply("{}");
        }));
        using var cancellation = new CancellationTokenSource();
        var request = new SenseNovaHttpClient(http).CompleteAsync(MockCredential.Value, Model, new("input", 1024, 100), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => request);
        Assert.AreEqual(1, calls);
    }
}
