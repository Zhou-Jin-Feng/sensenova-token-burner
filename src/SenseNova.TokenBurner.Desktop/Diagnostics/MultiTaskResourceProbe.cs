using System.Diagnostics;
using System.Text.Json;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Desktop.Diagnostics;

/// <summary>仅显式开发命令使用：隔离profile、3虚拟key、一次6秒代表性采样，无真实网络。</summary>
internal static class MultiTaskResourceProbe
{
    public static int Run(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (!Path.IsPathFullyQualified(outputDirectory) || Directory.Exists(Path.Combine(output, "Profile"))) return 1;
        Directory.CreateDirectory(output);
        using var handler = new MockSenseNovaHandler(delay: TimeSpan.FromMilliseconds(180));
        using var http = new HttpClient(handler) { BaseAddress = new(SenseNovaDefaults.BaseUrl + "/") };
        var api = new SenseNovaHttpClient(http);
        var coordinator = new MultiTaskCoordinator(new(new JsonTaskCatalogStore(Path.Combine(output, "Profile"), StorageProfile.Mock)), api, api, true);
        using var form = new MultiTaskForm(coordinator);
        var result = 0;
        form.Shown += async (_, _) =>
        {
            try
            {
                await coordinator.InitializeAsync();
                for (var index = 1; index <= 3; index++)
                    await coordinator.AddAsync(MockCredential.Value + "-" + index, new() { DisplayName = "key-" + index, CustomTargetTokens = 10_000_000 });
                var all = coordinator.RunAllAsync();
                var samples = new List<object>();
                using var process = Process.GetCurrentProcess();
                var startCpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
                long peak = 0; var maxActive = 0;
                while (clock.Elapsed < TimeSpan.FromSeconds(6))
                {
                    process.Refresh(); var working = process.WorkingSet64; peak = Math.Max(peak, working);
                    maxActive = Math.Max(maxActive, coordinator.Limiter.ActiveRequests);
                    samples.Add(new { Seconds = clock.Elapsed.TotalSeconds, WorkingSetBytes = working, ActiveRequests = coordinator.Limiter.ActiveRequests });
                    await Task.Delay(200);
                }
                process.Refresh(); var cpu = (process.TotalProcessorTime - startCpu).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
                await coordinator.StopAllAsync(); await all;
                await coordinator.RefreshAsync();
                using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "panel.png")); }
                var payload = new
                {
                    SchemaVersion = 1, Mode = "isolated mock, one 6-second representative maximum-concurrency sample",
                    RealKeyReads = 0, RealNetworkRequests = 0, ProcessCount = 1, TaskCount = coordinator.Tasks.Count,
                    InputCharacters = RequestBaseline.InputCharacterTarget, GlobalConcurrency = coordinator.Limiter.Maximum,
                    MaximumActiveRequests = maxActive, PeakWorkingSetMiB = peak / 1048576d, AverageCpuPercent = cpu,
                    DurationSeconds = clock.Elapsed.TotalSeconds, RequestCount = handler.RequestCount,
                    ConfirmedTokens = coordinator.Tasks.Sum(task => task.Snapshot.ConfirmedUsage.TotalTokens),
                    FinalInFlight = coordinator.Tasks.Sum(task => task.Snapshot.InFlightRequests),
                    UnknownRequests = coordinator.Tasks.Sum(task => task.Snapshot.UnknownUsageRequests),
                    Summary = coordinator.BatchSummary, Samples = samples,
                    Limits = "mock does not verify real credits, long-term waits or new-machine installation; bitmap is control rendering"
                };
                await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                if (maxActive != 3 || peak > 150 * 1048576L) result = 1;
            }
            catch (Exception) { result = 1; }
            finally { form.RequestExit(); }
        };
        Application.Run(form);
        return result;
    }
}
