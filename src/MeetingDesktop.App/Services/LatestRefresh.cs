namespace MeetingDesktop.App.Services;

/// <summary>One background read at a time; only the newest request may update the view.</summary>
internal sealed class LatestRefresh<TRequest, TResult>(Func<TRequest, TResult> read, Action<TResult> apply) : IDisposable
{
    private readonly object _gate = new();
    private (long Version, TRequest Request)? _pending;
    private TaskCompletionSource? _running;
    private long _version;
    private bool _disposed;

    public Task Request(TRequest request)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            _pending = (++_version, request);
            if (_running is not null) return _running.Task;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running = completion;
            _ = RunAsync(completion);
            return completion.Task;
        }
    }

    private async Task RunAsync(TaskCompletionSource completion)
    {
        while (true)
        {
            (long Version, TRequest Request) next;
            lock (_gate)
            {
                if (_disposed || _pending is null)
                {
                    _running = null;
                    completion.TrySetResult();
                    return;
                }
                next = _pending.Value;
                _pending = null;
            }
            try
            {
                TResult result = await Task.Run(() => read(next.Request));
                lock (_gate)
                    if (!_disposed && next.Version == _version) apply(result);
            }
            catch (Exception)
            {
                // Optional read-only presentation must not abort a switch.
                // A newer pending request is still processed after this failure.
            }
        }
    }

    public void Invalidate()
    {
        lock (_gate) { _version++; _pending = null; }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _version++; _pending = null; }
    }
}
