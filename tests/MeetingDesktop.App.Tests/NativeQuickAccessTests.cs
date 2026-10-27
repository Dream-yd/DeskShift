using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MeetingDesktop.App.Services;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Tests;

public sealed class NativeQuickAccessTests
{
    [Fact]
    public void NativeHotkeyAndTrayMessagesReachTheSameSwitchEventAndDisposeCleanly() => StaTest.Run(() =>
    {
        var window = new Window();
        try
        {
            nint handle = new WindowInteropHelper(window).EnsureHandle();
            int switches = 0;
            using (var access = new DesktopQuickAccess(window))
            {
                access.Apply(new AppSettings { QuickSwitchHotkey = QuickSwitchHotkey.Disabled, DesktopMouseGesture = DesktopMouseGesture.Disabled,
                    TrayDoubleClickAction = TrayDoubleClickAction.SwitchDesktop });
                access.SwitchRequested += (_, _) => switches++;
                SendMessage(handle, 0x0312, 0x4D44, 0);
                SendMessage(handle, 0x8000 + 73, 0, (1 << 16) | 0x0203);
                Pump();
                Assert.Equal(2, switches);
                // An ordinary tray single click and unrelated hotkey must not switch.
                SendMessage(handle, 0x8000 + 73, 0, (1 << 16) | 0x0202);
                SendMessage(handle, 0x0312, 999, 0);
                Pump();
                Assert.Equal(2, switches);
            }
            SendMessage(handle, 0x0312, 0x4D44, 0);
            Pump();
            Assert.Equal(2, switches);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MouseHitTestingRejectsOrdinaryApplicationWindows() => StaTest.Run(() =>
    {
        var window = new Window { Width = 200, Height = 150, Left = 400, Top = 250, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show();
            Pump();
            Point point = window.PointToScreen(new Point(50, 50));
            Assert.Equal((nint)0, DesktopBlankHitTest.DesktopSurfaceAt((int)point.X, (int)point.Y));
            Assert.Equal((nint)0, DesktopBlankHitTest.DesktopViewAt((int)point.X, (int)point.Y));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PendingNativeRequestsAreDroppedAfterDisposal() => StaTest.Run(() =>
    {
        var window = new Window();
        try
        {
            nint handle = new WindowInteropHelper(window).EnsureHandle();
            int switches = 0;
            var access = new DesktopQuickAccess(window);
            access.SwitchRequested += (_, _) => switches++;
            SendMessage(handle, 0x0312, 0x4D44, 0);
            access.Dispose();
            Pump();
            Assert.Equal(0, switches);
        }
        finally { window.Close(); }
    });

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
}
