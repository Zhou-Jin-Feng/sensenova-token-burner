using SenseNova.TokenBurner.Desktop.Diagnostics;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments is ["--multi-resource-baseline", var output])
        {
            ApplicationConfiguration.Initialize();
            return MultiTaskResourceProbe.Run(output);
        }
        ResourceProbeOptions? options;
        try { options = ResourceProbeOptions.Parse(arguments); }
        catch (ArgumentException) { return 1; }
        if (options is null && arguments.Length > 0 && !arguments.SequenceEqual(new[] { "--mock" })) return 1;
        var mockMode = options is not null || arguments.SequenceEqual(new[] { "--mock" })
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SENSENOVA_MOCK_PROFILE"));

        ApplicationConfiguration.Initialize();
        UserInstanceLock? instance;
        try
        {
            // 锁按 Windows 当前用户固定位置取得，切换开发 profile 也不能并行运行第二个面板。
            instance = UserInstanceLock.TryAcquire(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SenseNova.TokenBurner"));
        }
        catch (LocalStorageException exception)
        {
            MessageBox.Show(exception.Message, "SenseNova Token Burner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        if (instance is null)
        {
            MessageBox.Show("应用已经运行，请使用现有窗口。", "SenseNova Token Burner", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }
        using var instanceLifetime = instance;
        var directory = mockMode ? Environment.GetEnvironmentVariable("SENSENOVA_MOCK_PROFILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SenseNova.TokenBurner", "Demo")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SenseNova.TokenBurner", "User");
        using HttpMessageHandler handler = mockMode ? new MockSenseNovaHandler()
            : new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(SenseNovaDefaults.BaseUrl + "/"),
            Timeout = TimeSpan.FromMinutes(2)
        };
        var profile = mockMode ? StorageProfile.Mock : StorageProfile.User;
        if (options is null)
        {
            var api = new SenseNovaHttpClient(http);
            var coordinator = new MultiTaskCoordinator(new TaskWorkspace(new JsonTaskCatalogStore(directory, profile)), api, api, mockMode);
            using var multiForm = new MultiTaskForm(coordinator);
            Application.Run(multiForm);
            return 0;
        }
        var viewModel = new MainWindowViewModel(new SenseNovaHttpClient(http),
            new JsonUserSettingsStore(directory, profile), new DpapiCredentialStore(directory, profile), mockMode,
            new JsonRunStateStore(directory));
        using var lifetime = new CancellationTokenSource();
        using var form = new MainForm(viewModel);
        var exitCode = 0;
        form.FormClosed += (_, _) => lifetime.Cancel();
        form.Shown += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            if (options is null || lifetime.IsCancellationRequested) return;
            try
            {
                if (options.FullPanel)
                {
                    form.Enabled = false;
                    await PanelResourceProbe.RunAsync(options, viewModel, (MockSenseNovaHandler)handler, lifetime.Token);
                }
                else if (options.FullEngine)
                {
                    form.Enabled = false;
                    await EngineResourceProbe.RunAsync(options, (MockSenseNovaHandler)handler, lifetime.Token);
                }
                else await ResourceProbe.RunAsync(options, viewModel, (MockSenseNovaHandler)handler, lifetime.Token);
            }
            catch (Exception) { exitCode = 1; }
            if (!lifetime.IsCancellationRequested) form.RequestExit();
        };
        Application.Run(form);
        return exitCode;
    }
}
