namespace SenseNova.TokenBurner.Core;

/// <summary>全应用共享许可；等待不占在途预算，不启动独立进程。</summary>
public sealed class RequestConcurrencyLimiter(int maximum = RequestBaseline.Concurrency)
{
    private readonly object _gate = new();
    private int _active;
    private int _maximum = maximum is >= 1 and <= RequestBaseline.Concurrency ? maximum
        : throw new ArgumentOutOfRangeException(nameof(maximum));
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ActiveRequests { get { lock (_gate) return _active; } }
    public int Maximum { get { lock (_gate) return _maximum; } }
    public Task Changed { get { lock (_gate) return _changed.Task; } }
    public void SetMaximum(int maximum)
    {
        if (maximum is < 1 or > RequestBaseline.Concurrency) throw new ArgumentOutOfRangeException(nameof(maximum));
        lock (_gate) { _maximum = maximum; PulseLocked(); }
    }
    public IDisposable? TryAcquire()
    {
        lock (_gate)
        {
            if (_active >= _maximum) return null;
            _active++;
            return new Lease(this);
        }
    }
    public async Task<IDisposable> AcquireAsync(CancellationToken token = default)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var changed = Changed;
            if (TryAcquire() is { } lease) return lease;
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }
    private void Release() { lock (_gate) { _active--; PulseLocked(); } }
    private void PulseLocked()
    {
        var previous = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }
    private sealed class Lease(RequestConcurrencyLimiter owner) : IDisposable
    {
        private RequestConcurrencyLimiter? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
