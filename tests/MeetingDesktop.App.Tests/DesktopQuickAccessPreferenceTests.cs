using MeetingDesktop.App.Services;
using MeetingDesktop.Core.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MeetingDesktop.App.Tests;

public sealed class DesktopQuickAccessPreferenceTests
{
    [Fact]
    public void TrayPublishesPreferenceChangesForTheSettingsOwner() =>
        Assert.NotNull(typeof(DesktopQuickAccess).GetEvent("TrayDoubleClickActionChanged"));

    [Fact]
    public void TrayExposesNonactivatingNotification() =>
        Assert.NotNull(typeof(DesktopQuickAccess).GetMethod("ShowNotification", [typeof(string)]));

    [Theory]
    [InlineData(TrayDoubleClickAction.OpenWindow, 1, 1)]
    [InlineData(TrayDoubleClickAction.SwitchDesktop, 0, 2)]
    [InlineData((TrayDoubleClickAction)99, 1, 1)]
    public void AppliedPreferenceDispatchesDoubleClickWhileHotkeyAlwaysSwitches(TrayDoubleClickAction action, int opens, int switches) => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        int actualOpens = 0, actualSwitches = 0, changes = 0;
        fixture.Access.OpenRequested += (_, _) => actualOpens++;
        fixture.Access.SwitchRequested += (_, _) => actualSwitches++;
        fixture.Access.TrayDoubleClickActionChanged += _ => changes++;
        fixture.Access.Apply(Settings(action));
        fixture.Message(0x8000 + 73, 0, (1 << 16) | 0x0203);
        fixture.Message(0x0312, 0x4D44, 0);
        fixture.Message(0x0312, 999, 0);
        fixture.Message(0x8000 + 73, 0, 0x0202);
        Pump();
        Assert.Equal(opens, actualOpens);
        Assert.Equal(switches, actualSwitches);
        Assert.Equal(0, changes);
    });

    [Fact]
    public void DefaultDoubleClickOpensAndQueuedRequestsAreDroppedAfterDisposal() => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        int opens = 0, switches = 0;
        fixture.Access.OpenRequested += (_, _) => opens++;
        fixture.Access.SwitchRequested += (_, _) => switches++;
        fixture.Message(0x8000 + 73, 0, 0x0203);
        Pump();
        Assert.Equal(1, opens);
        fixture.Message(0x8000 + 73, 0, 0x0203);
        fixture.Message(0x0312, 0x4D44, 0);
        fixture.Access.Dispose();
        Pump();
        Assert.Equal(1, opens);
        Assert.Equal(0, switches);
    });

    [Fact]
    public void MenuChoicesAreExclusiveAndEmitOnlyUserPreferenceChanges() => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        var changes = new List<TrayDoubleClickAction>();
        fixture.Access.TrayDoubleClickActionChanged += changes.Add;
        var menu = fixture.Access.CreateMenu();
        var choices = menu.Items.OfType<MenuItem>().Where(item => item.IsCheckable).ToArray();
        Assert.Equal(2, choices.Length);
        Assert.True(choices[0].IsChecked);
        Assert.False(choices[1].IsChecked);
        choices[1].IsChecked = true;
        choices[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(choices[0].IsChecked);
        Assert.True(choices[1].IsChecked);
        choices[1].IsChecked = false; // WPF toggles a checked option before raising Click.
        choices[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(choices[1].IsChecked);
        Assert.Equal(new[] { TrayDoubleClickAction.SwitchDesktop }, changes);
        choices[0].IsChecked = true;
        choices[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(choices[0].IsChecked);
        Assert.False(choices[1].IsChecked);
        Assert.Equal(new[] { TrayDoubleClickAction.SwitchDesktop, TrayDoubleClickAction.OpenWindow }, changes);
        fixture.Access.Apply(Settings(TrayDoubleClickAction.OpenWindow));
        Assert.True(choices[0].IsChecked);
        Assert.False(choices[1].IsChecked);
        Assert.Equal(2, changes.Count);
        choices[0].IsChecked = false;
        choices[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(choices[0].IsChecked);
        Assert.Equal(2, changes.Count);
        Assert.True(fixture.Access.CreateMenu().Items.OfType<MenuItem>().First(item => item.IsCheckable).IsChecked);
    });

    [Fact]
    public void BalloonUsesOnlyNativeInfoUpdateAndDoesNotReplayOnTooltipUpdate() => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Calls.Clear();
        fixture.Access.ShowNotification("Saved settings");
        var call = Assert.Single(fixture.Calls);
        Assert.Equal(1u, call.Operation);
        Assert.Equal(0x10u, call.Data.Flags);
        Assert.Equal("Saved settings", call.Data.Info);
        Assert.False(string.IsNullOrEmpty(call.Data.InfoTitle));
        Assert.Equal(0x11u, call.Data.InfoFlags); // NIIF_INFO | NIIF_NOSOUND.
        fixture.Access.UpdateTooltip();
        Assert.Equal(0u, fixture.Calls[^1].Data.Flags & 0x10);
        Assert.Equal("", fixture.Calls[^1].Data.Info);
    });

    [Fact]
    public void BalloonBoundsUnicodeAndIgnoresEmptyOrDisposedRequests() => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Calls.Clear();
        fixture.Access.ShowNotification("");
        Assert.Empty(fixture.Calls);
        fixture.Access.ShowNotification(new string('x', 254) + "😀tail");
        Assert.Equal(new string('x', 254), Assert.Single(fixture.Calls).Data.Info);
        fixture.Access.Dispose();
        fixture.Calls.Clear();
        fixture.Access.ShowNotification("Too late");
        Assert.Empty(fixture.Calls);
    });

    private static AppSettings Settings(TrayDoubleClickAction action) => new()
    {
        TrayDoubleClickAction = action,
        QuickSwitchHotkey = QuickSwitchHotkey.Disabled,
        DesktopMouseGesture = DesktopMouseGesture.Disabled,
    };

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Window _window = new(); // Never shown; no HWND or native registration.
        public readonly List<(uint Operation, DesktopQuickAccess.NotifyIconData Data)> Calls = [];
        public DesktopQuickAccess Access { get; }

        public Fixture() => Access = new(_window,
            new GlobalHotkeyRegistration((_, _) => true, () => { }),
            (uint operation, ref DesktopQuickAccess.NotifyIconData data) =>
            {
                Calls.Add((operation, data));
                return true;
            });

        public void Message(int message, nint wparam, nint lparam)
        {
            bool handled = false;
            Access.OnMessage(0, message, wparam, lparam, ref handled);
        }

        public void Dispose() { Access.Dispose(); _window.Close(); }
    }
}
