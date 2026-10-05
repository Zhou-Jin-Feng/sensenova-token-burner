using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Desktop.Diagnostics;

public sealed record ResourceProbeOptions(string OutputDirectory, int IdleSeconds, int LoadSeconds, int WarmupSeconds)
{
    public bool FullEngine { get; init; }
    public bool FullPanel { get; init; }
    public static ResourceProbeOptions? Parse(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] is not ("--resource-baseline" or "--engine-resource-baseline" or "--panel-resource-baseline")) return null;
        if (arguments.Length != 5 || !Path.IsPathFullyQualified(arguments[1]))
            throw new ArgumentException("资源测量参数无效。");
        return new(Path.GetFullPath(arguments[1]), ReadSeconds(arguments[2], 1, 3600),
            ReadSeconds(arguments[3], 1, 1800), ReadSeconds(arguments[4], 0, 60))
        { FullEngine = arguments[0] == "--engine-resource-baseline", FullPanel = arguments[0] == "--panel-resource-baseline" };
    }

    private static int ReadSeconds(string value, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < minimum || seconds > maximum) throw new ArgumentException("资源测量时长无效。");
        return seconds;
    }
}

/// <summary>显式开发开关：驱动现有 mock 命令，外部脚本采样；普通启动不执行。</summary>
public static class ResourceProbe
{
    public static async Task RunAsync(ResourceProbeOptions options, MainWindowViewModel viewModel,
        MockSenseNovaHandler handler, CancellationToken cancellationToken = default)
    {
        var requests = 0;
        var tokens = 0L;
        using var registration = cancellationToken.Register(viewModel.CancelOperation);
        try
        {
            await WriteStateAsync(options, "Warmup", requests, tokens);
            await Task.Delay(TimeSpan.FromSeconds(options.WarmupSeconds), cancellationToken);
            await WriteStateAsync(options, "Idle", requests, tokens);
            await Task.Delay(TimeSpan.FromSeconds(options.IdleSeconds), cancellationToken);
            if (handler.RequestCount != 0) throw new InvalidOperationException("待机期间出现模拟请求。");

            await WriteStateAsync(options, "Setup", requests, tokens);
            await viewModel.ReadModelsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!viewModel.ProbeCommand.CanExecute(null)) throw new InvalidOperationException("模拟模型未准备就绪。");
            await WriteStateAsync(options, "Load", requests, tokens);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(options.LoadSeconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completedBefore = handler.SuccessfulProbeCount;
                await viewModel.ProbeCommand.ExecuteAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (handler.SuccessfulProbeCount != completedBefore + 1
                    || viewModel.LastUsage != new TokenUsage(128, 8, 136))
                    throw new InvalidOperationException("模拟用量未完成，测量无效。");
                requests++;
                tokens += viewModel.LastUsage.TotalTokens;
                // 模拟延迟 80ms，加 100ms 异步等待；单次串行，无忙循环。
                await Task.Delay(100, cancellationToken);
            }
            if (handler.RequestCount != requests + 1 || handler.SuccessfulProbeCount != requests)
                throw new InvalidOperationException("测量期间出现额外操作。");
            await WriteStateAsync(options, "Completed", requests, tokens);
        }
        catch (OperationCanceledException)
        {
            await WriteStateAsync(options, "Aborted", requests, tokens);
            throw;
        }
        catch
        {
            await WriteStateAsync(options, "Failed", requests, tokens);
            throw;
        }
    }

    private static async Task WriteStateAsync(ResourceProbeOptions options, string phase, int requests, long tokens)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var path = Path.Combine(options.OutputDirectory, "probe-state.json");
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(new
            {
                Phase = phase, PhaseStartedUtc = DateTimeOffset.UtcNow,
                SuccessfulMockRequests = requests, SimulatedTokens = tokens,
                RuntimeVersion = Environment.Version.ToString(), NetworkEnabled = false, Frontend = "WinForms"
            }));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
