using System.Diagnostics;
using System.Text.Json;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Desktop.Diagnostics;

/// <summary>显式mock测量：走正式面板VM、输入生成、持久引擎与UI状态更新。</summary>
public static class PanelResourceProbe
{
    public static async Task RunAsync(ResourceProbeOptions options, MainWindowViewModel viewModel,
        MockSenseNovaHandler handler, CancellationToken token = default, int percentage = 95)
    {
        if (!viewModel.IsMockMode) throw new InvalidOperationException("资源负载仅允许mock。");
        using var cancellation = token.Register(viewModel.CancelOperation);
        double duration = 0;
        long allocation = 0;
        async Task WriteAsync(string phase)
        {
            Directory.CreateDirectory(options.OutputDirectory);
            await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "probe-state.json"), JsonSerializer.Serialize(new
            {
                Phase = phase, Frontend = "WinForms", RuntimeVersion = Environment.Version.ToString(), NetworkEnabled = false,
                SuccessfulMockRequests = handler.SuccessfulCompletionCount, SimulatedTokens = viewModel.Run.ConfirmedUsage.TotalTokens,
                Panel = new { DurationSeconds = duration, AllocatedBytes = allocation, InputCharacters = RequestBaseline.InputCharacterTarget,
                    RequestReservation = viewModel.Run.Parameters?.RequestTokenReservation, EndReason = viewModel.Run.EndReason?.ToString(),
                    InFlight = viewModel.Run.InFlightRequests, Unknown = viewModel.Run.UnknownUsageRequests, ScheduleEnabled = false }
            }));
        }
        try
        {
            await WriteAsync("Warmup");
            await Task.Delay(TimeSpan.FromSeconds(options.WarmupSeconds), token);
            if (handler.RequestCount != 0) throw new InvalidOperationException("启动期间出现请求。");
            await viewModel.ReadModelsAsync();
            viewModel.Percentage = percentage;
            if (!viewModel.StartRunCommand.CanExecute(null)) throw new InvalidOperationException("面板未准备就绪。");
            await WriteAsync("Load");
            var clock = Stopwatch.StartNew();
            var allocatedBefore = GC.GetTotalAllocatedBytes();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (deadline.Token.Register(viewModel.CancelOperation))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(options.LoadSeconds));
                await viewModel.StartRunCommand.ExecuteAsync();
                deadline.Token.ThrowIfCancellationRequested();
            }
            duration = clock.Elapsed.TotalSeconds;
            allocation = GC.GetTotalAllocatedBytes() - allocatedBefore;
            var run = viewModel.Run;
            if (run.State != RunState.Completed || run.InFlightRequests != 0 || run.UnknownUsageRequests != 0
                || run.CompletedRequests != handler.SuccessfulCompletionCount || handler.SuccessfulProbeCount != 0
                || handler.RequestCount != handler.SuccessfulCompletionCount + 1
                || viewModel.Recovery.Document?.CurrentRun?.Snapshot != run)
                throw new InvalidOperationException("面板运行或持久用量未完整收尾。");
            var requestsBefore = handler.RequestCount;
            var documentBefore = viewModel.Recovery.Document;
            await WriteAsync("Idle");
            await Task.Delay(TimeSpan.FromSeconds(options.IdleSeconds), token);
            if (requestsBefore != handler.RequestCount || documentBefore != viewModel.Recovery.Document)
                throw new InvalidOperationException("等待期间有自动请求或状态变更。");
            await WriteAsync("Completed");
        }
        catch (OperationCanceledException) { await WriteAsync("Aborted"); throw; }
        catch { await WriteAsync("Failed"); throw; }
    }
}
