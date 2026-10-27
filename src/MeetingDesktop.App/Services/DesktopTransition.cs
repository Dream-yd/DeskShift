using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Services;

/// <summary>A bounded, click-through desktop fade below ordinary application windows.</summary>
internal sealed class DesktopTransition : IAsyncDisposable
{
    private readonly List<Window> _covers = [];
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim CaptureGate = new(1, 1);
    private readonly CancellationTokenSource _captureCancellation = new();
    private bool _closed;
    private bool _fading;
    internal string? SkipReason { get; private set; }
    private readonly bool _enabled;
    private readonly Func<string>? _status;
    private readonly Func<nint>? _getDesktopHost;
    private readonly Task<(MonitorInfo Monitor, BitmapSource Image)[]>? _prepared;

    public DesktopTransition(bool enabled, Func<string>? status = null,
        Func<CancellationToken, (MonitorInfo Monitor, BitmapSource Image)[]>? captureFrames = null,
        Func<nint>? getDesktopHost = null)
    {
        _enabled = enabled;
        _status = status;
        _getDesktopHost = getDesktopHost;
        // Start while the workflow is validating and saving its recovery record.
        // By the before-change callback the old desktop is usually already captured.
        if (!enabled || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;
        if (!CaptureGate.Wait(0)) { SkipReason = "capture-busy"; return; }
        CancellationToken token = _captureCancellation.Token;
        _prepared = Task.Run(() =>
        {
            try { return (captureFrames ?? CaptureFrames)(token); }
            catch (Exception) { return []; }
            finally { CaptureGate.Release(); }
        });
    }


    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || _closed) return;
        try
        {
            nint desktopHost;
            if (_getDesktopHost is not null) desktopHost = _getDesktopHost();
            else
            {
                nint view = DesktopGridView.FindView(retries: 1, cancellationToken: cancellationToken);
                desktopHost = view == 0 ? 0 : GetAncestor(view, 2);
            }
            if (desktopHost == 0) { SkipReason = "desktop-host-unavailable"; return; }
            if (_prepared is not { } capture) return;
            // Bounded asynchronous wait; input keeps dispatching throughout.
            if (await Task.WhenAny(capture, Task.Delay(250, cancellationToken)) != capture)
            { SkipReason = "capture-budget"; _captureCancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); return; }
            var frames = await capture;
            cancellationToken.ThrowIfCancellationRequested();
            nint previous = GetWindow(desktopHost, 3 /* GW_HWNDPREV */);
            if (!IsWindow(desktopHost) || previous == 0 || !IsWindow(previous)) { SkipReason = "unverified-z-order"; return; }
            foreach (var frame in frames)
            {
                var cover = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    AllowsTransparency = true, Background = Brushes.Transparent,
                    ShowInTaskbar = false, ShowActivated = false, Topmost = false,
                    IsHitTestVisible = false, Focusable = false,
                    Width = frame.Monitor.Width, Height = frame.Monitor.Height,
                    Content = new Image { Source = frame.Image, Stretch = Stretch.Fill, IsHitTestVisible = false },
                    ToolTip = _status?.Invoke(),
                };
                _covers.Add(cover);
                nint handle = new WindowInteropHelper(cover).EnsureHandle();
                SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x80 | 0x20);
                // Verify permission while still hidden. Denied desktop placement
                // must not briefly show an unpositioned full-screen window.
                if (!PlaceBelowApplications(handle, previous, desktopHost, frame.Monitor))
                { SkipReason = "window-placement-" + Marshal.GetLastPInvokeError(); Close(); return; }
                cover.Show();
                // Above Explorer's desktop, underneath ordinary application windows.
                if (!PlaceBelowApplications(handle, previous, desktopHost, frame.Monitor))
                { SkipReason = "window-placement-" + Marshal.GetLastPInvokeError(); Close(); return; }
            }
            if (_covers.Count > 0)
            {
                _watchdog.Tick += OnWatchdog;
                _watchdog.Start();
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            }
        }
        catch (OperationCanceledException) { Close(); throw; }
        catch (Exception ex) { SkipReason = ex.GetType().Name + ": " + ex.Message; Close(); }
    }

    private static bool PlaceBelowApplications(nint handle, nint previous, nint desktop, MonitorInfo monitor)
    {
        const uint flags = 0x0010 | 0x0200; // NOACTIVATE | NOOWNERZORDER
        if (SetWindowPos(handle, previous, monitor.X, monitor.Y, monitor.Width, monitor.Height, flags)) return true;
        // Some shell-owned predecessor HWNDs reject relative insertion. Moving only
        // our own window to the bottom needs no shell mutation or elevation.
        if (!SetWindowPos(handle, 1 /* HWND_BOTTOM */, monitor.X, monitor.Y, monitor.Width, monitor.Height, flags)) return false;
        // Never show a cover unless the real desktop is still below it. Windows
        // which were already above the desktop also remain above this bottom cover.
        nint next = GetWindow(handle, 2 /* GW_HWNDNEXT */);
        for (int visited = 0; next != 0 && visited < 512; visited++, next = GetWindow(next, 2))
            if (next == desktop) return true;
        return false;
    }

    private static (MonitorInfo Monitor, BitmapSource Image)[] CaptureFrames(CancellationToken token)
    {
        try
        {
            var frames = new List<(MonitorInfo, BitmapSource)>();
            var service = new DesktopPreviewService();
            foreach (var monitor in MonitorTopologyReader.Capture().Monitors)
            {
                token.ThrowIfCancellationRequested();
                if (service.Capture(monitor.DeviceName) is { } preview)
                    frames.Add((monitor, DesktopBitmap.FromPreview(preview)));
            }
            return frames.ToArray();
        }
        catch (Exception) { return []; }
    }

    public ValueTask DisposeAsync()
    {
        if (_closed || _fading) return ValueTask.CompletedTask;
        if (_covers.Count == 0) { Close(); return ValueTask.CompletedTask; }
        _fading = true;
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) { Close(); return ValueTask.CompletedTask; }
        foreach (var cover in _covers.ToArray())
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            fade.Completed += (_, _) => Close();
            cover.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        return ValueTask.CompletedTask;
    }

    private void OnWatchdog(object? sender, EventArgs e) => Close();
    private void OnDisplayChanged(object? sender, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(Close);
    private void Close()
    {
        if (_closed) return;
        _closed = true;
        _captureCancellation.Cancel();
        _captureCancellation.Dispose();
        _watchdog.Stop();
        _watchdog.Tick -= OnWatchdog;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        foreach (var cover in _covers.ToArray()) { cover.Content = null; cover.Close(); }
        _covers.Clear();
    }

    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
