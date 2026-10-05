using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class CredentialWorkflowTests
{
    [TestMethod]
    public async Task UserConfigurationRestoresKeyWithoutRequestsAndSeparatesMockFiles()
    {
        using var folder = new TestFolder();
        await new JsonUserSettingsStore(folder.Path).SaveAsync(new AppSettings { Percentage = 25 });
        await new DpapiCredentialStore(folder.Path).SaveAsync(MockCredential.Value);
        var client = new RecordingClient();
        var settings = new JsonUserSettingsStore(folder.Path, StorageProfile.User);
        var credentials = new DpapiCredentialStore(folder.Path, StorageProfile.User);
        await using (var vm = new MainWindowViewModel(client, settings, credentials, mockMode: false))
        {
            await vm.InitializeAsync();
            Assert.AreEqual("", vm.ApiKey);
            vm.ApiKey = MockCredential.Value;
            vm.Percentage = 50;
            await vm.SaveConfigurationAsync();
        }
        await using var reopened = new MainWindowViewModel(client, settings, credentials, mockMode: false);
        await reopened.InitializeAsync();
        Assert.AreEqual(MockCredential.Value, reopened.ApiKey);
        Assert.AreEqual(50, reopened.Percentage);
        Assert.IsFalse(reopened.ConnectionValidated);
        Assert.IsFalse(reopened.ProbeCommand.CanExecute(null));
        Assert.AreEqual(0, client.ModelCalls + client.ProbeCalls);
        Assert.AreEqual(25, (await new JsonUserSettingsStore(folder.Path).LoadAsync())!.Percentage);
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(folder.Path, "settings.json"))).Contains(MockCredential.Value));
        var ciphertext = await File.ReadAllBytesAsync(Path.Combine(folder.Path, "api-key.dat"));
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(ciphertext).Contains(MockCredential.Value));
        // 复制用户密文到mock位置仍不能以旧模拟熵解密。
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "mock-credential.dat"), ciphertext);
        await Assert.ThrowsExactlyAsync<LocalStorageException>(() => new DpapiCredentialStore(folder.Path).LoadAsync());
    }

    [TestMethod]
    public async Task ChangingKeyInvalidatesModelsAndFailedCheckDoesNotLeakException()
    {
        var client = new RecordingClient();
        await using var vm = new MainWindowViewModel(client, mockMode: false) { ApiKey = MockCredential.Value };
        await vm.ReadModelsAsync();
        Assert.IsTrue(vm.ConnectionValidated);
        Assert.IsNotNull(vm.SelectedModel);
        Assert.IsFalse(vm.ProbeCommand.CanExecute(null));
        vm.ApiKey = "development-only-replacement";
        Assert.IsFalse(vm.ConnectionValidated);
        Assert.AreEqual(0, vm.Models.Count);
        Assert.IsNull(vm.SelectedModel);
        vm.ApiKey = MockCredential.Value;
        client.BeforeModels = _ => throw new SenseNovaApiException(ApiFailureKind.Authentication, MockCredential.Value);
        await vm.ReadModelsAsync();
        Assert.IsFalse(vm.ConnectionValidated);
        StringAssert.Contains(vm.StatusText, "认证失败");
        Assert.IsFalse(vm.StatusText.Contains(MockCredential.Value));
    }

    [TestMethod]
    public async Task SessionOnlyUserKeyIsNeitherSavedNorLoaded()
    {
        using var folder = new TestFolder();
        var settings = new JsonUserSettingsStore(folder.Path, StorageProfile.User);
        var credentials = new RecordingCredentialStore();
        await using (var vm = new MainWindowViewModel(new RecordingClient(), settings, credentials, mockMode: false)
            { ApiKey = MockCredential.Value, RememberCredential = false })
            await vm.SaveConfigurationAsync();
        await using var reopened = new MainWindowViewModel(new RecordingClient(), settings, credentials, mockMode: false);
        await reopened.InitializeAsync();
        Assert.AreEqual("", reopened.ApiKey);
        Assert.AreEqual(0, credentials.Loads + credentials.Saves);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("has spaces")]
    [DataRow("bad\r\nheader")]
    public async Task InvalidKeyCannotCheckOrWrite(string key)
    {
        using var folder = new TestFolder();
        var client = new RecordingClient();
        var credentials = new RecordingCredentialStore();
        await using var vm = new MainWindowViewModel(client, new JsonUserSettingsStore(folder.Path), credentials, mockMode: false)
            { ApiKey = key };
        Assert.IsFalse(vm.ReadModelsCommand.CanExecute(null));
        await vm.ReadModelsAsync();
        await vm.SaveConfigurationAsync();
        Assert.AreEqual(0, client.ModelCalls + credentials.Saves);
        Assert.AreEqual(0, Directory.GetFiles(folder.Path).Length);
    }
}
