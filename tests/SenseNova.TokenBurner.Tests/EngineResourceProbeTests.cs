using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Desktop.Diagnostics;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class EngineResourceProbeTests
{
    [TestMethod]
    public void FullEngineRequiresExplicitFlagAndValidatedArguments()
    {
        using var folder = new TestFolder();
        var options = ResourceProbeOptions.Parse(["--engine-resource-baseline", folder.Path, "120", "120", "10"]);
        Assert.IsTrue(options!.FullEngine);
        Assert.IsFalse(ResourceProbeOptions.Parse(["--resource-baseline", folder.Path, "120", "60", "10"])!.FullEngine);
        Assert.IsNull(ResourceProbeOptions.Parse([]));
        Assert.ThrowsExactly<ArgumentException>(() => ResourceProbeOptions.Parse(["--engine-resource-baseline", folder.Path, "0", "60", "10"]));
    }

    [TestMethod]
    public async Task DriverRunsActualHttpBudgetAndRecordPathsThenWaitsWithoutRequestsOrWrites()
    {
        using var folder = new TestFolder();
        using var handler = new MockSenseNovaHandler(delay: TimeSpan.FromMilliseconds(5));
        await EngineResourceProbe.RunAsync(new(folder.Path, 1, 10, 0), handler, targetTokens: 5000, inputCharacters: 1000);
        var text = await File.ReadAllTextAsync(Path.Combine(folder.Path, "probe-state.json"));
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Assert.AreEqual("Completed", root.GetProperty("Phase").GetString());
        Assert.IsFalse(root.GetProperty("NetworkEnabled").GetBoolean());
        Assert.IsFalse(text.Contains(MockCredential.Value, StringComparison.Ordinal));
        var engine = root.GetProperty("Engine");
        Assert.AreEqual(0, engine.GetProperty("WaitRequests").GetInt32());
        Assert.AreEqual(0L, engine.GetProperty("WaitStateWrites").GetInt64());
        Assert.AreEqual("Shutdown", engine.GetProperty("ShutdownState").GetString());
        Assert.AreEqual(0, engine.GetProperty("RemainingInFlight").GetInt32());
        var rounds = engine.GetProperty("Rounds");
        Assert.AreEqual(2, rounds.GetArrayLength());
        Assert.AreEqual(rounds[0].GetProperty("Requests").GetInt64(), rounds[1].GetProperty("Requests").GetInt64());
        Assert.AreEqual(0L, rounds[0].GetProperty("StorageWrites").GetInt64());
        Assert.AreEqual(2 * rounds[1].GetProperty("Requests").GetInt64() + 3, rounds[1].GetProperty("StorageWrites").GetInt64());
        Assert.IsLessThanOrEqualTo(65_536L, rounds[1].GetProperty("MaximumStateFileBytes").GetInt64());
        Assert.AreEqual(handler.SuccessfulCompletionCount, root.GetProperty("SuccessfulMockRequests").GetInt64());
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(folder.Path, "engine-profile"), "*.tmp").Length);
    }

    [TestMethod]
    public async Task BadMockSetupCannotProduceCompletedEngineReport()
    {
        using var folder = new TestFolder();
        using var handler = new MockSenseNovaHandler(MockScenario.AuthenticationFailure, TimeSpan.Zero);
        await Assert.ThrowsAsync<Exception>(() => EngineResourceProbe.RunAsync(new(folder.Path, 1, 10, 0), handler,
            targetTokens: 5000, inputCharacters: 1000));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder.Path, "probe-state.json")));
        Assert.AreEqual("Failed", json.RootElement.GetProperty("Phase").GetString());
        Assert.AreEqual(0, handler.SuccessfulCompletionCount);
    }

    [TestMethod]
    public async Task CancellationReportsAbortedAndDoesNotStartFullRequestsDuringWarmup()
    {
        using var folder = new TestFolder();
        using var handler = new MockSenseNovaHandler(delay: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<OperationCanceledException>(() => EngineResourceProbe.RunAsync(new(folder.Path, 1, 10, 5),
            handler, cancellation.Token, 5000, 1000));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder.Path, "probe-state.json")));
        Assert.AreEqual("Aborted", json.RootElement.GetProperty("Phase").GetString());
        Assert.AreEqual(0, handler.SuccessfulCompletionCount);
    }
}
