using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure;
using SenseNova.TokenBurner.Infrastructure.Api;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Desktop.Diagnostics;

public sealed record EngineRoundMeasurement(string Phase, long Requests, long SimulatedTokens,
    double DurationSeconds, long AllocatedBytes, ulong ProcessWriteBytes, long StorageWrites,
    long StorageLogicalBytes, double StorageWriteMilliseconds, double MaximumStorageWriteMilliseconds,
    long MaximumStateFileBytes, RunEndReason? EndReason);

/// <summary>开发测量驱动：与正式HTTP/预算/记录共用路径，普通启动不执行。</summary>
public static class EngineResourceProbe
{
    public static async Task RunAsync(ResourceProbeOptions options, MockSenseNovaHandler handler,
        CancellationToken cancellationToken = default, long targetTokens = 120_000_000,
        int inputCharacters = RequestBaseline.InputCharacterTarget)
    {
        PersistentRunSession? runtime = null;
        SingleRunEngine? plain = null;
        MeasuredStore? measuredStore = null;
        var rounds = new List<EngineRoundMeasurement>(2);
        try
        {
            await WriteStateAsync(options, "Setup", rounds).ConfigureAwait(false);
            using var http = new HttpClient(handler, disposeHandler: false)
            { BaseAddress = new Uri(SenseNovaDefaults.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };
            var client = new SenseNovaHttpClient(http);
            var model = (await client.GetModelsAsync(MockCredential.Value, cancellationToken).ConfigureAwait(false))
                .Single(model => model.IsRebateEligible);
            var input = await Task.Run(() => MockCompletionInput.Create((inputCharacters + 1) / 2, inputCharacters),
                cancellationToken).ConfigureAwait(false);
            var executor = new CompletionRunExecutor(client, MockCredential.Value, model, input);
            var parameters = new RunParameters(model.Id, targetTokens)
            { MaximumConcurrency = 3, RequestTokenReservation = input.EstimatedTotalTokens };
            var profile = Path.Combine(options.OutputDirectory, "engine-profile");
            var innerStore = new JsonRunStateStore(profile);
            // 隔离测试资料模拟已经保留20条历史时的最大常规记录，不能当真实账户历史。
            var now = DateTimeOffset.UtcNow;
            var past = new RunSnapshot(RunState.Completed, parameters, new(0, 0, 0), 0, 0, 0, null)
            { EndReason = RunEndReason.BudgetRemainder };
            var seeded = RunStateDocument.Empty with
            {
                History = Enumerable.Range(0, RunStateDocument.MaximumHistory).Select(index =>
                    new RunRecord(Guid.NewGuid(), now.AddMinutes(-index - 1), now.AddMinutes(-index - 1), past)).ToArray()
            };
            if (await innerStore.LoadAsync(cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("测量目录已有运行资料，请使用新的隔离目录。");
            await innerStore.SaveAsync(seeded, cancellationToken).ConfigureAwait(false);
            measuredStore = new MeasuredStore(innerStore, profile);
            runtime = new(executor, measuredStore);
            await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await runtime.ConfirmRecoveryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await WriteStateAsync(options, "Warmup", rounds).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(options.WarmupSeconds), cancellationToken).ConfigureAwait(false);

            await WriteStateAsync(options, "LoadPlain", rounds).ConfigureAwait(false);
            plain = new(executor);
            var start = Capture();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(options.LoadSeconds));
                var result = await plain.RunAsync(parameters, deadline.Token).ConfigureAwait(false);
                ValidateRound(result, handler, start.Requests, inputCharacters);
                rounds.Add(Measure("LoadPlain", result, start, null));
            }

            await WriteStateAsync(options, "LoadPersisted", rounds).ConfigureAwait(false);
            measuredStore.Reset();
            start = Capture();
            await runtime.EnableScheduleAsync(new(parameters, 5), cancellationToken).ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            while (runtime.Schedule.LastRun is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed >= TimeSpan.FromSeconds(options.LoadSeconds))
                    throw new TimeoutException("持久运行未在测量时限内完整结束。");
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
            var persisted = runtime.Schedule.LastRun!;
            ValidateRound(persisted, handler, start.Requests, inputCharacters);
            rounds.Add(Measure("LoadPersisted", persisted, start, measuredStore));
            if (runtime.Recovery.Document!.History.Length != RunStateDocument.MaximumHistory)
                throw new InvalidOperationException("测量历史未保持上限。");

            await WriteStateAsync(options, "Idle", rounds).ConfigureAwait(false);
            var requestsBeforeWait = handler.RequestCount;
            var writesBeforeWait = measuredStore.Writes;
            var ioBeforeWait = ReadIo().WriteTransferCount;
            await Task.Delay(TimeSpan.FromSeconds(options.IdleSeconds), cancellationToken).ConfigureAwait(false);
            if (handler.RequestCount != requestsBeforeWait || measuredStore.Writes != writesBeforeWait
                || runtime.Schedule.State != ScheduleState.Enabled || runtime.Schedule.NextRunUtc <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("五小时计划等待期间出现请求、落盘或计划异常。");
            var waitWriteBytes = ReadIo().WriteTransferCount - ioBeforeWait;
            var waitStateWrites = measuredStore.Writes - writesBeforeWait;
            await runtime.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            if (runtime.Schedule.State != ScheduleState.Shutdown || runtime.Schedule.NextRunUtc is not null
                || runtime.CurrentRun.InFlightRequests != 0)
                throw new InvalidOperationException("退出未完成调度与在途收尾。");
            await WriteStateAsync(options, "Completed", rounds, new
            {
                InputCharacters = inputCharacters, MaximumConcurrency = 3, TargetTokens = targetTokens,
                SeededHistory = RunStateDocument.MaximumHistory, IntervalHours = 5,
                WaitSeconds = options.IdleSeconds, WaitRequests = handler.RequestCount - requestsBeforeWait,
                WaitStateWrites = waitStateWrites,
                WaitProcessWriteBytes = waitWriteBytes, ShutdownState = runtime.Schedule.State.ToString(),
                RemainingInFlight = runtime.CurrentRun.InFlightRequests, StateFileBytes = measuredStore.MaximumBytes,
                Rounds = rounds
            }).ConfigureAwait(false);

            StartMeasurement Capture() => new(Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(false),
                ReadIo().WriteTransferCount, handler.SuccessfulCompletionCount);
        }
        catch (OperationCanceledException)
        {
            await WriteStateAsync(options, "Aborted", rounds).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await WriteStateAsync(options, "Failed", rounds, new
            {
                FailureType = exception.GetType().Name, Run = runtime?.CurrentRun, Recovery = runtime?.Recovery.State.ToString(),
                StorageErrorType = measuredStore?.ErrorType, StorageNativeErrorCode = measuredStore?.NativeErrorCode
            }).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (plain is not null) await plain.StopAsync().ConfigureAwait(false);
            if (runtime is not null && runtime.Schedule.State != ScheduleState.Shutdown)
                await runtime.ShutdownAsync().ConfigureAwait(false);
        }
    }

    private static void ValidateRound(RunSnapshot run, MockSenseNovaHandler handler, int requestsBefore, int characters)
    {
        var requests = handler.SuccessfulCompletionCount - requestsBefore;
        var perRequest = (characters + 1) / 2 + 8L;
        if (run.State != RunState.Completed || run.InFlightRequests != 0 || run.UnknownUsageRequests != 0
            || run.CompletedRequests != requests || requests <= 0 || run.ConfirmedUsage.TotalTokens != requests * perRequest)
            throw new InvalidOperationException("完整mock运行未正常结算，不能记作资源通过。");
    }

    private static EngineRoundMeasurement Measure(string phase, RunSnapshot result, StartMeasurement start, MeasuredStore? store)
        => new(phase, result.CompletedRequests, result.ConfirmedUsage.TotalTokens,
            Stopwatch.GetElapsedTime(start.Timestamp).TotalSeconds, GC.GetTotalAllocatedBytes(false) - start.AllocatedBytes,
            ReadIo().WriteTransferCount - start.WriteBytes, store?.Writes ?? 0, store?.LogicalBytes ?? 0,
            store?.TotalMilliseconds ?? 0, store?.MaximumMilliseconds ?? 0, store?.MaximumBytes ?? 0, result.EndReason);

    private static async Task WriteStateAsync(ResourceProbeOptions options, string phase,
        IReadOnlyList<EngineRoundMeasurement> rounds, object? engine = null)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var path = Path.Combine(options.OutputDirectory, "probe-state.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
            {
                Phase = phase, PhaseStartedUtc = DateTimeOffset.UtcNow, RuntimeVersion = Environment.Version.ToString(),
                NetworkEnabled = false, Frontend = "WinForms", SuccessfulMockRequests = rounds.Sum(round => round.Requests),
                SimulatedTokens = rounds.Sum(round => round.SimulatedTokens), Engine = engine
            })).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record StartMeasurement(long Timestamp, long AllocatedBytes, ulong WriteBytes, int Requests);

    private sealed class MeasuredStore(IRunStateStore inner, string profile) : IRunStateStore
    {
        public long Writes { get; private set; }
        public long LogicalBytes { get; private set; }
        public long MaximumBytes { get; private set; }
        public double TotalMilliseconds { get; private set; }
        public double MaximumMilliseconds { get; private set; }
        public string? ErrorType { get; private set; }
        public int? NativeErrorCode { get; private set; }
        public void Reset() { Writes = LogicalBytes = MaximumBytes = 0; TotalMilliseconds = MaximumMilliseconds = 0; }
        public Task<RunStateDocument?> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public async Task SaveAsync(RunStateDocument document, CancellationToken cancellationToken = default)
        {
            var start = Stopwatch.GetTimestamp();
            try { await inner.SaveAsync(document, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception)
            {
                ErrorType = exception.GetType().Name;
                NativeErrorCode = (exception as LocalStorageException)?.NativeErrorCode;
                throw;
            }
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var bytes = new FileInfo(Path.Combine(profile, "run-state.json")).Length;
            Writes++;
            LogicalBytes += bytes;
            MaximumBytes = Math.Max(MaximumBytes, bytes);
            TotalMilliseconds += elapsed;
            MaximumMilliseconds = Math.Max(MaximumMilliseconds, elapsed);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint process, out IoCounters counters);

    private static IoCounters ReadIo()
    {
        using var process = Process.GetCurrentProcess();
        if (!GetProcessIoCounters(process.Handle, out var counters)) throw new IOException("读取本进程IO计数失败。");
        return counters;
    }
}
