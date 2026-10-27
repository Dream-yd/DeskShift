namespace MeetingDesktop.Core;

internal interface IShellWorkerConnection : IDisposable
{
    bool IsAlive { get; }
    DesktopShellWorker.ShellResponse Send(DesktopShellWorker.ShellRequest request, CancellationToken token);
}

/// <summary>Serializes commands and joins the failed helper before callers can roll back.</summary>
internal sealed class ShellWorkerSession(Func<CancellationToken, IShellWorkerConnection> connect,
    TimeSpan? operationTimeout = null) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan timeout = operationTimeout ?? TimeSpan.FromSeconds(20);
    private IShellWorkerConnection? connection;

    internal Task WarmUpAsync(CancellationToken token = default) => Task.Run(() =>
        Invoke(new() { Operation = "ping" }, token), token);

    internal DesktopShellWorker.ShellResponse Invoke(DesktopShellWorker.ShellRequest request,
        CancellationToken token = default, bool retire = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        gate.Wait(linked.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            linked.CancelAfter(timeout);
            if (connection is null || !connection.IsAlive)
            {
                Reset();
                connection = connect(linked.Token);
            }
            var response = connection.Send(request, linked.Token);
            // Remote application errors are still valid replies. Do not replay a setter.
            return response;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            Reset();
            throw DesktopShellWorker.HelperTimeout(request.Operation);
        }
        catch { Reset(); throw; }
        finally
        {
            try { if (retire) Reset(); }
            finally { gate.Release(); }
        }
    }

    private void Reset()
    {
        var previous = connection;
        connection = null;
        previous?.Dispose();
    }

    public void Dispose()
    {
        // Cancel before acquiring the gate so startup/read timeouts do not delay exit.
        lifetime.Cancel();
        gate.Wait();
        try { Reset(); }
        finally { gate.Release(); }
    }
}
