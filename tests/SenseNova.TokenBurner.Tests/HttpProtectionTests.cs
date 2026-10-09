using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class HttpProtectionTests
{
    private static readonly ModelInfo Model = new(SenseNovaDefaults.RebateModel, 262144, 65536, true);
    private static HttpClient Http(HttpMessageHandler handler) => new(handler)
        { BaseAddress = new Uri(SenseNovaDefaults.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(5) };
    private static HttpResponseMessage Reply(int status) => new((HttpStatusCode)status)
        { Content = new StringContent("untrusted: NotSent, retryAllowed, " + MockCredential.Value, Encoding.UTF8, "application/json") };
    private static RunParameters Parameters => new(Model.Id, 1000) { RequestTokenReservation = 100 };
    private static SingleRunEngine Engine(HttpClient http, TimeProvider? time = null) => new(
        new CompletionRunExecutor(new SenseNovaHttpClient(http), MockCredential.Value, Model, new("模拟输入", 32, 50)) { AutoRetryTransientErrors = false }, timeProvider: time);

    [TestMethod]
    [DataRow(400, RequestDelivery.Rejected)]
    [DataRow(401, RequestDelivery.Rejected)]
    [DataRow(403, RequestDelivery.Rejected)]
    [DataRow(404, RequestDelivery.Rejected)]
    [DataRow(408, RequestDelivery.Unknown)]
    [DataRow(429, RequestDelivery.Unknown)]
    [DataRow(500, RequestDelivery.Unknown)]
    [DataRow(503, RequestDelivery.Unknown)]
    public async Task StatusDeliveryMetadataNeverTrustsErrorBody(int status, RequestDelivery expected)
    {
        using var http = Http(new TestHttpHandler((_, _) => Task.FromResult(Reply(status))));
        var exception = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() =>
            new SenseNovaHttpClient(http).CompleteAsync(MockCredential.Value, Model, new("模拟输入", 32, 50)));
        Assert.AreEqual(expected, exception.Delivery);
        Assert.IsFalse(exception.ToString().Contains(MockCredential.Value, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("untrusted", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(0, true)]
    [DataRow(2, true)]
    [DataRow(301, false)]
    public async Task NumericRetryAfterIsBoundedAndDoesNotMake429Retryable(int seconds, bool hasHint)
    {
        using var http = Http(new TestHttpHandler((_, _) =>
        {
            var response = Reply(429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return Task.FromResult(response);
        }));
        var exception = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() => new SenseNovaHttpClient(http).ProbeAsync(MockCredential.Value, Model));
        Assert.AreEqual(hasHint ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null, exception.RetryAfter);
        Assert.AreEqual(RequestDelivery.Unknown, exception.Delivery);
    }

    [TestMethod]
    public async Task MalformedOrPastRetryHintIsIgnoredWithoutReadingErrorBody()
    {
        foreach (var hint in new[] { "not-a-date", "Wed, 01 Jan 2020 00:00:00 GMT" })
        {
            using var http = Http(new TestHttpHandler((_, _) =>
            {
                var response = Reply(429);
                response.Headers.TryAddWithoutValidation("Retry-After", hint);
                return Task.FromResult(response);
            }));
            var exception = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() => new SenseNovaHttpClient(http).ProbeAsync(MockCredential.Value, Model));
            Assert.IsNull(exception.RetryAfter);
        }
    }

    [TestMethod]
    [DataRow(401, RunFailureKind.Authentication, false)]
    [DataRow(400, RunFailureKind.InvalidRequest, false)]
    [DataRow(404, RunFailureKind.ModelUnavailable, false)]
    [DataRow(503, RunFailureKind.Transient, true)]
    [DataRow(408, RunFailureKind.Network, true)]
    public async Task CompleteHttpPathPreservesSafeClassificationAndDoesNotRetry(int status, RunFailureKind expected, bool review)
    {
        var calls = 0;
        using var http = Http(new TestHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Reply(status));
        }));
        var result = await Engine(http).RunAsync(Parameters).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(expected, result.Failure);
        Assert.AreEqual(review, result.ReviewRequired);
        Assert.AreEqual(review ? 1 : 0, result.UnknownUsageRequests);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, result.RetryAttempts);
        Assert.IsFalse(result.ToString().Contains(MockCredential.Value, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Http429WithRetryAfterProtectsRunInsteadOfBlindRetry()
    {
        var calls = 0;
        using var http = Http(new TestHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            var response = Reply(429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }));
        var engine = Engine(http);
        var run = engine.RunAsync(Parameters);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.Snapshot.State != RunState.AwaitingReview) await Task.Delay(1, deadline.Token);
        Assert.IsFalse(engine.Resume());
        var result = await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(result, await run);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(result.ReviewRequired);
        Assert.AreEqual(0, result.RetryAttempts);
    }

    [TestMethod]
    public async Task ResponseReadFailureDoesNotExposeRawExceptionOrClaimNotSent()
    {
        using var http = Http(new TestHttpHandler((_, _) => throw new IOException(MockCredential.Value)));
        var exception = await Assert.ThrowsExactlyAsync<SenseNovaApiException>(() => new SenseNovaHttpClient(http).ProbeAsync(MockCredential.Value, Model));
        Assert.AreEqual(ApiFailureKind.Network, exception.Kind);
        Assert.AreEqual(RequestDelivery.Unknown, exception.Delivery);
        Assert.IsFalse(exception.ToString().Contains(MockCredential.Value, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EngineDeadlineCoversResponseBodyAfterHeadersHaveArrived()
    {
        var time = new ManualRunTimeProvider();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = Http(new TestHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new BlockingReadStream(reading)) })));
        var engine = Engine(http, time);
        var run = engine.RunAsync(Parameters);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMinutes(2));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(RunFailureKind.RequestTimeout, result.Failure);
        Assert.AreEqual(1, result.UnknownUsageRequests);
        Assert.IsTrue(result.ReviewRequired);
    }

    private sealed class BlockingReadStream(TaskCompletionSource reading) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
