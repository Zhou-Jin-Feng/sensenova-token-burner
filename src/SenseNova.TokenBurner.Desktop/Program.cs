using SenseNova.TokenBurner.Desktop.Diagnostics;
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
        ResourceProbeOptions? options;
        try { options = ResourceProbeOptions.Parse(arguments); }
        catch (ArgumentException) { return 1; }

        ApplicationConfiguration.Initialize();
        var directory = Environment.GetEnvironmentVariable("SENSENOVA_MOCK_PROFILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SenseNova.TokenBurner", "Demo");
        using var handler = new MockSenseNovaHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(SenseNovaDefaults.BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        var viewModel = new MainWindowViewModel(new SenseNovaHttpClient(http),
            new JsonUserSettingsStore(directory), new DpapiCredentialStore(directory));
        using var lifetime = new CancellationTokenSource();
        using var form = new MainForm(viewModel);
        var exitCode = 0;
        form.FormClosing += (_, _) => lifetime.Cancel();
        form.Shown += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            if (options is null || lifetime.IsCancellationRequested) return;
            try { await ResourceProbe.RunAsync(options, viewModel, handler, lifetime.Token); }
            catch (Exception) { exitCode = 1; }
            if (!lifetime.IsCancellationRequested) form.Close();
        };
        Application.Run(form);
        return exitCode;
    }
}
