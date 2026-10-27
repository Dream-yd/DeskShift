using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

namespace MeetingDesktop.Core.Tests;

public sealed class ElevatedSessionTests
{
    [Theory]
    [InlineData("--elevated-session")]
    [InlineData("--elevated-session", "C:\\caller", "1", "1")]
    [InlineData("--elevated-session", "0123456789abcdef0123456789abcdef", "0", "1")]
    [InlineData("--elevated-session", "0123456789abcdef0123456789abcdef", "1", "0")]
    [InlineData("--elevated-session", "0123456789abcdef0123456789abcdef", "1", "1", "extra")]
    [InlineData("--elevated-restore-shortcuts", "--elevated-session")]
    [InlineData("--elevated-session", "00000000000000000000000000000000", "1", "1")]
    [InlineData("--elevated-session", "0123456789abcdef0123456789abcdef", "+1", "1")]
    [InlineData("--elevated-session", "0123456789abcdef0123456789abcdef", "1", "9223372036854775807")]
    public void MalformedHelperInvocationIsHandledWithFailure(params string[] arguments)
    {
        Assert.True(ElevatedSession.TryRun(arguments, out int code));
        Assert.Equal(1, code);
    }

    [Fact]
    public void OrdinaryStartupIsNotHandled()
    {
        Assert.False(ElevatedSession.TryRun([], out int code));
        Assert.Equal(0, code);
        Assert.False(ElevatedSession.TryRun(null!, out _));
        Assert.False(ElevatedSession.TryRun(["--ordinary"], out _));
    }

    [Fact]
    public void ParentIdentityIncludesCreationTimeAndAccountUserSid()
    {
        using var parent = Process.GetCurrentProcess();
        long ticks = parent.StartTime.ToUniversalTime().Ticks;
        Assert.True(ElevatedSession.MatchesParent(parent, ticks));
        Assert.False(ElevatedSession.MatchesParent(parent, ticks + 1));
        Assert.True(ElevatedReportChannel.IsSameProcessUser(parent.Id));
        Assert.False(ElevatedReportChannel.IsSameProcessUser(int.MaxValue));
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            ElevatedSession.IsProcessElevated(parent.Id));
        Assert.False(ElevatedSession.IsProcessElevated(int.MaxValue));
    }

    [Fact]
    public void LaunchContainsOnlyRandomPipeAndExactParentIdentity()
    {
        using var first = new ElevatedSessionClient();
        using var second = new ElevatedSessionClient();
        var info = first.BuildRelaunchInfo();
        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.Equal(4, info.ArgumentList.Count);
        Assert.Equal(ElevatedSession.HelperSwitch, info.ArgumentList[0]);
        Assert.True(Guid.TryParseExact(info.ArgumentList[1], "N", out _));
        Assert.NotEqual(info.ArgumentList[1], second.BuildRelaunchInfo().ArgumentList[1]);
        Assert.Equal(Environment.ProcessId.ToString(), info.ArgumentList[2]);
        using var process = Process.GetCurrentProcess();
        Assert.Equal(process.StartTime.ToUniversalTime().Ticks.ToString(), info.ArgumentList[3]);
    }

    [Fact]
    public async Task WrongKernelClientPidIsRejectedBeforeHandshakeAndPipeAllowsOnlyOneConnection()
    {
        using var server = new ElevatedSessionClient();
        string id = server.BuildRelaunchInfo().ArgumentList[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        // PID 4 is the Windows System process, never the test process connected below.
        using var differentProcess = Process.GetProcessById(4);
        Task authenticate = server.AuthenticateAsync(differentProcess, timeout.Token);
        using var peer = new NamedPipeClientStream(".", ElevatedSession.PipePrefix + id,
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await peer.ConnectAsync(timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => authenticate);
        using var noHello = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await peer.ReadExactlyAsync(new byte[1], noHello.Token));
        using var second = new NamedPipeClientStream(".", ElevatedSession.PipePrefix + id,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await Assert.ThrowsAsync<TimeoutException>(() => second.ConnectAsync(100));
    }

    [Fact]
    public async Task MediumTokenCannotAuthenticateAsAnElevatedHelper()
    {
        if (ElevatedSession.IsProcessElevated(Environment.ProcessId)) return;
        using var server = new ElevatedSessionClient();
        string id = server.BuildRelaunchInfo().ArgumentList[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var process = Process.GetCurrentProcess();
        Task authenticate = server.AuthenticateAsync(process, timeout.Token);
        using var peer = new NamedPipeClientStream(".", ElevatedSession.PipePrefix + id,
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await peer.ConnectAsync(timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => authenticate);
    }

    [Fact]
    public async Task CancelledAuthorizationDoesNotLaunchAProcess()
    {
        using var client = new ElevatedSessionClient();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.OpenAsync(cancelled.Token));
    }

    [Fact]
    public void InvalidParentCreationTimeFailsBeforeConnectingOrTouchingFiles()
    {
        Assert.True(ElevatedSession.TryRun([ElevatedSession.HelperSwitch, Guid.NewGuid().ToString("N"),
            Environment.ProcessId.ToString(), "1"], out int code));
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task KernelDisconnectProbeDetectsClosedParentPipe()
    {
        string id = Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(ElevatedSession.PipePrefix + id, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var peer = new NamedPipeClientStream(".", ElevatedSession.PipePrefix + id,
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var connected = server.WaitForConnectionAsync(timeout.Token);
        await peer.ConnectAsync(timeout.Token);
        await connected;
        Assert.True(ElevatedSession.IsPipeConnected(peer.SafePipeHandle));
        server.Dispose();
        Assert.False(ElevatedSession.IsPipeConnected(peer.SafePipeHandle));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WatchdogStopsAnActiveOperationOnParentDeathOrDisconnect(bool parentAlive, bool connected)
    {
        bool stopped = false;
        var lifetime = new SessionLifetime(() => parentAlive, () => connected, () => stopped = true);
        lifetime.HandshakeComplete();
        lifetime.OperationState(true);
        lifetime.Check();
        Assert.True(stopped);
    }

    [Fact]
    public void WatchdogBoundsHandshakeAndActiveOperationButAllowsIdleSessions()
    {
        long clock = 0;
        int stops = 0;
        var lifetime = new SessionLifetime(() => true, () => true, () => stops++, () => clock);
        clock = 16000;
        lifetime.Check();
        Assert.Equal(1, stops);
        lifetime.HandshakeComplete();
        clock = long.MaxValue / 2;
        lifetime.Check();
        Assert.Equal(1, stops);
        lifetime.OperationState(true);
        clock += (long)ElevatedSession.OperationTimeout.TotalMilliseconds;
        lifetime.Check();
        Assert.Equal(2, stops);
        lifetime.OperationState(false);
        clock += 100000;
        lifetime.Check();
        Assert.Equal(2, stops);
    }

    [Fact]
    public async Task GrantAndPingNeverCallTheFileExecutor()
    {
        using var stream = new ScriptStream(Frames(SessionCommand.Hello, SessionCommand.Ping, SessionCommand.Close));
        await SessionProtocol.ServeAsync(stream, _ => throw new Exception("Must not move files"));
        Assert.Equal(Frames(SessionCommand.Hello, SessionCommand.Ping, SessionCommand.Close), stream.Output.ToArray());
    }

    [Fact]
    public async Task CommandsRunInOrderAndKeepPartialReports()
    {
        using var stream = new ScriptStream(Frames(SessionCommand.Hello, SessionCommand.Move,
            SessionCommand.Restore, SessionCommand.Close));
        var calls = new List<string>();
        var states = new List<bool>();
        var partial = new ElevatedOperationReport(false, new(2, [new("source", "target", "preserved")]), 3, "failed");
        await SessionProtocol.ServeAsync(stream, argument =>
        {
            calls.Add(argument);
            return partial;
        }, states.Add);
        Assert.Equal([ElevatedOperations.MoveShortcutsAside, ElevatedOperations.RestoreShortcuts], calls);
        Assert.Equal([true, false, true, false], states);
        stream.Output.Position = 0;
        Assert.Equal(SessionCommand.Hello, await SessionProtocol.ReadCommandAsync(stream.Output));
        for (int i = 0; i < 2; i++)
        {
            var report = await ElevatedReportChannel.ReadAsync(stream.Output, default);
            Assert.False(report.Succeeded);
            Assert.Equal(2, report.Result.MovedCount);
            Assert.Equal(3, report.RemainingCount);
            Assert.Equal("failed", report.FailedPath);
            Assert.Single(report.Result.Conflicts);
        }
        Assert.Equal(SessionCommand.Close, await SessionProtocol.ReadCommandAsync(stream.Output));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public async Task CommandLengthMustBeExactlyOne(int length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<IOException>(() => SessionProtocol.ReadCommandAsync(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(255)]
    public async Task UnknownCommandIsRejectedBeforeExecutor(byte command)
    {
        using var stream = new ScriptStream(Frames(SessionCommand.Hello).Concat(new byte[] { 1, 0, 0, 0, command }).ToArray());
        int calls = 0;
        await Assert.ThrowsAsync<IOException>(() => SessionProtocol.ServeAsync(stream, _ =>
        {
            calls++;
            return new(true, new(0, []));
        }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData((byte)SessionCommand.Move)]
    [InlineData((byte)SessionCommand.Restore)]
    [InlineData((byte)SessionCommand.Ping)]
    public async Task HelloIsRequiredBeforeAnyCommand(byte first)
    {
        using var stream = new ScriptStream(Frames((SessionCommand)first));
        await Assert.ThrowsAsync<IOException>(() => SessionProtocol.ServeAsync(stream,
            _ => throw new Exception("Must not run before handshake")));
        Assert.Equal(0, stream.Output.Length);
    }

    [Fact]
    public async Task DisconnectAfterGrantStopsWithoutExecutingAnything()
    {
        using var stream = new ScriptStream(Frames(SessionCommand.Hello));
        await Assert.ThrowsAsync<EndOfStreamException>(() => SessionProtocol.ServeAsync(stream,
            _ => throw new Exception("Must not run")));
    }

    [Fact]
    public async Task TruncatedCommandAndCancelledReadFailClosed()
    {
        using var stream = new MemoryStream([1, 0, 0, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => SessionProtocol.ReadCommandAsync(stream));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionProtocol.ReadCommandAsync(stream, cancelled.Token));
    }

    private static byte[] Frames(params SessionCommand[] commands) =>
        commands.SelectMany(command => new byte[] { 1, 0, 0, 0, (byte)command }).ToArray();

    private sealed class ScriptStream(byte[] input) : Stream
    {
        private readonly MemoryStream source = new(input);
        internal MemoryStream Output { get; } = new();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => source.ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => Output.WriteAsync(buffer, token);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { source.Dispose(); Output.Dispose(); } base.Dispose(disposing); }
    }
}
