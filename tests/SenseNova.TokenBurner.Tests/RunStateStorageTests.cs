using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class RunStateStorageTests
{
    [TestMethod]
    public async Task LegacySettingsUpgradeInMemoryWithoutRewritingOriginal()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "mock-settings.json");
        const string legacy = "{\"SchemaVersion\":1,\"Percentage\":50,\"IntervalHours\":2.5,\"ModelId\":\"legacy-model\",\"RememberCredential\":false}";
        await File.WriteAllTextAsync(path, legacy);
        var store = new JsonUserSettingsStore(folder.Path);
        var upgraded = (await store.LoadAsync())!;
        Assert.AreEqual(2, upgraded.SchemaVersion);
        Assert.AreEqual(50, upgraded.Percentage);
        Assert.AreEqual(2.5m, upgraded.IntervalHours);
        Assert.AreEqual("legacy-model", upgraded.ModelId);
        Assert.IsFalse(upgraded.RememberCredential);
        Assert.AreEqual(legacy, await File.ReadAllTextAsync(path));
        await store.SaveAsync(upgraded);
        Assert.AreEqual(upgraded, await store.LoadAsync());
    }

    [TestMethod]
    public async Task RecordRoundTripAndCancellationKeepCompletePreviousDocument()
    {
        using var folder = new TestFolder();
        var store = new JsonRunStateStore(folder.Path);
        Assert.IsNull(await store.LoadAsync());
        var initial = Document();
        await store.SaveAsync(initial);
        var path = Path.Combine(folder.Path, "run-state.json");
        var before = await File.ReadAllBytesAsync(path);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(RunStateDocument.Empty, canceled.Token));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        var loaded = (await store.LoadAsync())!;
        Assert.AreEqual(initial.CurrentRun, loaded.CurrentRun);
        Assert.AreEqual(0, Directory.GetFiles(folder.Path, "*.tmp").Length);
        Assert.IsFalse((await File.ReadAllTextAsync(path)).Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("{}")]
    [DataRow("{\"SchemaVersion\":1,\"CurrentRun\":null,\"History\":null,\"Plan\":null,\"PlanRequested\":false}")]
    [DataRow("{\"SchemaVersion\":1,\"CurrentRun\":null,\"History\":[],\"Plan\":null,\"PlanRequested\":true}")]
    [DataRow("{\"SchemaVersion\":9,\"CurrentRun\":null,\"History\":[],\"Plan\":null,\"PlanRequested\":false}")]
    [DataRow("{\"SchemaVersion\":1,\"CurrentRun\":null,\"History\":[],\"Plan\":null,\"PlanRequested\":false,\"ApiKey\":\"virtual-secret\"}")]
    public async Task InvalidRecordsAreRejectedWithoutEchoAndPreserved(string json)
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "run-state.json");
        await File.WriteAllTextAsync(path, json);
        var failure = await Assert.ThrowsExactlyAsync<LocalStorageException>(() => new JsonRunStateStore(folder.Path).LoadAsync());
        Assert.IsFalse(failure.ToString().Contains("virtual-secret", StringComparison.Ordinal));
        Assert.IsFalse(failure.ToString().Contains(folder.Path, StringComparison.Ordinal));
        Assert.AreEqual(json, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task InvalidNumbersAndOversizeRecordsDoNotReplaceLastGoodRecord()
    {
        using var folder = new TestFolder();
        var store = new JsonRunStateStore(folder.Path);
        var initial = Document();
        await store.SaveAsync(initial);
        var path = Path.Combine(folder.Path, "run-state.json");
        var before = await File.ReadAllBytesAsync(path);
        var bad = initial with { CurrentRun = initial.CurrentRun! with
        { Snapshot = initial.CurrentRun.Snapshot with { ReservedTokens = 1 } } };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(bad));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        await File.WriteAllBytesAsync(path, new byte[65_537]);
        await Assert.ThrowsExactlyAsync<LocalStorageException>(() => store.LoadAsync());
    }

    [TestMethod]
    public async Task InvalidEnumAndMissingRequiredSnapshotFieldsFailOnLoad()
    {
        using var folder = new TestFolder();
        var store = new JsonRunStateStore(folder.Path);
        await store.SaveAsync(Document());
        var path = Path.Combine(folder.Path, "run-state.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        node["CurrentRun"]!["Snapshot"]!["State"] = 999;
        await File.WriteAllTextAsync(path, node.ToJsonString());
        await Assert.ThrowsExactlyAsync<LocalStorageException>(() => store.LoadAsync());
        await store.SaveAsync(Document());
        node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        node["CurrentRun"]!["Snapshot"]!.AsObject().Remove("InFlightRequests");
        await File.WriteAllTextAsync(path, node.ToJsonString());
        await Assert.ThrowsExactlyAsync<LocalStorageException>(() => store.LoadAsync());
    }

    private static RunStateDocument Document() => RunStateDocument.Empty with
    {
        CurrentRun = new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new(RunState.Running, new("mock-model", 1000) { RequestTokenReservation = 100 },
                new(0, 0, 0), 0, 1, 0, null) { ReservedTokens = 100 })
    };
}
