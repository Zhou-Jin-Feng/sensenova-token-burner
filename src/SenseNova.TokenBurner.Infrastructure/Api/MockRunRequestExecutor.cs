using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Api;

/// <summary>单轮生命周期开发mock；无HTTP、凭据、端口或额外进程。</summary>
public sealed class MockRunRequestExecutor : IRunRequestExecutor
{
    private readonly TimeSpan _latency;

    public MockRunRequestExecutor(TimeSpan? latency = null)
    {
        _latency = latency ?? TimeSpan.FromMilliseconds(50);
        if (_latency < TimeSpan.Zero || _latency.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(latency));
    }

    public async Task<TokenUsage> ExecuteAsync(RunParameters parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        return new(128, 8, 136);
    }
}
