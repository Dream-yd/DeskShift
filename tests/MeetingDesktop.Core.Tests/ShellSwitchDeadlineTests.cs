namespace MeetingDesktop.Core.Tests;

public sealed class ShellSwitchDeadlineTests
{
    [Fact]
    public void IndependentVerifierUsesTheTimeRemainingAfterTheSwitch()
    {
        bool verified = false, warmed = false;
        var clock = new ManualClock();
        Assert.Throws<DesktopViewVerificationException>(() => DesktopShellWorker.SwitchTo(@"C:\Target",
            (_, token) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(250));
                token.ThrowIfCancellationRequested();
                return new() { Icons = [] };
            },
            (_, token) =>
            {
                verified = true;
                clock.Advance(TimeSpan.FromMilliseconds(250));
                token.ThrowIfCancellationRequested();
                return new() { Icons = [], TargetVerified = true };
            },
            () => warmed = true, operationTimeout: TimeSpan.FromMilliseconds(400), timeProvider: clock));
        Assert.True(verified);
        Assert.True(warmed);
    }

    [Fact]
    public void LateMainReplyCannotStartVerificationWhenCancellationTimerHasNotFired()
    {
        var clock = new ManualClock();
        bool verified = false;
        Assert.Throws<DesktopViewVerificationException>(() => DesktopShellWorker.SwitchTo(@"C:\Target",
            (_, token) => { clock.Advance(TimeSpan.FromSeconds(21)); Assert.False(token.IsCancellationRequested); return new() { Icons = [] }; },
            (_, _) => { verified = true; return new() { Icons = [], TargetVerified = true }; },
            () => { }, timeProvider: clock));
        Assert.False(verified);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    [Fact]
    public void CallerCancellationIsNotReportedAsATimeout()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool invoked = false;
        Assert.ThrowsAny<OperationCanceledException>(() => DesktopShellWorker.SwitchTo(@"C:\Target",
            (_, _) => { invoked = true; return new() { Icons = [] }; },
            (_, _) => new() { Icons = [], TargetVerified = true }, () => { }, cancellation.Token));
        Assert.False(invoked);
    }

    [Fact]
    public void SuccessfulSwitchStillRequiresAnIndependentMatchingSnapshot()
    {
        bool verified = false;
        var result = DesktopShellWorker.SwitchTo(@"C:\Target", (_, _) => new() { Icons = [] },
            (_, _) => { verified = true; return new() { Icons = [], TargetVerified = true }; }, () => { });
        Assert.True(verified);
        Assert.Empty(result);
    }
}
