using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MeetingDesktop.App.Services;

namespace MeetingDesktop.App.Tests;

public sealed class SwitchNotificationGeometryTests
{
    [Fact]
    public void NotificationUsesCompactCapsuleDimensions() => StaTest.Run(() =>
    {
        using var notification = new SwitchNotification(() => "Switching", animate: false);
        Assert.Equal(300, notification.Window.Width);
        Assert.Equal(64, notification.Window.Height);
    });

    [Theory]
    [InlineData(0, 0, 1920, 1040, 96, 810, 821, 300, 64)]
    [InlineData(-1920, -200, 1920, 1040, 144, -1185, 605, 450, 96)]
    [InlineData(1920, 40, 2560, 1400, 192, 2900, 1124, 600, 128)]
    [InlineData(48, 0, 1872, 1080, 120, 796, 846, 375, 80)]
    [InlineData(0, 0, 1920, 1040, 0, 810, 821, 300, 64)]
    [InlineData(0, 0, 800, 200, 96, 250, 112, 300, 64)]
    [InlineData(0, 0, 1200, 300, 144, 375, 168, 450, 96)]
    [InlineData(0, 0, 800, 112, 96, 250, 24, 300, 64)]
    [InlineData(0, 0, 800, 100, 96, 250, 18, 300, 64)]
    [InlineData(-500, -500, 200, 60, 96, -500, -500, 200, 60)]
    public void BoundsTarget82PercentOfWorkHeightAndClampToAvailableMargins(
        int x, int y, int width, int height, uint dpi,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        var bounds = SwitchNotification.CalculateBounds(new Int32Rect(x, y, width, height), dpi);
        Assert.Equal(new Int32Rect(expectedX, expectedY, expectedWidth, expectedHeight), bounds);
    }

    [Fact]
    public void NativeCapsuleRegionTracksActualWindowSize() => StaTest.Run(() =>
    {
        using var notification = new SwitchNotification(() => "Geometry test", animate: false);
        // Supply only the fallback resource, without creating an App or changing user settings.
        notification.Window.Resources["Brush.Window"] = Brushes.Black;
        notification.Show();
        nint handle = new WindowInteropHelper(notification.Window).Handle;
        AssertCapsule(handle);
        foreach (var size in new[] { (450, 96), (600, 128), (300, 64) })
        {
            Assert.True(SetWindowPos(handle, 0, 0, 0, size.Item1, size.Item2,
                0x0002 | 0x0004 | 0x0010)); // NOMOVE | NOZORDER | NOACTIVATE
            AssertCapsule(handle);
        }
    });

    private static void AssertCapsule(nint handle)
    {
        Assert.True(GetWindowRect(handle, out var bounds));
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        nint region = CreateRectRgn(0, 0, 0, 0);
        Assert.NotEqual(0, region);
        try
        {
            Assert.Equal(3, GetWindowRgn(handle, region)); // COMPLEXREGION, not a rectangular HWND.
            Assert.True(PtInRegion(region, width / 2, height / 2));
            Assert.True(PtInRegion(region, width / 2, 1));
            Assert.True(PtInRegion(region, 1, height / 2));
            Assert.True(PtInRegion(region, width - 2, height / 2));
            Assert.False(PtInRegion(region, 1, 1));
            Assert.False(PtInRegion(region, width - 2, 1));
            Assert.False(PtInRegion(region, width - 2, height - 2));
            Assert.False(PtInRegion(region, 1, height - 2));
        }
        finally { _ = DeleteObject(region); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
