using MeetingDesktop.App.Services;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Workflows;
using System.ComponentModel;
using System.IO;

namespace MeetingDesktop.App.Tests;

public sealed class SessionElevationBrokerTests
{
    [Fact]
    public async Task EvidenceDistinguishesNoSessionFromAttemptedTransportFailure()
    {
        var connection = new FakeConnection();
        using var broker = new SessionElevationBroker(_ => Task.FromResult<ISessionElevationConnection>(connection));
        var absent = broker.RunWithEvidence(ElevatedOperations.RestoreShortcuts);
        Assert.Equal(ElevationOutcome.Failed, absent.Outcome);
        Assert.True(absent.DefinitelyNotDispatched);
        Assert.Empty(connection.Calls);
        Assert.Equal(ElevationOutcome.Succeeded, await broker.AuthorizeAsync());
        var dispatched = broker.RunWithEvidence(ElevatedOperations.RestoreShortcuts);
        Assert.Equal(ElevationOutcome.Succeeded, dispatched.Outcome);
        Assert.False(dispatched.DefinitelyNotDispatched);
        Assert.Single(connection.Calls);
    }

    [Fact]
    public async Task TransportFailureAfterConnectionRunIsAmbiguous()
    {
        var connection = new FakeConnection { OnRun = () => throw new IOException("lost after dispatch") };
        using var broker = new SessionElevationBroker(_ => Task.FromResult<ISessionElevationConnection>(connection));
        Assert.Equal(ElevationOutcome.Succeeded, await broker.AuthorizeAsync());
        var result = broker.RunWithEvidence(ElevatedOperations.RestoreShortcuts);
        Assert.Equal(ElevationOutcome.Failed, result.Outcome);
        Assert.False(result.DefinitelyNotDispatched);
        Assert.Single(connection.Calls);
    }

    [Fact]
    public async Task SubscriberFailureCannotStrandAuthorizationOrHideItFromOtherSubscribers()
    {
        using var broker = new SessionElevationBroker(_ => Task.FromResult<ISessionElevationConnection>(new FakeConnection()));
        int observed = 0;
        broker.AuthorizationChanged += (_, _) => throw new InvalidOperationException("Broken UI subscriber");
        broker.AuthorizationChanged += (_, _) => Interlocked.Increment(ref observed);
        Task<ElevationOutcome>? grant = null;
        Assert.Null(Record.Exception(() => { grant = broker.AuthorizeAsync(); }));
        Assert.Equal(ElevationOutcome.Succeeded, await grant!.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(broker.IsAuthorizing);
        Assert.True(broker.IsAuthorized);
        Assert.True(observed >= 2);
        broker.Revoke();
        Assert.False(broker.IsAuthorized);
    }
    [Fact]
    public void UnauthorisedSwitchFailsWithoutLaunchingAnything()
    {
        int starts = 0;
        using var broker = new SessionElevationBroker(_ => { starts++; throw new Exception("No UAC from Run"); });
        Assert.Equal(ElevationOutcome.Failed, broker.Run(ElevatedOperations.MoveShortcutsAside));
        Assert.False(broker.IsAuthorized);
        Assert.Null(broker.LastReport);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task ProductionDefaultIsUnauthorisedAndDisposedBrokerCannotGrant()
    {
        using ISessionElevationBroker broker = new SessionElevationBroker();
        Assert.False(broker.IsAuthorized);
        Assert.False(broker.IsAuthorizing);
        Assert.Equal(ElevationOutcome.Failed, broker.Run(ElevatedOperations.RestoreShortcuts));
        broker.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => broker.AuthorizeAsync());
        Assert.Equal(ElevationOutcome.Failed, broker.Run(ElevatedOperations.MoveShortcutsAside));
    }

    [Fact]
    public async Task CancellationAfterLaunchClosesLateHelperBeforeAuthorizationReturns()
    {
        var pending = new TaskCompletionSource<ISessionElevationConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var broker = new SessionElevationBroker(_ => { entered.SetResult(); return pending.Task; });
        using var token = new CancellationTokenSource();
        Task<ElevationOutcome> authorization = broker.AuthorizeAsync(token.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        token.Cancel();
        var connection = new FakeConnection();
        pending.SetResult(connection);
        Assert.Equal(ElevationOutcome.Failed, await authorization);
        Assert.True(connection.Disposed);
        Assert.Empty(connection.Calls);
        Assert.False(broker.IsAuthorized);
        Assert.False(broker.IsAuthorizing);
    }

    [Fact]
    public async Task GrantIsReusedForBothOperationsAndRevocationRequiresExplicitGrant()
    {
        int starts = 0, changes = 0;
        var connection = new FakeConnection();
        using var broker = new SessionElevationBroker(_ => { starts++; return Task.FromResult<ISessionElevationConnection>(connection); });
        broker.AuthorizationChanged += (_, _) => Interlocked.Increment(ref changes);
        Assert.Equal(ElevationOutcome.Succeeded, await broker.AuthorizeAsync());
        Assert.True(broker.IsAuthorized);
        Assert.False(broker.IsAuthorizing);
        Assert.Empty(connection.Calls);
        Assert.Equal(ElevationOutcome.Succeeded, await broker.AuthorizeAsync());
        Assert.Equal(1, starts);
        Assert.Equal(ElevationOutcome.Succeeded, broker.Run(ElevatedOperations.MoveShortcutsAside));
        Assert.Equal(ElevationOutcome.Succeeded, broker.Run(ElevatedOperations.RestoreShortcuts));
        Assert.Equal([ElevatedOperations.MoveShortcutsAside, ElevatedOperations.RestoreShortcuts], connection.Calls);
        broker.Revoke();
        Assert.True(connection.Disposed);
        Assert.False(broker.IsAuthorized);
        Assert.Equal(ElevationOutcome.Failed, broker.Run(ElevatedOperations.RestoreShortcuts));
        Assert.Equal(1, starts);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task ConcurrentGrantsCoalesceAndSwitchDoesNotJoinThePendingGrant()
    {
        var pending = new TaskCompletionSource<ISessionElevationConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        using var broker = new SessionElevationBroker(_ => { Interlocked.Increment(ref starts); return pending.Task; });
        Task<ElevationOutcome> first = broker.AuthorizeAsync();
        Task<ElevationOutcome> second = broker.AuthorizeAsync();
        Assert.True(broker.IsAuthorizing);
        Assert.False(broker.IsAuthorized);
        Assert.Equal(ElevationOutcome.Failed, broker.Run(ElevatedOperations.RestoreShortcuts));
        pending.SetResult(new FakeConnection());
        Assert.Equal(ElevationOutcome.Succeeded, await first);
        Assert.Equal(ElevationOutcome.Succeeded, await second);
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedOrDisposedPendingGrantCannotInstallALateHelper(bool dispose)
    {
        var pending = new TaskCompletionSource<ISessionElevationConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var broker = new SessionElevationBroker(_ => { entered.SetResult(); return pending.Task; });
        Task<ElevationOutcome> grant = broker.AuthorizeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (dispose) broker.Dispose(); else broker.Revoke();
        var late = new FakeConnection();
        pending.SetResult(late);
        Assert.Equal(ElevationOutcome.Failed, await grant);
        Assert.True(late.Disposed);
        Assert.False(broker.IsAuthorized);
    }

    [Fact]
    public async Task DeclinedAndCancelledGrantNeverAuthorizesOrMoves()
    {
        using var declined = new SessionElevationBroker(_ => throw new Win32Exception(1223));
        Assert.Equal(ElevationOutcome.Declined, await declined.AuthorizeAsync());
        Assert.False(declined.IsAuthorizing);
        Assert.False(declined.IsAuthorized);
        int starts = 0;
        using var cancelled = new SessionElevationBroker(_ => { starts++; throw new Exception("Must not launch"); });
        using var token = new CancellationTokenSource();
        token.Cancel();
        Assert.Equal(ElevationOutcome.Failed, await cancelled.AuthorizeAsync(token.Token));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task PartialFailureRetainsCountsAndTheAuthenticatedSession()
    {
        var connection = new FakeConnection
        {
            Report = new(false, new(2, [new("source", "requested", "preserved")]), 1, "failed")
        };
        using var broker = new SessionElevationBroker(_ => Task.FromResult<ISessionElevationConnection>(connection));
        await broker.AuthorizeAsync();
        Assert.Equal(ElevationOutcome.Failed, broker.Run(ElevatedOperations.MoveShortcutsAside));
        Assert.Same(connection.Report, broker.LastReport);
        Assert.True(broker.IsAuthorized);
    }

    [Fact]
    public async Task IpcFailureWaitsForHelperCompletionBeforeReturningFailure()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeConnection
        {
            OnRun = () => throw new IOException("Lost response"),
            OnDispose = () => { disposing.SetResult(); stopped.Task.GetAwaiter().GetResult(); }
        };
        using var broker = new SessionElevationBroker(_ => Task.FromResult<ISessionElevationConnection>(connection));
        await broker.AuthorizeAsync();
        Task<ElevationOutcome> run = Task.Run(() => broker.Run(ElevatedOperations.RestoreShortcuts));
        await disposing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(run.IsCompleted);
        stopped.SetResult();
        Assert.Equal(ElevationOutcome.Failed, await run);
        Assert.False(broker.IsAuthorized);
        Assert.Null(broker.LastReport);
        Assert.Equal(ElevationOutcome.Failed, broker.Run(ElevatedOperations.RestoreShortcuts));
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForAnAlreadyRevokingHelper()
    {
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeConnection { OnDispose = () =>
        {
            stopping.SetResult();
            stopped.Task.GetAwaiter().GetResult();
        } };
        using var broker = new SessionElevationBroker(_ => Task.FromResult<ISessionElevationConnection>(connection));
        await broker.AuthorizeAsync();
        Task revoke = Task.Run(broker.Revoke);
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task dispose = Task.Run(broker.Dispose);
        try
        {
            await Task.WhenAny(dispose, Task.Delay(100));
            Assert.False(dispose.IsCompleted);
        }
        finally { stopped.SetResult(); await revoke; await dispose; }
    }

    [Fact]
    public async Task ReplacementAuthorizationWaitsForOldMutationToFinish()
    {
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new FakeConnection { OnRun = () =>
        {
            executing.SetResult();
            completed.Task.GetAwaiter().GetResult();
            throw new IOException("Disconnected");
        } };
        int launches = 0;
        using var broker = new SessionElevationBroker(_ => Task.FromResult<ISessionElevationConnection>(
            Interlocked.Increment(ref launches) == 1 ? old : new FakeConnection()));
        await broker.AuthorizeAsync();
        Task<ElevationOutcome> run = Task.Run(() => broker.Run(ElevatedOperations.RestoreShortcuts));
        await executing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        broker.Revoke();
        Task<ElevationOutcome> replacement = broker.AuthorizeAsync();
        Assert.False(replacement.IsCompleted);
        Assert.Equal(1, launches);
        completed.SetResult();
        Assert.Equal(ElevationOutcome.Failed, await run);
        Assert.Equal(ElevationOutcome.Succeeded, await replacement);
        Assert.Equal(2, launches);
    }

    [Fact]
    public async Task OperationsAreSerializedAndUnexpectedHelperExitNotifies()
    {
        int active = 0, max = 0, changes = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeConnection { OnRun = () =>
        {
            int count = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref max, Math.Max(max, count));
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            Interlocked.Decrement(ref active);
        } };
        using var broker = new SessionElevationBroker(_ => Task.FromResult<ISessionElevationConnection>(connection));
        await broker.AuthorizeAsync();
        var first = Task.Run(() => broker.Run(ElevatedOperations.MoveShortcutsAside));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = Task.Run(() => broker.Run(ElevatedOperations.RestoreShortcuts));
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, max);
        broker.AuthorizationChanged += (_, _) => Interlocked.Increment(ref changes);
        connection.Exit();
        Assert.False(broker.IsAuthorized);
        Assert.True(changes >= 1);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--elevated-restore-shortcuts C:\\caller")]
    [InlineData("--elevated-session")]
    public void OnlyFixedOperationsAreAccepted(string argument)
    {
        using var broker = new SessionElevationBroker(_ => throw new Exception("No launch"));
        Assert.Throws<ArgumentException>(() => broker.Run(argument));
    }

    private sealed class FakeConnection : ISessionElevationConnection
    {
        public bool IsAlive { get; private set; } = true;
        public bool Disposed { get; private set; }
        public event EventHandler? Closed;
        internal ElevatedOperationReport Report { get; init; } = new(true, new(0, []));
        internal List<string> Calls { get; } = [];
        internal Action? OnRun { get; init; }
        internal Action? OnDispose { get; init; }
        public ElevatedOperationReport Run(string argument) { Calls.Add(argument); OnRun?.Invoke(); return Report; }
        public void Exit() { IsAlive = false; Closed?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { if (Disposed) return; Disposed = true; IsAlive = false; OnDispose?.Invoke(); }
    }
}
