using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Desktop.Diagnostics;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class PanelResourceProbeTests
{
    [TestMethod]
    public async Task GeneratedInputAndPersistentVmProduceCompletedVirtualReport()
    {
        using var folder = new TestFolder();
        using var handler = new MockSenseNovaHandler(delay: TimeSpan.Zero);
        using var http = new HttpClient(handler) { BaseAddress = new(SenseNovaDefaults.BaseUrl + "/") };
        await using var vm = new MainWindowViewModel(new SenseNovaHttpClient(http), runStore: new JsonRunStateStore(folder.Path));
        await vm.InitializeAsync();
        var options = ResourceProbeOptions.Parse(["--panel-resource-baseline", folder.Path, "1", "5", "0"]);
        Assert.IsTrue(options!.FullPanel);
        Assert.IsFalse(options.FullEngine);
        await PanelResourceProbe.RunAsync(options, vm, handler, percentage: 1);
        var json = await File.ReadAllTextAsync(Path.Combine(folder.Path, "probe-state.json"));
        using var report = JsonDocument.Parse(json);
        Assert.AreEqual("Completed", report.RootElement.GetProperty("Phase").GetString());
        Assert.IsTrue(report.RootElement.GetProperty("SuccessfulMockRequests").GetInt32() > 0);
        Assert.AreEqual(201024, report.RootElement.GetProperty("Panel").GetProperty("RequestReservation").GetInt64());
        Assert.IsFalse(report.RootElement.GetProperty("NetworkEnabled").GetBoolean());
        Assert.IsFalse(json.Contains(MockCredential.Value));
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(folder.Path, "run-state.json"))).Contains(MockCredential.Value));
    }

    [TestMethod]
    public async Task FailedConnectionCannotClaimCompletedPanelMeasurement()
    {
        using var folder = new TestFolder();
        using var handler = new MockSenseNovaHandler(MockScenario.AuthenticationFailure, TimeSpan.Zero);
        using var http = new HttpClient(handler) { BaseAddress = new(SenseNovaDefaults.BaseUrl + "/") };
        await using var vm = new MainWindowViewModel(new SenseNovaHttpClient(http), runStore: new JsonRunStateStore(folder.Path));
        await vm.InitializeAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PanelResourceProbe.RunAsync(new(folder.Path, 1, 5, 0), vm, handler, percentage: 1));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder.Path, "probe-state.json")));
        Assert.AreEqual("Failed", report.RootElement.GetProperty("Phase").GetString());
        Assert.AreEqual(0, handler.SuccessfulCompletionCount);
    }

    [TestMethod]
    public async Task CancellationDuringWarmupReportsAbortedAndZeroRequests()
    {
        using var folder = new TestFolder();
        using var handler = new MockSenseNovaHandler();
        using var http = new HttpClient(handler) { BaseAddress = new(SenseNovaDefaults.BaseUrl + "/") };
        await using var vm = new MainWindowViewModel(new SenseNovaHttpClient(http), runStore: new JsonRunStateStore(folder.Path));
        await vm.InitializeAsync();
        using var cancellation = new CancellationTokenSource(100);
        await Assert.ThrowsAsync<OperationCanceledException>(() => PanelResourceProbe.RunAsync(new(folder.Path, 1, 5, 10), vm, handler, cancellation.Token));
        Assert.AreEqual(0, handler.RequestCount);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder.Path, "probe-state.json")));
        Assert.AreEqual("Aborted", report.RootElement.GetProperty("Phase").GetString());
    }

    [TestMethod]
    public async Task ResourceDriverRejectsUserModeBeforeWork()
    {
        using var folder = new TestFolder();
        using var handler = new MockSenseNovaHandler();
        await using var vm = new MainWindowViewModel(mockMode: false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PanelResourceProbe.RunAsync(new(folder.Path, 1, 1, 0), vm, handler));
        Assert.AreEqual(0, Directory.GetFiles(folder.Path).Length);
        Assert.AreEqual(0, handler.RequestCount);
    }
}
