using System.Windows;
using MeetingDesktop.App.Services;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Tests;

public sealed class SwitchFeedbackTests
{
    [Theory]
    [InlineData(DesktopSwitchMode.Live, true, false)]
    [InlineData(DesktopSwitchMode.RestartExplorer, true, true)]
    [InlineData(DesktopSwitchMode.RestartExplorer, false, false)]
    public void LiveSwitchNeverCoversTheAnimatedDesktop(DesktopSwitchMode mode, bool smooth, bool expected)
    {
        Assert.Equal(expected, SwitchFeedback.ShouldCoverDesktop(mode, smooth));
        Assert.Equal(mode != DesktopSwitchMode.Live, SwitchFeedback.ShouldCapturePreview(mode, true));
        Assert.False(SwitchFeedback.ShouldCapturePreview(mode, false));
    }

    [Fact]
    public void NotificationIsSmallNonActivatingAndHasBoundedLifetime() => StaTest.Run(() =>
    {
        using var notification = new SwitchNotification(() => "Switching", animate: false);
        Assert.False(notification.Window.ShowActivated);
        Assert.False(notification.Window.ShowInTaskbar);
        Assert.False(notification.Window.Focusable);
        Assert.False(notification.Window.IsHitTestVisible);
        Assert.InRange(notification.Window.Width, 200, 420);
        Assert.InRange(notification.Window.Height, 40, 140);
        Assert.Equal(WindowStyle.None, notification.Window.WindowStyle);
        Assert.True(notification.Window.AllowsTransparency);
        notification.Complete(true);
        notification.Dispose();
        notification.Dispose();
        Assert.True(notification.IsClosed);
    });

    [Fact]
    public void DisabledNotificationAndLiveFadeDoNotCreateAnyWindows() => StaTest.Run(() =>
    {
        var feedback = new SwitchFeedback(new AppSettings { ShowSwitchNotification = false },
            DesktopSwitchMode.Live, () => throw new InvalidOperationException("Disabled UI must not poll status."));
        feedback.ShowAsync(CancellationToken.None).GetAwaiter().GetResult();
        feedback.DisposeAsync().GetAwaiter().GetResult();
    });
}
