using MeetingDesktop.App.Services;
using System.IO;

namespace MeetingDesktop.App.Tests;

public sealed class LatestRefreshTests
{
    [Fact]
    public void PresentationResultReturnsToTheDispatcherWithoutBlockingItsMessages() => StaTest.Run(() =>
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        try
        {
            using var release = new ManualResetEventSlim();
            bool applied = false;
            using var refresh = new LatestRefresh<int, int>(value =>
            {
                Assert.False(dispatcher.CheckAccess());
                if (!release.Wait(3000)) throw new TimeoutException();
                return value;
            }, value => { Assert.True(dispatcher.CheckAccess()); applied = value == 42; });
            var frame = new System.Windows.Threading.DispatcherFrame();
            Task running = refresh.Request(42);
            dispatcher.BeginInvoke(() => release.Set());
            _ = running.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Assert.True(applied);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    [Fact]
    public async Task SlowReadDoesNotBlockCallerAndPendingRequestsCollapseToTheLatest()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = new List<int>();
        var applied = new List<int>();
        using var refresh = new LatestRefresh<int, int>(value =>
        {
            lock (reads) reads.Add(value);
            if (value == 1) { started.Set(); if (!release.Wait(3000)) throw new TimeoutException(); }
            return value;
        }, applied.Add);
        Task first = refresh.Request(1);
        try
        {
            Assert.True(started.Wait(2000));
            Assert.False(first.IsCompleted);
            _ = refresh.Request(2);
            _ = refresh.Request(3);
        }
        finally { release.Set(); }
        await first;
        Assert.Equal(new[] { 1, 3 }, reads);
        Assert.Equal(new[] { 3 }, applied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspendOrDisposeRejectsAnInFlightResult(bool dispose)
    {
        using var release = new ManualResetEventSlim();
        int applies = 0;
        using var refresh = new LatestRefresh<int, int>(value => { release.Wait(2000); return value; }, _ => applies++);
        Task running = refresh.Request(1);
        if (dispose) refresh.Dispose(); else refresh.Invalidate();
        release.Set();
        await running;
        Assert.Equal(0, applies);
        if (!dispose) { await refresh.Request(2); Assert.Equal(1, applies); }
    }

    [Fact]
    public async Task FailedReadDoesNotPoisonTheNextRefresh()
    {
        var values = new List<int>();
        using var refresh = new LatestRefresh<int, int>(value => value == 1 ? throw new IOException() : value, values.Add);
        await refresh.Request(1);
        await refresh.Request(2);
        Assert.Equal(new[] { 2 }, values);
    }
}
