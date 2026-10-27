using System.Runtime.InteropServices;
using System.Text;
using MeetingDesktop.Core;

namespace MeetingDesktop.App.Services;

/// <summary>A temporary style on the icon container; never touches the wallpaper host or window visibility.</summary>
internal sealed class NativeIconOpacity : IIconOpacityLayer
{
    private const int Layered = 0x80000;
    private readonly nint _window;
    private readonly uint _process;
    private bool _wasLayered;
    private uint _color, _flags = 2;
    private byte _opacity = 255;
    private const string OwnerProperty = "MeetingDesktop.IconFade.Owner";
    private readonly nint _owner = (nint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private bool _disposed;
    private bool _usable = true;

    private NativeIconOpacity(nint window)
    {
        _window = window;
        GetWindowThreadProcessId(window, out _process);
    }

    internal static NativeIconOpacity? TryAcquireDesktop()
    {
        nint window = DesktopGridView.FindView(retries: 1);
        nint container = GetParent(window);
        // FindView has a legacy Explorer-window fallback: do not use it for presentation.
        if (Class(window) != "SysListView32" || Class(container) != "SHELLDLL_DefView" ||
            Class(GetAncestor(window, 2)) is not ("WorkerW" or "Progman") || !IsWindowVisible(window)) return null;
        bool iconsOnly = true;
        EnumChildWindows(container, (child, _) =>
        {
            iconsOnly = Class(child) is "SysListView32" or "SysHeader32";
            return iconsOnly;
        }, 0);
        // Layering the ListView itself turns its transparent background into black.
        // Its DefView container preserves that transparency. Unknown wallpaper or
        // organizer children are excluded instead of fading their content too.
        return iconsOnly ? TryAcquire(container) : null;
    }

    internal static NativeIconOpacity? TryAcquire(nint window)
    {
        if (window == 0 || !IsWindow(window)) return null;
        var lease = new NativeIconOpacity(window);
        if (!lease.TryClaim()) return null;
        int style = GetWindowLong(window, -20);
        lease._wasLayered = (style & Layered) != 0;
        // Explorer can already use a color-keyed DefView. Preserve that key and
        // opacity. A per-pixel UpdateLayeredWindow surface has no readable LWA
        // attributes and must not be converted to a different composition mode.
        if (!lease.OwnsWindow() || (lease._wasLayered &&
            !GetLayeredWindowAttributes(window, out lease._color, out lease._opacity, out lease._flags)))
        {
            if (GetProp(window, OwnerProperty) == lease._owner) RemoveProp(window, OwnerProperty);
            return null;
        }
        Marshal.SetLastPInvokeError(0);
        int previous = SetWindowLong(window, -20, style | Layered);
        if ((previous == 0 && Marshal.GetLastPInvokeError() != 0) || !lease.SetAlpha(255))
        {
            lease._usable = false;
            return lease.Restore() ? null : lease;
        }
        return lease;
    }

    public bool SetAlpha(byte alpha)
    {
        if (!_usable || !OwnsWindow()) return false;
        int style = GetWindowLong(_window, -20);
        if ((style & Layered) == 0)
        {
            Marshal.SetLastPInvokeError(0);
            if (SetWindowLong(_window, -20, style | Layered) == 0 && Marshal.GetLastPInvokeError() != 0)
                return false;
        }
        byte target = (byte)(alpha * ((_flags & 2) != 0 ? _opacity : 255) / 255);
        uint options = _flags | 2;
        if (GetLayeredWindowAttributes(_window, out uint color, out byte current, out uint flags) &&
            color == _color && current == target && flags == options) return true;
        return SetLayeredWindowAttributes(_window, _color, target, options);
    }

    private bool OwnsWindow() => !_disposed && IsWindow(_window) &&
        GetWindowThreadProcessId(_window, out uint process) != 0 && process == _process &&
        GetProp(_window, OwnerProperty) == _owner;

    private bool TryClaim()
    {
        // A timed-out helper may still be restoring. Never snapshot its partial
        // opacity as the next switch's baseline, including across helper processes.
        using var gate = new Mutex(false, @"Local\MeetingDesktop.IconFade." + _window.ToString("X"));
        bool entered;
        try { entered = gate.WaitOne(0); }
        catch (AbandonedMutexException) { entered = true; }
        if (!entered) return false;
        try { return GetProp(_window, OwnerProperty) == 0 && SetProp(_window, OwnerProperty, _owner); }
        finally { gate.ReleaseMutex(); }
    }

    public bool Restore()
    {
        if (OwnsWindow())
        {
            if (_wasLayered)
            {
                if ((GetWindowLong(_window, -20) & Layered) == 0)
                    SetWindowLong(_window, -20, GetWindowLong(_window, -20) | Layered);
                if (!SetLayeredWindowAttributes(_window, _color, _opacity, _flags) ||
                    !GetLayeredWindowAttributes(_window, out uint color, out byte opacity, out uint flags) ||
                    color != _color || ((_flags & 2) != 0 && opacity != _opacity) || flags != _flags) return false;
            }
            else if ((GetWindowLong(_window, -20) & Layered) != 0)
            {
                SetLayeredWindowAttributes(_window, 0, 255, 2);
                // Preserve unrelated style changes made while the switch was in progress.
                SetWindowLong(_window, -20, GetWindowLong(_window, -20) & ~Layered);
                if ((GetWindowLong(_window, -20) & Layered) != 0) return false;
            }
            if (!RedrawWindow(_window, 0, 0, 0x0001 | 0x0004 | 0x0080 | 0x0400)) return false;
            RemoveProp(_window, OwnerProperty);
            if (GetProp(_window, OwnerProperty) == _owner) return false;
        }
        _disposed = true;
        return true;
    }

    public void Dispose() => Restore();

    private static string Class(nint window)
    {
        var text = new StringBuilder(128);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    private delegate bool EnumWindow(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint color, out byte alpha, out uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(nint window, string key, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string key);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(nint window, nint rect, nint region, uint flags);
}
