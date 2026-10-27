using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Services;

internal enum HotkeyStatus { Disabled, Active, Unavailable }

internal sealed class GlobalHotkeyRegistration(
    Func<uint, uint, bool> register, Action unregister) : IDisposable
{
    private bool _registered;
    private bool _disposed;

    public HotkeyStatus Apply(QuickSwitchHotkey preference)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Release();
        (uint modifiers, uint key) = preference switch
        {
            QuickSwitchHotkey.ControlAltM => (0x4003u, 0x4Du),
            QuickSwitchHotkey.ControlAltD => (0x4003u, 0x44u),
            QuickSwitchHotkey.ControlShiftF10 => (0x4006u, 0x79u),
            _ => (0u, 0u),
        };
        if (key == 0) return HotkeyStatus.Disabled;
        _registered = register(modifiers, key);
        return _registered ? HotkeyStatus.Active : HotkeyStatus.Unavailable;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Release();
        _disposed = true;
    }

    private void Release()
    {
        if (!_registered) return;
        unregister();
        _registered = false;
    }
}
