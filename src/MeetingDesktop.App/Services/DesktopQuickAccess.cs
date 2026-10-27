using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using MeetingDesktop.App.Localization;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Services;

/// <summary>Owns the tray icon and global shortcut for the lifetime of the main window.</summary>
internal sealed class DesktopQuickAccess : IDisposable
{
    private const int HotkeyId = 0x4D44;
    private const int TrayMessage = 0x8000 + 73;
    private readonly Window _window;
    private readonly HwndSource? _source;
    private readonly GlobalHotkeyRegistration _hotkey;
    private readonly DesktopMouseShortcut? _mouse;
    private readonly NotifyIcon _notifyIcon = Shell_NotifyIcon;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly uint _taskbarCreated;
    private NotifyIconData _icon;
    private bool _ownsIcon;
    private bool _disposed;
    private int _attempts;
    private QuickSwitchHotkey? _preference;
    private DesktopMouseGesture? _mousePreference;
    private ContextMenu? _menu;
    private TrayDoubleClickAction _doubleClickAction = TrayDoubleClickAction.OpenWindow;
    private MenuItem? _openDoubleClickItem;
    private MenuItem? _switchDoubleClickItem;

    public DesktopQuickAccess(Window window)
    {
        _window = window;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        nint handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle)!;
        _hotkey = new((modifiers, key) => RegisterHotKey(handle, HotkeyId, modifiers, key),
            () => UnregisterHotKey(handle, HotkeyId));
        _mouse = new(window.Dispatcher);
        _mouse.Triggered += (_, _) => SwitchRequested?.Invoke(this, EventArgs.Empty);
        _icon = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = handle, Id = 1,
            Flags = 1 | 2 | 4 | 0x80, Callback = TrayMessage,
            Tip = "", Info = "", InfoTitle = "",
        };
        if (Environment.ProcessPath is { } executable)
        {
            var small = new nint[1];
            if (ExtractIconEx(executable, 0, null, small, 1) > 0)
            { _icon.Icon = small[0]; _ownsIcon = _icon.Icon != 0; }
        }
        if (_icon.Icon == 0) _icon.Icon = LoadIcon(0, (nint)32512);
        _source.AddHook(OnMessage);
        _retry.Tick += OnRetry;
        AddTrayIcon();
    }

    // Keeps message routing and menu/balloon behavior testable without a native window,
    // shell icon, keyboard registration or mouse hook.
    internal DesktopQuickAccess(Window window, GlobalHotkeyRegistration hotkey, NotifyIcon notifyIcon)
    {
        _window = window;
        _hotkey = hotkey;
        _notifyIcon = notifyIcon;
        _icon = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Id = 1,
            Flags = 1 | 2 | 4 | 0x80, Callback = TrayMessage,
            Tip = "", Info = "", InfoTitle = "",
        };
        _retry.Tick += OnRetry;
        AddTrayIcon();
    }

    public bool IsTrayAvailable { get; private set; }
    public HotkeyStatus HotkeyStatus { get; private set; }
    public bool IsMouseAvailable { get; private set; } = true;
    public event EventHandler? SwitchRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? AvailabilityChanged;
    public event Action<TrayDoubleClickAction>? TrayDoubleClickActionChanged;

    public void Apply(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _doubleClickAction = Enum.IsDefined(settings.TrayDoubleClickAction)
            ? settings.TrayDoubleClickAction : TrayDoubleClickAction.OpenWindow;
        UpdateDoubleClickChecks();
        if (_preference != settings.QuickSwitchHotkey || HotkeyStatus == HotkeyStatus.Unavailable)
        {
            HotkeyStatus = _hotkey.Apply(settings.QuickSwitchHotkey);
            _preference = settings.QuickSwitchHotkey;
        }
        if (_mousePreference != settings.DesktopMouseGesture || !IsMouseAvailable)
        {
            IsMouseAvailable = _mouse?.Apply(settings.DesktopMouseGesture) ?? true;
            _mousePreference = settings.DesktopMouseGesture;
        }
        UpdateTooltip();
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateTooltip()
    {
        if (_disposed) return;
        _icon.Tip = LocalizationManager.Instance[_doubleClickAction == TrayDoubleClickAction.SwitchDesktop
            ? "QuickTrayTipSwitch" : "QuickTrayTipOpen"];
        if (IsTrayAvailable) _notifyIcon(1 /* NIM_MODIFY */, ref _icon);
    }

    /// <summary>Shows a native balloon without activating or opening the application window.</summary>
    public void ShowNotification(string message)
    {
        if (_disposed || !IsTrayAvailable || string.IsNullOrWhiteSpace(message)) return;
        // A copy prevents later tooltip updates / Explorer restarts from replaying a balloon.
        NotifyIconData notification = _icon;
        notification.Flags = 0x10; // NIF_INFO only; never change focus, visibility or icon state.
        notification.Info = FitBalloonText(message.Replace('\0', ' '), 255);
        notification.InfoTitle = FitBalloonText(LocalizationManager.Instance["AppTitle"], 63);
        notification.InfoFlags = 1 | 0x10; // NIIF_INFO | NIIF_NOSOUND.
        _notifyIcon(1 /* NIM_MODIFY */, ref notification);
    }

    private static string FitBalloonText(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        int length = char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum;
        return text[..length];
    }

    private void AddTrayIcon()
    {
        IsTrayAvailable = _notifyIcon(0 /* NIM_ADD */, ref _icon);
        if (IsTrayAvailable)
        {
            _icon.Version = 4;
            _notifyIcon(4 /* NIM_SETVERSION */, ref _icon);
            _retry.Stop();
        }
        else if (_attempts < 10) _retry.Start();
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRetry(object? sender, EventArgs e)
    {
        if (++_attempts >= 10) _retry.Stop();
        AddTrayIcon();
    }

    internal nint OnMessage(nint window, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (_disposed) return 0;
        if (message == 0x0312 /* WM_HOTKEY */ && wparam == HotkeyId)
        {
            handled = true;
            QueueSwitch();
        }
        else if (message == TrayMessage)
        {
            int notification = (int)((long)lparam & 0xFFFF);
            if (notification == 0x0203 /* WM_LBUTTONDBLCLK */)
                QueueTrayDoubleClick();
            else if (notification is 0x007B or 0x0401 /* context menu or keyboard activation */)
                _window.Dispatcher.BeginInvoke(() => { if (!_disposed) ShowMenu(); });
            handled = true;
        }
        else if (_taskbarCreated != 0 && (uint)message == _taskbarCreated)
        {
            _attempts = 0;
            AddTrayIcon();
        }
        return 0;
    }

    private void QueueSwitch() => _window.Dispatcher.BeginInvoke(() =>
    {
        if (!_disposed) SwitchRequested?.Invoke(this, EventArgs.Empty);
    });

    private void QueueTrayDoubleClick()
    {
        TrayDoubleClickAction action = _doubleClickAction;
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (action == TrayDoubleClickAction.SwitchDesktop) SwitchRequested?.Invoke(this, EventArgs.Empty);
            else OpenRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private void ShowMenu()
    {
        _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _menu = CreateMenu();
        SetForegroundWindow(_source!.Handle);
        _menu.IsOpen = true;
    }

    internal ContextMenu CreateMenu()
    {
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        AddMenuItem("QuickToggle", () => SwitchRequested?.Invoke(this, EventArgs.Empty));
        AddMenuItem("QuickOpen", () => OpenRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add(new Separator());
        _openDoubleClickItem = AddDoubleClickOption("QuickTrayDoubleClickOpen", TrayDoubleClickAction.OpenWindow);
        _switchDoubleClickItem = AddDoubleClickOption("QuickTrayDoubleClickSwitch", TrayDoubleClickAction.SwitchDesktop);
        UpdateDoubleClickChecks();
        _menu.Items.Add(new Separator());
        AddMenuItem("QuickExit", () => ExitRequested?.Invoke(this, EventArgs.Empty));
        return _menu;
    }

    private MenuItem AddDoubleClickOption(string key, TrayDoubleClickAction action)
    {
        var item = new MenuItem { Header = LocalizationManager.Instance[key], IsCheckable = true };
        item.Click += (_, _) =>
        {
            if (_disposed) return;
            bool changed = _doubleClickAction != action;
            _doubleClickAction = action;
            UpdateDoubleClickChecks(); // Clicking the selected option cannot uncheck both.
            if (!changed) return;
            UpdateTooltip();
            TrayDoubleClickActionChanged?.Invoke(action);
        };
        _menu!.Items.Add(item);
        return item;
    }

    private void UpdateDoubleClickChecks()
    {
        if (_openDoubleClickItem is not null)
            _openDoubleClickItem.IsChecked = _doubleClickAction == TrayDoubleClickAction.OpenWindow;
        if (_switchDoubleClickItem is not null)
            _switchDoubleClickItem.IsChecked = _doubleClickAction == TrayDoubleClickAction.SwitchDesktop;
    }

    private void AddMenuItem(string key, Action action)
    {
        var item = new MenuItem { Header = LocalizationManager.Instance[key] };
        item.Click += (_, _) => action();
        _menu!.Items.Add(item);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retry.Stop();
        _retry.Tick -= OnRetry;
        _mouse?.Dispose();
        _hotkey.Dispose();
        _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _notifyIcon(2 /* NIM_DELETE */, ref _icon);
        IsTrayAvailable = false;
        if (_ownsIcon) DestroyIcon(_icon.Icon);
        _source?.RemoveHook(OnMessage);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id; public uint Flags; public uint Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    internal delegate bool NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, nint[]? large, nint[] small, uint count);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
