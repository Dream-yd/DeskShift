using MeetingDesktop.App.Services;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Tests;

public sealed class GlobalHotkeyRegistrationTests
{
    [Theory]
    [InlineData(QuickSwitchHotkey.ControlAltM, 0x4003u, 0x4Du)]
    [InlineData(QuickSwitchHotkey.ControlAltD, 0x4003u, 0x44u)]
    [InlineData(QuickSwitchHotkey.ControlShiftF10, 0x4006u, 0x79u)]
    public void RegistersTheSelectedCombinationWithRepeatSuppression(QuickSwitchHotkey choice, uint modifiers, uint key)
    {
        (uint, uint)? actual = null;
        using var hotkey = new GlobalHotkeyRegistration((m, k) => { actual = (m, k); return true; }, () => { });
        Assert.Equal(HotkeyStatus.Active, hotkey.Apply(choice));
        Assert.Equal((modifiers, key), actual);
    }

    [Fact]
    public void ConflictDoesNotClaimAWorkingShortcutAndCanBeRetried()
    {
        bool taken = true;
        int removed = 0;
        var hotkey = new GlobalHotkeyRegistration((_, _) => !taken, () => removed++);
        Assert.Equal(HotkeyStatus.Unavailable, hotkey.Apply(QuickSwitchHotkey.ControlAltM));
        Assert.Equal(0, removed);
        taken = false;
        Assert.Equal(HotkeyStatus.Active, hotkey.Apply(QuickSwitchHotkey.ControlAltM));
        hotkey.Dispose();
        Assert.Equal(1, removed);
    }

    [Fact]
    public void ChangingOrDisablingUnregistersExactlyOnce()
    {
        var calls = new List<string>();
        var hotkey = new GlobalHotkeyRegistration((_, key) => { calls.Add("add" + key); return true; }, () => calls.Add("remove"));
        hotkey.Apply(QuickSwitchHotkey.ControlAltM);
        hotkey.Apply(QuickSwitchHotkey.ControlAltD);
        hotkey.Apply(QuickSwitchHotkey.Disabled);
        hotkey.Dispose();
        hotkey.Dispose();
        Assert.Equal(new[] { "add77", "remove", "add68", "remove" }, calls);
    }

    [Fact]
    public void InvalidPreferenceDoesNotCaptureAnyKeys()
    {
        using var hotkey = new GlobalHotkeyRegistration((_, _) => throw new InvalidOperationException(), () => { });
        Assert.Equal(HotkeyStatus.Disabled, hotkey.Apply((QuickSwitchHotkey)999));
    }
}
