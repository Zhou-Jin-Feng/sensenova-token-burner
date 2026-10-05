using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Desktop.Diagnostics;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class ResourceProbeTests
{
    [TestMethod]
    public void OrdinaryStartupDoesNotEnableResourceWorkload()
    {
        Assert.IsNull(ResourceProbeOptions.Parse([]));
        Assert.IsNull(ResourceProbeOptions.Parse(["unrelated"]));
        using var folder = new TestFolder();
        var options = ResourceProbeOptions.Parse(["--resource-baseline", folder.Path, "120", "60", "10"]);
        Assert.IsNotNull(options);
        Assert.AreEqual(120, options.IdleSeconds);
        Assert.AreEqual(60, options.LoadSeconds);
    }

    [TestMethod]
    [DataRow("0", "60", "10")]
    [DataRow("-1", "60", "10")]
    [DataRow("3601", "60", "10")]
    [DataRow("120", "0", "10")]
    [DataRow("120", "1801", "10")]
    [DataRow("120", "60", "61")]
    [DataRow("abc", "60", "10")]
    public void InvalidDurationIsRejected(string idle, string load, string warmup)
    {
        using var folder = new TestFolder();
        Assert.ThrowsExactly<ArgumentException>(() => ResourceProbeOptions.Parse(["--resource-baseline", folder.Path, idle, load, warmup]));
    }

    [TestMethod]
    public void IncompleteArgumentsOrRelativeOutputCannotEnableProbe()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ResourceProbeOptions.Parse(["--resource-baseline"]));
        Assert.ThrowsExactly<ArgumentException>(() => ResourceProbeOptions.Parse(["--resource-baseline", "relative", "120", "60", "10"]));
        using var folder = new TestFolder();
        Assert.ThrowsExactly<ArgumentException>(() => ResourceProbeOptions.Parse(["--resource-baseline", folder.Path, "120", "60", "10", "unknown"]));
    }

    [TestMethod]
    [DataRow("default")]
    [DataRow("software")]
    [DataRow("unknown")]
    public void RemovedWpfRenderingArgumentsAreRejected(string extraArgument)
    {
        using var folder = new TestFolder();
        Assert.ThrowsExactly<ArgumentException>(() => ResourceProbeOptions.Parse(["--resource-baseline", folder.Path, "120", "60", "10", extraArgument]));
    }

    [TestMethod]
    public async Task ProbeUsesViewModelPipelineAndReportsOnlyVirtualStatistics()
    {
        using var folder = new TestFolder();
        using var handler = new MockSenseNovaHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri(SenseNovaDefaults.BaseUrl + "/") };
        await using var vm = new MainWindowViewModel(new SenseNovaHttpClient(http));
        await ResourceProbe.RunAsync(new(folder.Path, 1, 1, 0), vm, handler);
        var json = await File.ReadAllTextAsync(Path.Combine(folder.Path, "probe-state.json"));
        using var report = JsonDocument.Parse(json);
        Assert.AreEqual("Completed", report.RootElement.GetProperty("Phase").GetString());
        var completed = report.RootElement.GetProperty("SuccessfulMockRequests").GetInt32();
        Assert.IsTrue(completed > 0);
        Assert.AreEqual(completed, handler.SuccessfulProbeCount);
        Assert.AreEqual(completed + 1, handler.RequestCount);
        Assert.AreEqual(completed * 136L, report.RootElement.GetProperty("SimulatedTokens").GetInt64());
        Assert.IsFalse(report.RootElement.GetProperty("NetworkEnabled").GetBoolean());
        Assert.AreEqual("WinForms", report.RootElement.GetProperty("Frontend").GetString());
        Assert.IsFalse(json.Contains(MockCredential.Value, StringComparison.Ordinal));
        Assert.AreEqual(0, Directory.GetFiles(folder.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task CancellingIdleDoesNotStartRequestsAndReportsAborted()
    {
        using var folder = new TestFolder();
        using var handler = new MockSenseNovaHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri(SenseNovaDefaults.BaseUrl + "/") };
        await using var vm = new MainWindowViewModel(new SenseNovaHttpClient(http));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(() => ResourceProbe.RunAsync(new(folder.Path, 10, 1, 0), vm, handler, cancellation.Token));
        Assert.AreEqual(0, handler.RequestCount);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder.Path, "probe-state.json")));
        Assert.AreEqual("Aborted", report.RootElement.GetProperty("Phase").GetString());
    }

    [TestMethod]
    public async Task BadMockSetupCannotProduceCompletedBaseline()
    {
        using var folder = new TestFolder();
        using var handler = new MockSenseNovaHandler(MockScenario.AuthenticationFailure);
        using var http = new HttpClient(handler) { BaseAddress = new Uri(SenseNovaDefaults.BaseUrl + "/") };
        await using var vm = new MainWindowViewModel(new SenseNovaHttpClient(http));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ResourceProbe.RunAsync(new(folder.Path, 1, 1, 0), vm, handler));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder.Path, "probe-state.json")));
        Assert.AreEqual("Failed", report.RootElement.GetProperty("Phase").GetString());
        Assert.AreEqual(0, handler.SuccessfulProbeCount);
    }
}
