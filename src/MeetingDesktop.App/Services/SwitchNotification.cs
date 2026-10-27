using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MeetingDesktop.App.Localization;

namespace MeetingDesktop.App.Services;

/// <summary>A per-pixel transparent capsule that leaves the live background visible.</summary>
internal sealed class SwitchNotification : IDisposable
{
    // WPF windows and their lifetimes belong to their dispatcher thread.
    [ThreadStatic] private static SwitchNotification? _active;
    private readonly Func<string> _status;
    private readonly bool _animate;
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly TextBlock _indicator;
    private readonly Border _card;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
    private long? _finished;
    private bool _closing;
    private HwndSource? _source;
    private nint _monitor;
    private DispatcherOperation? _geometryUpdate;
    private (int Width, int Height) _regionSize;
    private bool CanAnimate => _animate && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    internal Window Window { get; }
    internal bool IsClosed { get; private set; }

    public SwitchNotification(Func<string> status, bool animate)
    {
        _status = status;
        _animate = animate && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        _title = new TextBlock { Text = LocalizationManager.Instance["SwitchNotificationWorking"],
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        _detail = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        _indicator = new TextBlock { Text = "\u2022", FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 16, Width = 22, Margin = new Thickness(0, 0, 10, 0),
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title); text.Children.Add(_detail);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(text, 1);
        body.Children.Add(_indicator); body.Children.Add(text);
        _card = new Border { CornerRadius = new CornerRadius(32), Padding = new Thickness(18, 9, 22, 9),
            BorderThickness = new Thickness(1), Child = body };
        Window = new Window { Width = 300, Height = 64, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false,
            Focusable = false, IsHitTestVisible = false, Topmost = true,
            UseLayoutRounding = true, AllowsTransparency = true,
            Background = Brushes.Transparent, Content = _card, Title = "Meeting Desktop" };
        Window.Closed += OnClosed;
        _timer.Tick += OnTick;
        ApplyColors();
    }

    internal void Show()
    {
        if (IsClosed || Window.IsVisible) return;
        _active?.Dispose();
        _active = this;
        nint handle = new WindowInteropHelper(Window).EnsureHandle();
        SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x80 | 0x20);
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessage);
        // System Acrylic paints an opaque rectangle outside the rounded WPF card,
        // even with an HWND region. Per-pixel alpha keeps those corners transparent.
        ThemeManager.Changed += OnThemeChanged;
        // Capture once: foreground changes during the operation must not make the toast jump screens.
        _monitor = MonitorFromWindow(GetForegroundWindow(), 2);
        PositionOnActiveMonitor(handle);
        Window.Show();
        // Showing/moving a WPF HWND can change its DPI and physical size.
        PositionOnActiveMonitor(handle);
        UpdateWindowRegion(handle);
        if (CanAnimate)
            _card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _timer.Start();
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
        if (message == 0x84) { handled = true; return -1; } // HTTRANSPARENT
        if (message == 0x0005) UpdateWindowRegion(hwnd); // WM_SIZE: use actual HWND pixels.
        if (message is 0x02E0 or 0x007E or 0x001A) // DPI, display, or work area/settings changed.
        {
            // Leave WM_DPICHANGED unhandled so WPF updates its transform and suggested bounds first.
            QueueGeometryUpdate();
            if (message == 0x001A) ApplyColors();
        }
        return 0;
    }

    private void QueueGeometryUpdate()
    {
        if (IsClosed || _geometryUpdate?.Status == DispatcherOperationStatus.Pending) return;
        _geometryUpdate = Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _geometryUpdate = null;
            if (IsClosed || _source is null) return;
            PositionOnActiveMonitor(_source.Handle);
            UpdateWindowRegion(_source.Handle);
        }));
    }

    // Work area and result are physical pixels, including negative virtual-screen origins.
    internal static Int32Rect CalculateBounds(Int32Rect work, uint dpi)
    {
        double scale = (dpi == 0 ? 96 : dpi) / 96d;
        int width = Math.Min(work.Width, (int)Math.Ceiling(300 * scale));
        int height = Math.Min(work.Height, (int)Math.Ceiling(64 * scale));
        // Keep the capsule in the lower middle, rather than immediately above the taskbar.
        // On unusually small work areas, reduce the margin symmetrically to stay on screen.
        int gap = Math.Min((work.Height - height) / 2, (int)Math.Ceiling(24 * scale));
        int top = (int)Math.Round(work.Height * .82 - height / 2d);
        top = Math.Clamp(top, gap, work.Height - height - gap);
        return new Int32Rect(work.X + (work.Width - width) / 2,
            work.Y + top, width, height);
    }

    private void PositionOnActiveMonitor(nint handle)
    {
        var info = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
        if (!GetMonitorInfo(_monitor, ref info))
        {
            _monitor = MonitorFromWindow(handle, 2);
            if (!GetMonitorInfo(_monitor, ref info)) return;
        }
        var bounds = CalculateBounds(new Int32Rect(info.Work.Left, info.Work.Top,
            info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top), GetDpiForWindow(handle));
        _ = SetWindowPos(handle, -1, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0200);
    }

    private void UpdateWindowRegion(nint handle)
    {
        if (IsClosed || !GetWindowRect(handle, out var rect)) return;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || _regionSize == (width, height)) return;
        int diameter = Math.Min(width, height);
        nint region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == 0) return;
        var previousSize = _regionSize;
        _regionSize = (width, height); // SetWindowRgn can synchronously send window-position messages.
        if (SetWindowRgn(handle, region, Window.IsVisible) == 0)
        {
            _regionSize = previousSize;
            _ = DeleteObject(region);
        }
        // On success Windows owns the HRGN and disposes it when replaced or the HWND closes.
    }

    internal void Complete(bool success)
    {
        if (IsClosed || _finished is not null) return;
        _finished = System.Diagnostics.Stopwatch.GetTimestamp();
        _title.Text = LocalizationManager.Instance[success ? "SwitchNotificationDone" : "SwitchNotificationFailed"];
        _detail.Text = success ? ReadStatus() : LocalizationManager.Instance["SwitchNotificationReview"];
        _indicator.Text = success ? "\u2713" : "!";
        if (!Window.IsVisible) Dispose();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (IsClosed) return;
        // The independent watchdog also removes a toast abandoned during shutdown/error handling.
        if (_closing && _finished is { } closing && System.Diagnostics.Stopwatch.GetElapsedTime(closing).TotalSeconds >= 2.5)
        { Dispose(); return; }
        if (_finished is { } finished && System.Diagnostics.Stopwatch.GetElapsedTime(finished).TotalSeconds >= 1.6)
        { CloseWithFade(); return; }
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalSeconds >= 30)
        { Dispose(); return; }
        if (_finished is null) _detail.Text = ReadStatus();
    }

    private string ReadStatus()
    {
        try { return _status(); }
        catch (Exception)
        {
            // Optional progress text must not interrupt switching or the lifetime timer.
            return _detail.Text;
        }
    }

    private void CloseWithFade()
    {
        if (_closing || IsClosed) return;
        _closing = true;
        if (!CanAnimate) { Dispose(); return; }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Dispose();
        _card.BeginAnimation(UIElement.OpacityProperty, fade);
        // The timer remains live in case WPF cannot render the final animation frame.
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyColors();
    private void ApplyColors()
    {
        if (!CanAnimate) _card.BeginAnimation(UIElement.OpacityProperty, null);
        if (SystemParameters.HighContrast)
        {
            _card.Background = SystemColors.WindowBrush;
            _card.BorderBrush = _title.Foreground = _detail.Foreground = _indicator.Foreground = SystemColors.WindowTextBrush;
            return;
        }
        bool dark = ThemeManager.IsDark;
        // Approximate Codex's neutral charcoal/warm white; these are not sampled brand colors.
        _card.Background = new SolidColorBrush(dark ? Color.FromArgb(230, 36, 36, 35) : Color.FromArgb(235, 247, 246, 242));
        _card.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(70, 181, 179, 172) : Color.FromArgb(65, 105, 103, 96));
        _title.Foreground = new SolidColorBrush(dark ? Color.FromRgb(242, 241, 236) : Color.FromRgb(42, 42, 39));
        _detail.Foreground = _indicator.Foreground = new SolidColorBrush(
            dark ? Color.FromRgb(188, 187, 180) : Color.FromRgb(104, 103, 96));
    }

    private void OnClosed(object? sender, EventArgs e) => Release();
    private void Release()
    {
        if (IsClosed) return;
        IsClosed = true;
        if (ReferenceEquals(_active, this)) _active = null;
        _timer.Stop(); _timer.Tick -= OnTick;
        _geometryUpdate?.Abort(); _geometryUpdate = null;
        ThemeManager.Changed -= OnThemeChanged;
        _source?.RemoveHook(WindowMessage);
        Window.Closed -= OnClosed;
    }

    public void Dispose()
    {
        if (IsClosed) return;
        Release(); Window.Close();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInformation
    { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInformation info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
