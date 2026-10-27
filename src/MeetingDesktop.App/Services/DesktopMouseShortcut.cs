using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Services;

/// <summary>Observes mouse clicks; every event is passed through to Windows unchanged.</summary>
internal sealed class DesktopMouseShortcut : IDisposable
{
    private static readonly int[] ModifierKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C];
    private readonly Dispatcher _dispatcher;
    private readonly HookProc _callback;
    private readonly DesktopDoubleClickGesture _gesture = new(GetDoubleClickTime(),
        GetSystemMetrics(36), GetSystemMetrics(37), GetSystemMetrics(68), GetSystemMetrics(69));
    private readonly BlockingCollection<(DesktopDoubleClick Click, DesktopMouseGesture Mode, int Generation, long Queued)> _work = new(1);
    private nint _hook;
    private int _generation;
    private bool _disposed;
    private long _lastTrigger;
    private DesktopMouseGesture _mode;

    public DesktopMouseShortcut(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = OnMouse;
        var thread = new Thread(CheckCandidates) { IsBackground = true, Name = "Desktop blank-area hit test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public event EventHandler? Triggered;

    public bool Apply(DesktopMouseGesture mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _generation++;
        _gesture.Reset();
        _mode = Enum.IsDefined(mode) ? mode : DesktopMouseGesture.Disabled;
        if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
        if (_mode != DesktopMouseGesture.Disabled) _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _callback, GetModuleHandle(null), 0);
        return _mode == DesktopMouseGesture.Disabled || _hook != 0;
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0)
            {
                var info = Marshal.PtrToStructure<MouseHookData>(data);
                bool middle = _mode == DesktopMouseGesture.MiddleDoubleClick;
                nint down = middle ? 0x0207 : 0x0201, up = middle ? 0x0208 : 0x0202;
                nint surface = 0;
                if (message == down || message == up)
                    surface = middle ? DesktopBlankHitTest.DesktopSurfaceAt(info.X, info.Y) : DesktopBlankHitTest.DesktopViewAt(info.X, info.Y);
                var point = new DesktopPointer(info.X, info.Y, info.Time, surface);
                bool blocked = (info.Flags & 1) != 0 || ModifiersHeld() || Environment.TickCount64 - _lastTrigger < 900;
                if (_gesture.Observe(_mode, (uint)message, point, blocked) is { } click)
                    _work.TryAdd((click, _mode, _generation, Environment.TickCount64));
            }
        }
        catch (Exception) { _gesture.Reset(); } // A shortcut must not interfere with normal mouse input.
        return CallNextHookEx(_hook, code, message, data);
    }

    private void CheckCandidates()
    {
        foreach (var candidate in _work.GetConsumingEnumerable())
        {
            try
            {
                if (Environment.TickCount64 - candidate.Queued > 750) continue;
                bool accepted = candidate.Mode == DesktopMouseGesture.MiddleDoubleClick
                    ? DesktopBlankHitTest.IsDesktopSurface(candidate.Click)
                    : DesktopBlankHitTest.IsBlank(candidate.Click);
                if (!accepted) continue;
                _dispatcher.BeginInvoke(() =>
                {
                    if (_disposed || _hook == 0 || candidate.Generation != _generation ||
                        Environment.TickCount64 - candidate.Queued > 750 || Environment.TickCount64 - _lastTrigger < 900) return;
                    _lastTrigger = Environment.TickCount64;
                    Triggered?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (Exception) { /* Unavailable or stale accessibility information means no shortcut. */ }
        }
    }

    private static bool ModifiersHeld() => ModifierKeys.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
        _work.CompleteAdding();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData { public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public nuint Extra; }
    private delegate nint HookProc(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int hook, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
