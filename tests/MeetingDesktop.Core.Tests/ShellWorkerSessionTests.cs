using System.IO.Pipes;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class ShellWorkerSessionTests
{
    [Fact]
    public void ReusesOneConnectionForRepeatedReads()
    {
        int starts = 0;
        var child = new Connection();
        using var session = new ShellWorkerSession(_ => { starts++; return child; });
        session.Invoke(new() { Operation = "capture" });
        session.Invoke(new() { Operation = "capture" });
        Assert.Equal(1, starts);
        Assert.Equal(2, child.Requests);
        Assert.False(child.Disposed);
    }

    [Fact]
    public void AnIdleDeadHelperIsReplacedBeforeSending()
    {
        var first = new Connection();
        var second = new Connection();
        int starts = 0;
        using var session = new ShellWorkerSession(_ => ++starts == 1 ? first : second);
        session.Invoke(new() { Operation = "capture" });
        first.IsAlive = false;
        session.Invoke(new() { Operation = "capture" });
        Assert.True(first.Disposed);
        Assert.Equal(1, first.Requests);
        Assert.Equal(1, second.Requests);
    }

    [Fact]
    public async Task RetiredVerifierNeverCarriesShellStateIntoTheNextSwitch()
    {
        var children = new List<Connection>();
        using var session = new ShellWorkerSession(_ => { var child = new Connection(); children.Add(child); return child; });
        await session.WarmUpAsync();
        session.Invoke(new() { Operation = "verify" }, retire: true);
        Assert.True(children[0].Disposed);
        await session.WarmUpAsync();
        Assert.Equal(2, children.Count);
        Assert.Equal("ping", Assert.Single(children[1].Operations));
        Assert.Equal(new[] { "ping", "verify" }, children[0].Operations);
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("place")]
    [InlineData("capture")]
    public void BrokenTransportIsJoinedAndNeverReplaysAnUncertainCommand(string operation)
    {
        int starts = 0;
        var broken = new Connection((_, _) => throw new EndOfStreamException("Lost after dispatch."));
        var replacement = new Connection();
        using var session = new ShellWorkerSession(_ => ++starts == 1 ? broken : replacement);
        Assert.Throws<EndOfStreamException>(() => session.Invoke(new() { Operation = operation }));
        Assert.True(broken.Disposed);
        Assert.Equal(1, starts);
        Assert.Equal(1, broken.Requests);
        session.Invoke(new() { Operation = "capture" });
        Assert.Equal(2, starts);
        Assert.Equal("capture", Assert.Single(replacement.Operations));
    }

    [Theory]
    [InlineData("switch", true)]
    [InlineData("verify", true)]
    [InlineData("capture", false)]
    [InlineData("place", false)]
    public void TimeoutStopsHelperBeforeReturningTheCorrectFailure(string operation, bool verificationFailure)
    {
        var child = new Connection((_, token) =>
        {
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        });
        using var session = new ShellWorkerSession(_ => child, TimeSpan.FromMilliseconds(50));
        var error = Assert.ThrowsAny<TimeoutException>(() => session.Invoke(new() { Operation = operation }));
        Assert.Equal(verificationFailure, error is DesktopViewVerificationException);
        Assert.True(child.Disposed);
    }

    [Fact]
    public void PreCancelledRequestDoesNotLaunchAProcess()
    {
        int starts = 0;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var session = new ShellWorkerSession(_ => { starts++; return new Connection(); });
        Assert.ThrowsAny<OperationCanceledException>(() => session.Invoke(new() { Operation = "switch" }, cancel.Token));
        Assert.Equal(0, starts);
    }

    [Fact]
    public void CallerCancellationIsNotReportedAsTimeoutAndJoinsHelper()
    {
        using var cancel = new CancellationTokenSource();
        var child = new Connection((_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return new(); });
        using var session = new ShellWorkerSession(_ => child);
        Assert.ThrowsAny<OperationCanceledException>(() => session.Invoke(new() { Operation = "switch" }, cancel.Token));
        Assert.True(child.Disposed);
    }

    [Fact]
    public async Task ShutdownCancelsAnInFlightCommandAndPreventsLateWarmup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new Connection((_, token) =>
        {
            entered.SetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return new();
        });
        using var session = new ShellWorkerSession(_ => child);
        Task pending = Task.Run(() => session.Invoke(new() { Operation = "capture" }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(child.Disposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WarmUpAsync());
        Assert.Equal(1, child.Requests);
    }

    [Fact]
    public async Task ParallelCallersCannotInterleaveProtocolFrames()
    {
        int active = 0;
        int highest = 0;
        var child = new Connection((_, _) =>
        {
            int count = Interlocked.Increment(ref active);
            highest = Math.Max(highest, count);
            Thread.SpinWait(20000);
            Interlocked.Decrement(ref active);
            return new();
        });
        using var session = new ShellWorkerSession(_ => child);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => session.Invoke(new() { Operation = "capture" }))));
        Assert.Equal(1, highest);
        Assert.Equal(12, child.Requests);
    }

    [Fact]
    public void RemoteOperationFailureDoesNotReplayOrDiscardHealthyTransport()
    {
        var child = new Connection((_, _) => DesktopShellWorker.DescribeFailure(new IOException("Denied.")));
        using var session = new ShellWorkerSession(_ => child);
        var reply = session.Invoke(new() { Operation = "switch" });
        Assert.Throws<InvalidOperationException>(() => DesktopShellWorker.ValidateResponse(reply, 0));
        Assert.False(child.Disposed);
        Assert.Equal(1, child.Requests);
    }

    [Fact]
    public void FrameRoundTripsIconIdentityAndStructuredError()
    {
        using var stream = new MemoryStream();
        var expected = new DesktopShellWorker.ShellResponse
        {
            Icons = new[] { new IconPosition("项目", -1910, 360, @"C:\Example\Desktop\项目", true) },
            Error = "核验失败", HResult = -1, ViewVerificationFailed = true,
        };
        ShellWorkerProtocol.Write(stream, expected);
        stream.Position = 0;
        var actual = ShellWorkerProtocol.Read<DesktopShellWorker.ShellResponse>(stream);
        Assert.Equal(expected.Icons, actual.Icons);
        Assert.Equal(expected.Error, actual.Error);
        Assert.Equal(expected.HResult, actual.HResult);
        Assert.True(actual.ViewVerificationFailed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(ShellWorkerProtocol.MaximumFrameBytes + 1)]
    public void RejectsInvalidFrameSizesBeforeReadingPayload(int size)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(size));
        Assert.Throws<IOException>(() => ShellWorkerProtocol.Read<DesktopShellWorker.ShellRequest>(stream));
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public void RejectsTruncatedPayload()
    {
        using var stream = new MemoryStream(new byte[] { 3, 0, 0, 0, 123 });
        Assert.Throws<EndOfStreamException>(() => ShellWorkerProtocol.Read<DesktopShellWorker.ShellRequest>(stream));
    }

    [Fact]
    public void RejectsCommandsBeforeHandshakeWithoutExecutingThem()
    {
        using var stream = new MemoryStream();
        ShellWorkerProtocol.Write(stream, new DesktopShellWorker.ShellRequest { Operation = "switch" });
        stream.Position = 0;
        int executed = 0;
        Assert.Throws<IOException>(() => ShellWorkerProtocol.Serve(stream, _ => { executed++; return new(); }));
        Assert.Equal(0, executed);
    }

    [Fact]
    public async Task RealPipeKeepsAllExecutionsOnOneStaThreadAndWarmupHasNoShellReads()
    {
        string id = "MeetingDesktop.ProtocolTest." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(id, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", id, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task connection = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await connection;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<(int Thread, ApartmentState Apartment, string Operation)>();
        var thread = new Thread(() =>
        {
            try
            {
                ShellWorkerProtocol.Serve(client, request =>
                {
                    calls.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), request.Operation));
                    return new() { Icons = Array.Empty<IconPosition>() };
                }, timeout.Token);
            }
            catch (EndOfStreamException) { }
            catch (Exception error) { finished.TrySetException(error); }
            finally { finished.TrySetResult(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        foreach (string operation in new[] { "hello-v1", "ping", "capture", "capture" })
        {
            ShellWorkerProtocol.Write(server, new DesktopShellWorker.ShellRequest { Operation = operation }, timeout.Token);
            var reply = ShellWorkerProtocol.Read<DesktopShellWorker.ShellResponse>(server, timeout.Token);
            if (operation is "ping" or "hello-v1") Assert.True(reply.Ready);
            else Assert.Empty(reply.Icons!);
        }
        server.Dispose();
        await finished.Task.WaitAsync(timeout.Token);
        Assert.Equal(2, calls.Count);
        Assert.Single(calls.Select(call => call.Thread).Distinct());
        Assert.All(calls, call => Assert.Equal(ApartmentState.STA, call.Apartment));
    }

    private sealed class Connection(Func<DesktopShellWorker.ShellRequest, CancellationToken, DesktopShellWorker.ShellResponse>? execute = null)
        : IShellWorkerConnection
    {
        public bool IsAlive { get; set; } = true;
        internal bool Disposed { get; private set; }
        internal int Requests { get; private set; }
        internal List<string> Operations { get; } = [];
        public DesktopShellWorker.ShellResponse Send(DesktopShellWorker.ShellRequest request, CancellationToken token)
        {
            Requests++;
            Operations.Add(request.Operation);
            return execute?.Invoke(request, token) ?? new() { Ready = true };
        }
        public void Dispose() { Disposed = true; IsAlive = false; }
    }
}
