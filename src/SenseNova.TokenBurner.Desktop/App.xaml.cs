using System.Windows;
using System.Net.Http;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Desktop;

public partial class App : Application
{
    private HttpClient? _httpClient;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var directory = Environment.GetEnvironmentVariable("SENSENOVA_MOCK_PROFILE")
            ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SenseNova.TokenBurner", "Demo");
        _httpClient = new HttpClient(new MockSenseNovaHandler())
        { BaseAddress = new Uri(SenseNovaDefaults.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };
        var viewModel = new MainWindowViewModel(new SenseNovaHttpClient(_httpClient),
            new JsonUserSettingsStore(directory), new DpapiCredentialStore(directory));
        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();
        _ = viewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _httpClient?.Dispose();
        base.OnExit(e);
    }
}
