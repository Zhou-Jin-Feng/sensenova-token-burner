using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class ViewModelWorkflowTests
{
    [TestMethod]
    public async Task SavedConfigurationAndDpapiRestoreWithoutApiOrSchedule()
    {
        using var folder = new TestFolder();
        var client = new RecordingClient();
        await using (var first = new MainWindowViewModel(client, new JsonUserSettingsStore(folder.Path), new DpapiCredentialStore(folder.Path)))
        {
            first.Percentage = 50;
            first.IntervalHoursText = "2.5";
            await first.SaveConfigurationCommand.ExecuteAsync();
            StringAssert.Contains(first.StatusText, "已保存");
        }
        await using var reopened = new MainWindowViewModel(client, new JsonUserSettingsStore(folder.Path), new DpapiCredentialStore(folder.Path));
        await reopened.InitializeAsync();
        Assert.AreEqual(50, reopened.Percentage);
        Assert.AreEqual("2.5", reopened.IntervalHoursText);
        Assert.AreEqual(0, client.ModelCalls + client.ProbeCalls);
        Assert.IsNull(reopened.SelectedModel);
        Assert.IsFalse(reopened.ProbeCommand.CanExecute(null));
        StringAssert.Contains(reopened.CredentialStatus, "已恢复");
        StringAssert.Contains(reopened.StatusText, "未启用");
    }

    [TestMethod]
    public async Task SessionOnlyDoesNotSaveOrLoadCredentialAndKeepsExistingCiphertext()
    {
        using var folder = new TestFolder();
        var credentials = new DpapiCredentialStore(folder.Path);
        await credentials.SaveAsync(MockCredential.Value);
        var path = Path.Combine(folder.Path, "mock-credential.dat");
        var before = await File.ReadAllBytesAsync(path);
        await using (var first = new MainWindowViewModel(new RecordingClient(), new JsonUserSettingsStore(folder.Path), credentials))
        {
            first.RememberCredential = false;
            await first.SaveConfigurationAsync();
        }
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        var trackingCredentials = new RecordingCredentialStore();
        await using var reopened = new MainWindowViewModel(new RecordingClient(), new JsonUserSettingsStore(folder.Path), trackingCredentials);
        await reopened.InitializeAsync();
        Assert.IsFalse(reopened.RememberCredential);
        Assert.AreEqual(0, trackingCredentials.Loads + trackingCredentials.Saves);
        await reopened.SaveConfigurationAsync();
        Assert.AreEqual(0, trackingCredentials.Loads + trackingCredentials.Saves);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("abc")]
    [DataRow("1,5")]
    public async Task InvalidIntervalDoesNotWriteConfigOrCredential(string hours)
    {
        using var folder = new TestFolder();
        var credentials = new RecordingCredentialStore();
        await using var vm = new MainWindowViewModel(new RecordingClient(), new JsonUserSettingsStore(folder.Path), credentials)
            { IntervalHoursText = hours };
        await vm.SaveConfigurationAsync();
        Assert.AreEqual(0, credentials.Saves);
        Assert.AreEqual(0, Directory.GetFiles(folder.Path).Length);
        StringAssert.Contains(vm.StatusText, "配置无效");
    }

    [TestMethod]
    public async Task ZeroAndUnconfirmedModelDoNotSendConsumptionRequest()
    {
        var client = new RecordingClient();
        await using var vm = new MainWindowViewModel(client);
        await vm.ReadModelsAsync();
        vm.Percentage = 0;
        Assert.IsFalse(vm.ProbeCommand.CanExecute(null));
        await vm.ProbeAsync();
        Assert.AreEqual(0, client.ProbeCalls);
        vm.Percentage = 50;
        vm.SelectedModel = vm.Models.Single(model => !model.IsRebateEligible);
        Assert.IsFalse(vm.ProbeCommand.CanExecute(null));
        await vm.ProbeAsync();
        Assert.AreEqual(0, client.ProbeCalls);
    }

    [TestMethod]
    public async Task SingleProbeReportsUsageAndSavedModelIsUsedOnNextRead()
    {
        using var folder = new TestFolder();
        var client = new RecordingClient();
        await using var vm = new MainWindowViewModel(client, new JsonUserSettingsStore(folder.Path));
        await vm.ReadModelsAsync();
        await vm.ProbeCommand.ExecuteAsync();
        Assert.AreEqual(new TokenUsage(128, 8, 136), vm.LastUsage);
        Assert.AreEqual(1, client.ProbeCalls);
        StringAssert.Contains(vm.UsageLabel, "136");
        vm.SelectedModel = vm.Models.Single(model => !model.IsRebateEligible);
        await vm.SaveConfigurationAsync();
        await vm.ReadModelsAsync();
        Assert.AreEqual("mock-unverified-model", vm.SelectedModel?.Id);
        Assert.IsFalse(vm.ProbeCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task PendingOperationBlocksOverlapAndCancellationAllowsRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingClient { BeforeModels = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } };
        await using var vm = new MainWindowViewModel(client);
        var pending = vm.ReadModelsCommand.ExecuteAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(vm.IsBusy);
        Assert.IsFalse(vm.ReadModelsCommand.CanExecute(null));
        await vm.ReadModelsAsync();
        Assert.AreEqual(1, client.ModelCalls);
        vm.CancelCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(vm.IsIdle);
        StringAssert.Contains(vm.StatusText, "取消");
        client.BeforeModels = null;
        await vm.ReadModelsAsync();
        Assert.AreEqual(2, client.ModelCalls);
        Assert.IsNotNull(vm.SelectedModel);
    }

    [TestMethod]
    public async Task ClosingCancelsAndAwaitsOperationAndPreventsFurtherCommands()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingClient { BeforeModels = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } };
        var vm = new MainWindowViewModel(client);
        var pending = vm.ReadModelsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(pending.IsCompleted);
        Assert.IsFalse(vm.IsIdle);
        Assert.IsFalse(vm.ReadModelsCommand.CanExecute(null));
        await vm.ReadModelsAsync();
        Assert.AreEqual(1, client.ModelCalls);
        await vm.DisposeAsync();
    }
}

internal sealed class RecordingClient : ISenseNovaClient
{
    public int ModelCalls { get; private set; }
    public int ProbeCalls { get; private set; }
    public Func<CancellationToken, Task>? BeforeModels { get; set; }
    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string credential, CancellationToken cancellationToken = default)
    {
        Assert.AreEqual(MockCredential.Value, credential);
        ModelCalls++;
        if (BeforeModels is not null) await BeforeModels(cancellationToken);
        return [new(SenseNovaDefaults.RebateModel, 262144, 65536, true), new("mock-unverified-model", 8192, 512, false)];
    }
    public Task<TokenUsage> ProbeAsync(string credential, ModelInfo model, CancellationToken cancellationToken = default)
    {
        Assert.AreEqual(MockCredential.Value, credential);
        ProbeCalls++;
        return Task.FromResult(new TokenUsage(128, 8, 136));
    }
}

internal sealed class RecordingCredentialStore : ICredentialStore
{
    public int Saves { get; private set; }
    public int Loads { get; private set; }
    public Task<string?> LoadAsync(CancellationToken cancellationToken = default) { Loads++; return Task.FromResult<string?>(MockCredential.Value); }
    public Task SaveAsync(string credential, CancellationToken cancellationToken = default) { Saves++; return Task.CompletedTask; }
}
