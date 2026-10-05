using System.Text;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure.Api;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class StorageTests
{
    [TestMethod]
    public async Task SettingsRoundTripKeepsCredentialOutOfJson()
    {
        using var folder = new TestFolder();
        var store = new JsonUserSettingsStore(folder.Path);
        Assert.IsNull(await store.LoadAsync());
        var settings = new AppSettings { Percentage = 50, IntervalHours = 2.5m, RememberCredential = false };
        await store.SaveAsync(settings);
        Assert.AreEqual(settings, await new JsonUserSettingsStore(folder.Path).LoadAsync());
        var json = await File.ReadAllTextAsync(System.IO.Path.Combine(folder.Path, "mock-settings.json"));
        Assert.IsFalse(json.Contains(MockCredential.Value, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, Directory.GetFiles(folder.Path).Length);
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("{\"SchemaVersion\":2}")]
    [DataRow("{\"Percentage\":96}")]
    [DataRow("{\"IntervalHours\":0}")]
    [DataRow("{\"ModelId\":null}")]
    [DataRow("{\"ApiKey\":\"mock-only-not-a-real-key\"}")]
    public async Task InvalidSettingsFailWithoutEchoingData(string json)
    {
        using var folder = new TestFolder();
        await File.WriteAllTextAsync(System.IO.Path.Combine(folder.Path, "mock-settings.json"), json);
        var failure = await Assert.ThrowsExactlyAsync<LocalStorageException>(() => new JsonUserSettingsStore(folder.Path).LoadAsync());
        Assert.IsFalse(failure.Message.Contains(json, StringComparison.Ordinal));
        Assert.IsFalse(failure.ToString().Contains(MockCredential.Value, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CancelledOrInvalidSavePreservesLastSettingsAndLeavesNoTemporaryFile()
    {
        using var folder = new TestFolder();
        var store = new JsonUserSettingsStore(folder.Path);
        var initial = new AppSettings { Percentage = 50 };
        await store.SaveAsync(initial);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(initial with { Percentage = 100 }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(initial with { Percentage = 25 }, cancelled.Token));
        Assert.AreEqual(initial, await store.LoadAsync());
        Assert.AreEqual(0, Directory.GetFiles(folder.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task DpapiRoundTripProducesCiphertextAndCancelledSavePreservesIt()
    {
        using var folder = new TestFolder();
        var store = new DpapiCredentialStore(folder.Path);
        Assert.IsNull(await store.LoadAsync());
        await store.SaveAsync(MockCredential.Value);
        var path = System.IO.Path.Combine(folder.Path, "mock-credential.dat");
        var ciphertext = await File.ReadAllBytesAsync(path);
        Assert.IsFalse(Encoding.UTF8.GetString(ciphertext).Contains(MockCredential.Value, StringComparison.Ordinal));
        Assert.AreEqual(MockCredential.Value, await new DpapiCredentialStore(folder.Path).LoadAsync());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync("another-virtual-key", cancelled.Token));
        CollectionAssert.AreEqual(ciphertext, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(0, Directory.GetFiles(folder.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task CorruptCiphertextFailsWithoutPlaintextOrPath()
    {
        using var folder = new TestFolder();
        await File.WriteAllTextAsync(System.IO.Path.Combine(folder.Path, "mock-credential.dat"), MockCredential.Value);
        var failure = await Assert.ThrowsExactlyAsync<LocalStorageException>(() => new DpapiCredentialStore(folder.Path).LoadAsync());
        Assert.IsFalse(failure.ToString().Contains(MockCredential.Value, StringComparison.Ordinal));
        Assert.IsFalse(failure.ToString().Contains(folder.Path, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OversizeLocalFilesAreRejectedBeforeParsing()
    {
        using var folder = new TestFolder();
        foreach (var name in new[] { "mock-settings.json", "mock-credential.dat" })
            await File.WriteAllBytesAsync(System.IO.Path.Combine(folder.Path, name), new byte[65_537]);
        await Assert.ThrowsExactlyAsync<LocalStorageException>(() => new JsonUserSettingsStore(folder.Path).LoadAsync());
        await Assert.ThrowsExactlyAsync<LocalStorageException>(() => new DpapiCredentialStore(folder.Path).LoadAsync());
    }
}

internal sealed class TestFolder : IDisposable
{
    private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SenseNovaMockTests");
    public string Path { get; }
    public TestFolder()
    {
        Path = System.IO.Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        // 只删除本 fixture 创建的 GUID 子目录，校验边界，不枚举其他用户数据。
        if (!System.IO.Path.GetFullPath(Path).StartsWith(System.IO.Path.GetFullPath(_parent) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("测试目录越界。");
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
