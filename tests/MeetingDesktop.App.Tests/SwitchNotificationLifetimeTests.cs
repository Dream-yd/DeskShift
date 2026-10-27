using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MeetingDesktop.App.Localization;
using MeetingDesktop.App.Services;

namespace MeetingDesktop.App.Tests;

public sealed class SwitchNotificationLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShowingLatestClosesPreviousEvenWhileItsCompletionIsVisible(bool completePrevious) => StaTest.Run(() =>
    {
        using var previous = Create(() => "Previous");
        using var latest = Create(() => "Latest");
        previous.Show();
        if (completePrevious) previous.Complete(true);

        latest.Show();

        Assert.True(previous.IsClosed);
        Assert.False(previous.Window.IsVisible);
        Assert.True(latest.Window.IsVisible);
        previous.Complete(true);
        previous.Dispose();
        previous.Show();
        Assert.False(latest.IsClosed);
        Assert.True(latest.Window.IsVisible);

        // Stale cleanup must not clear the owner used by the following switch.
        using var next = Create(() => "Next");
        next.Show();
        Assert.True(latest.IsClosed);
        Assert.True(next.Window.IsVisible);
    });

    [Fact]
    public void ConstructingOrDisposingUnshownNotificationDoesNotReplaceVisibleOne() => StaTest.Run(() =>
    {
        using var visible = Create(() => "Visible");
        visible.Show();
        using var unused = Create(() => "Unused");
        unused.Complete(true);
        unused.Dispose();
        visible.Show();
        Assert.True(visible.Window.IsVisible);
        Assert.False(visible.IsClosed);
        using var latest = Create(() => "Latest");
        latest.Show();
        Assert.True(visible.IsClosed);
    });

    [Fact]
    public void ClosedWindowReleasesOwnershipForFollowingNotifications() => StaTest.Run(() =>
    {
        using var previous = Create(() => "Previous");
        previous.Show();
        previous.Window.Close();
        Assert.True(previous.IsClosed);
        using var latest = Create(() => "Latest");
        latest.Show();
        previous.Dispose();
        using var next = Create(() => "Next");
        next.Show();
        Assert.True(latest.IsClosed);
        Assert.True(next.Window.IsVisible);
    });

    [Fact]
    public void ThrowingStatusCannotEscapeTimerAndPollingRecovers() => StaTest.Run(() =>
    {
        int reads = 0;
        using var notification = Create(() => ++reads switch
        {
            1 => "Last known status",
            2 => throw new InvalidOperationException("Optional status unavailable"),
            _ => "Recovered status"
        });
        var errors = new List<Exception>();
        var dispatcher = Dispatcher.CurrentDispatcher;
        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            errors.Add(e.Exception);
            e.Handled = true;
        }
        dispatcher.UnhandledException += OnUnhandled;
        try
        {
            notification.Show();
            PumpUntil(() => reads >= 2);
            Assert.Equal("Last known status", Text(notification, 1).Text);
            Assert.Empty(errors);
            PumpUntil(() => reads >= 3);
            Assert.Equal("Recovered status", Text(notification, 1).Text);
            Assert.Empty(errors);
        }
        finally { dispatcher.UnhandledException -= OnUnhandled; }
    });

    [Fact]
    public void ThrowingStatusCannotPreventUnshownCompletionFromClosing() => StaTest.Run(() =>
    {
        using var notification = Create(() => throw new InvalidOperationException("Optional status unavailable"));
        Assert.Null(Record.Exception(() => notification.Complete(true)));
        Assert.True(notification.IsClosed);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingStatusCannotPreventCompletionOrAutomaticClose(bool animate) => StaTest.Run(() =>
    {
        int reads = 0;
        using var notification = Create(() =>
        {
            reads++;
            throw new InvalidOperationException("Optional status unavailable");
        }, animate);
        notification.Show();
        Assert.Null(Record.Exception(() => notification.Complete(true)));
        Assert.Equal(LocalizationManager.Instance["SwitchNotificationDone"], Text(notification, 0).Text);
        var body = (Grid)((Border)notification.Window.Content).Child;
        Assert.Equal("\u2713", ((TextBlock)body.Children[0]).Text);
        notification.Complete(false);
        Assert.Equal(LocalizationManager.Instance["SwitchNotificationDone"], Text(notification, 0).Text);
        PumpUntil(() => notification.IsClosed);
        Assert.False(notification.Window.IsVisible);
        Assert.Equal(1, reads);
    });

    private static SwitchNotification Create(Func<string> status, bool animate = false)
    {
        var notification = new SwitchNotification(status, animate);
        notification.Window.Resources["Brush.Window"] = Brushes.Black;
        return notification;
    }

    private static TextBlock Text(SwitchNotification notification, int index)
    {
        var body = (Grid)((Border)notification.Window.Content).Child;
        return (TextBlock)((StackPanel)body.Children[1]).Children[index];
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        poll.Tick += (_, _) =>
        {
            if (condition() || elapsed.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false;
        };
        poll.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { poll.Stop(); }
        Assert.True(condition(), "Notification did not reach the expected state within five seconds.");
    }
}
