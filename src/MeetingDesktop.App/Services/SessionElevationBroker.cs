using MeetingDesktop.Core;
using MeetingDesktop.Core.Workflows;
using System.ComponentModel;
using System.IO;

namespace MeetingDesktop.App.Services;

public interface ISessionElevationBroker : IElevationBroker, IDisposable
{
    bool IsAuthorized { get; }
    bool IsAuthorizing { get; }
    /// <summary>May be raised on a worker thread; UI subscribers must dispatch to the UI thread.</summary>
    event EventHandler? AuthorizationChanged;
    Task<ElevationOutcome> AuthorizeAsync(CancellationToken token = default);
    void Revoke();
}

internal interface ISessionElevationConnection : IDisposable
{
    bool IsAlive { get; }
    event EventHandler? Closed;
    ElevatedOperationReport Run(string argument);
}

public sealed class SessionElevationBroker : ISessionElevationBroker
{
    private readonly object stateGate = new();
    private readonly object terminationGate = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly Func<CancellationToken, Task<ISessionElevationConnection>> connect;
    private ISessionElevationConnection? session;
    private Task<ElevationOutcome>? authorization;
    private CancellationTokenSource? authorizationCancellation;
    private ElevatedOperationReport? lastReport;
    private long generation;
    private bool disposed;

    public SessionElevationBroker() : this(ConnectAsync) { }
    internal SessionElevationBroker(Func<CancellationToken, Task<ISessionElevationConnection>> connect) =>
        this.connect = connect ?? throw new ArgumentNullException(nameof(connect));

    public bool IsAuthorized { get { lock (stateGate) return !disposed && session is { IsAlive: true }; } }
    public bool IsAuthorizing { get { lock (stateGate) return authorization is not null; } }
    public ElevatedOperationReport? LastReport => Volatile.Read(ref lastReport);
    public event EventHandler? AuthorizationChanged;

    /// <summary>The only method that may request UAC. Concurrent requests share the first caller's cancellation.</summary>
    public Task<ElevationOutcome> AuthorizeAsync(CancellationToken token = default)
    {
        TaskCompletionSource<ElevationOutcome> completion;
        CancellationTokenSource cancellation;
        ISessionElevationConnection? stale;
        long requestGeneration;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (token.IsCancellationRequested) return Task.FromResult(ElevationOutcome.Failed);
            if (authorization is not null) return authorization;
            if (session is { IsAlive: true }) return Task.FromResult(ElevationOutcome.Succeeded);
            stale = session;
            session = null;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            authorizationCancellation = cancellation;
            requestGeneration = generation;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            authorization = completion.Task;
        }
        Notify();
        // UAC's synchronous consent wait and subsequent IPC never block the GUI dispatcher.
        _ = Task.Run(() => AuthorizeCoreAsync(requestGeneration, cancellation, stale, completion));
        return completion.Task;
    }

    private async Task AuthorizeCoreAsync(long requestGeneration, CancellationTokenSource cancellation,
        ISessionElevationConnection? stale, TaskCompletionSource<ElevationOutcome> completion)
    {
        ISessionElevationConnection? candidate = null;
        ElevationOutcome outcome = ElevationOutcome.Failed;
        try
        {
            // Drain any old Run before granting a replacement session, without making switches wait on UAC.
            await operationGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            operationGate.Release();
            lock (terminationGate) stale?.Dispose();
            cancellation.Token.ThrowIfCancellationRequested();
            candidate = await connect(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            lock (stateGate)
            {
                if (!disposed && requestGeneration == generation && candidate.IsAlive)
                {
                    candidate.Closed += SessionClosed;
                    session = candidate;
                    candidate = null;
                    outcome = ElevationOutcome.Succeeded;
                }
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223 && !cancellation.IsCancellationRequested)
        { outcome = ElevationOutcome.Declined; }
        catch (Exception ex) when (ElevatedSession.IsTransportFailure(ex)) { }
        finally
        {
            // In particular, revoke/cancellation while the OS consent dialog is open cannot strand a late helper.
            candidate?.Dispose();
            lock (stateGate)
            {
                authorizationCancellation = null;
                authorization = null;
                cancellation.Dispose();
            }
            Notify();
            completion.TrySetResult(outcome);
        }
    }

    /// <summary>Uses only the existing grant. Missing permission returns Failed with no launch or file access.</summary>
    public ElevationOutcome Run(string argument) => RunWithEvidence(argument).Outcome;

    public ElevationRunResult RunWithEvidence(string argument)
    {
        if (!ElevatedOperations.IsOperation(argument)) throw new ArgumentException("Unknown elevated operation.", nameof(argument));
        operationGate.Wait();
        try
        {
            Volatile.Write(ref lastReport, null);
            ISessionElevationConnection? current;
            lock (stateGate) current = disposed ? null : session;
            if (current is null) return new(ElevationOutcome.Failed, DefinitelyNotDispatched: true);
            if (!current.IsAlive) { Invalidate(current); return new(ElevationOutcome.Failed, DefinitelyNotDispatched: true); }
            try
            {
                ElevatedOperationReport report = current.Run(argument);
                Volatile.Write(ref lastReport, report);
                return new(report.Succeeded ? ElevationOutcome.Succeeded : ElevationOutcome.Failed);
            }
            catch (Exception ex) when (ElevatedSession.IsTransportFailure(ex))
            {
                // Dispose waits for actual process completion, including when another thread already revoked it.
                Invalidate(current);
                return new(ElevationOutcome.Failed);
            }
        }
        finally { operationGate.Release(); }
    }

    private void SessionClosed(object? sender, EventArgs e)
    {
        if (sender is ISessionElevationConnection current) Invalidate(current);
    }

    private void Invalidate(ISessionElevationConnection current)
    {
        bool changed;
        lock (terminationGate)
        {
            lock (stateGate)
            {
                changed = ReferenceEquals(session, current);
                if (changed) session = null;
                current.Closed -= SessionClosed;
            }
            current.Dispose();
        }
        if (changed) Notify();
    }

    public void Revoke() => Stop(disposing: false);
    public void Dispose() => Stop(disposing: true);

    private void Stop(bool disposing)
    {
        lock (terminationGate)
        {
            ISessionElevationConnection? current;
            lock (stateGate)
            {
                disposed |= disposing;
                generation++;
                authorizationCancellation?.Cancel();
                current = session;
                session = null;
                if (current is not null) current.Closed -= SessionClosed;
            }
            // No state lock is held while the helper finishes. All stop callers share this completion barrier.
            current?.Dispose();
        }
        Notify();
    }

    private void Notify()
    {
        if (AuthorizationChanged is not { } subscribers) return;
        foreach (EventHandler subscriber in subscribers.GetInvocationList())
        {
            // UI observers cannot own the helper lifecycle or prevent a grant
            // task from completing. Notify the remaining observers as well.
            try { subscriber(this, EventArgs.Empty); }
            catch (Exception) { }
        }
    }

    private static async Task<ISessionElevationConnection> ConnectAsync(CancellationToken token)
    {
        var client = new ElevatedSessionClient();
        try
        {
            await client.OpenAsync(token).ConfigureAwait(false);
            return new Connection(client);
        }
        catch { client.Dispose(); throw; }
    }

    private sealed class Connection : ISessionElevationConnection
    {
        private readonly ElevatedSessionClient client;
        internal Connection(ElevatedSessionClient client)
        {
            this.client = client;
            client.Closed += OnClosed;
        }
        public bool IsAlive => client.IsAlive;
        public event EventHandler? Closed;
        public ElevatedOperationReport Run(string argument) => client.Run(argument);
        private void OnClosed(object? sender, EventArgs e) => Closed?.Invoke(this, EventArgs.Empty);
        public void Dispose() { client.Closed -= OnClosed; client.Dispose(); }
    }
}
