using System.IO.Pipes;
using System.Text;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests;

public class ElevationReportBridgeTests
{
    [Fact]
    public void SameAccountAuthenticationUsesTheProcessUser()
    {
        Assert.True(ElevatedReportChannel.IsSameProcessUser(Environment.ProcessId));
        Assert.False(ElevatedReportChannel.IsSameProcessUser(int.MaxValue));
    }

    private sealed class LegacyBroker : IElevationBroker
    {
        public ElevationOutcome Run(string argument) => ElevationOutcome.Succeeded;
    }

    [Fact]
    public void LegacyBrokerHasNoReport()
    {
        IElevationBroker broker = new LegacyBroker();
        Assert.Null(broker.LastReport);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--elevated-move-shortcuts-aside C:\\target")]
    public void RelaunchRejectsUnknownOperations(string argument) =>
        Assert.Throws<ArgumentException>(() => ElevatedOperations.BuildRelaunchInfo(argument));

    [Fact]
    public void LegacyRelaunchKeepsSingleTokenAndRunas()
    {
        var info = ElevatedOperations.BuildRelaunchInfo(ElevatedOperations.RestoreShortcuts);
        Assert.Equal(ElevatedOperations.RestoreShortcuts, info.Arguments);
        Assert.True(info.UseShellExecute);
        Assert.Equal("runas", info.Verb);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1048577)]
    public async Task RejectsUnboundedLengthsBeforeReadingPayload(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        await Assert.ThrowsAsync<IOException>(() => ElevatedReportChannel.ReadAsync(stream, default));
    }

    [Fact]
    public async Task RejectsTruncatedPayload()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(100));
        await Assert.ThrowsAsync<EndOfStreamException>(() => ElevatedReportChannel.ReadAsync(stream, default));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Succeeded\":true,\"Result\":{\"MovedCount\":-1,\"Conflicts\":[]}}")]
    [InlineData("{\"Succeeded\":true,\"Result\":{\"MovedCount\":0,\"Conflicts\":[null]}}")]
    public async Task RejectsInvalidReports(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(bytes.Length));
        stream.Write(bytes);
        stream.Position = 0;
        await Assert.ThrowsAsync<IOException>(() => ElevatedReportChannel.ReadAsync(stream, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticatedPipePreservesConflictsAndPartialProgress(bool succeeded)
    {
        using var channel = new ElevatedReportChannel();
        var info = channel.BuildRelaunchInfo(ElevatedOperations.RestoreShortcuts);
        string[] args = info.Arguments.Split(' ');
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var receive = channel.ReceiveAsync(Environment.ProcessId, timeout.Token);
        using var client = new NamedPipeClientStream(".", ElevatedReportChannel.Prefix + args[2],
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token);
        Assert.True(ElevatedReportChannel.GetNamedPipeServerProcessId(client.SafePipeHandle, out uint peer));
        Assert.Equal((uint)Environment.ProcessId, peer);
        byte[] ack = new byte[1];
        await client.ReadExactlyAsync(ack, timeout.Token);
        Assert.Equal(1, ack[0]);
        var report = new ElevatedOperationReport(succeeded,
            new(2, [new("source.lnk", "requested.lnk", "preserved.lnk")]), 1, "failed.lnk");
        await ElevatedReportChannel.WriteAsync(client, report, timeout.Token);
        var actual = await receive;
        Assert.Equal(succeeded, actual.Succeeded);
        Assert.Equal(2, actual.Result.MovedCount);
        Assert.Equal(report.Result.Conflicts, actual.Result.Conflicts);
        Assert.Equal(1, actual.RemainingCount);
        Assert.Equal("failed.lnk", actual.FailedPath);
    }

    [Fact]
    public async Task RejectsUnexpectedClientBeforeSendingAcknowledgment()
    {
        using var channel = new ElevatedReportChannel();
        string[] args = channel.BuildRelaunchInfo(ElevatedOperations.RestoreShortcuts).Arguments.Split(' ');
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var receive = channel.ReceiveAsync(int.MaxValue, timeout.Token);
        using var client = new NamedPipeClientStream(".", ElevatedReportChannel.Prefix + args[2],
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => receive);
    }

    [Fact]
    public async Task WaitingForHelperHonorsCancellation()
    {
        using var channel = new ElevatedReportChannel();
        using var timeout = new CancellationTokenSource();
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ReceiveAsync(Environment.ProcessId, timeout.Token));
    }

    [Fact]
    public void RejectsCallerSuppliedReportPathWithoutRunningOperation()
    {
        Assert.False(ElevatedOperations.TryRun([ElevatedOperations.RestoreShortcuts,
            "--report-pipe", @"C:\\report.json", "1"], out int code));
        Assert.Equal(0, code);
    }
    [Fact]
    public async Task HelperRejectsWrongServerProcessBeforeDesktopOperation()
    {
        string id = Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(ElevatedReportChannel.Prefix + id,
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var connected = server.WaitForConnectionAsync(timeout.Token);
        var helper = Task.Run(() =>
        {
            bool handled = ElevatedOperations.TryRun([ElevatedOperations.RestoreShortcuts,
                "--report-pipe", id, int.MaxValue.ToString()], out int code);
            return (handled, code);
        });
        await connected;
        var result = await helper.WaitAsync(timeout.Token);
        Assert.True(result.handled);
        Assert.Equal(1, result.code);
    }

    [Fact]
    public async Task OversizedReportWritesNothing()
    {
        using var stream = new MemoryStream();
        var report = new ElevatedOperationReport(false, new(0, []), 1,
            new string('x', ElevatedReportChannel.MaximumBytes));
        await Assert.ThrowsAsync<IOException>(() => ElevatedReportChannel.WriteAsync(stream, report, default));
        Assert.Equal(0, stream.Length);
    }
}

