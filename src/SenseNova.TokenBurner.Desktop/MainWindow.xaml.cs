using System.Windows;
using System.ComponentModel;
using SenseNova.TokenBurner.Desktop.ViewModels;

namespace SenseNova.TokenBurner.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _closing;
    private bool _readyToClose;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void ClosePreview(object sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_readyToClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        await _viewModel.DisposeAsync();
        _readyToClose = true;
        // DisposeAsync 在待机时可同步完成，必须先离开 Closing 事件再执行最终关闭。
        await Dispatcher.InvokeAsync(Close);
    }
}
