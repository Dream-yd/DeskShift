using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Services;

internal sealed record DesktopGlassFrame(BitmapSource Image, byte[] Pixels, MonitorInfo Monitor);

// The capture and window boundary is injectable so lifetime tests never need a native window or desktop capture.
internal sealed record LiquidGlassPlatform(
    Func<bool> IsVisible,
    Func<WindowState> WindowState,
    Func<bool> HighContrast,
    Func<TimeSpan> FadeDuration,
    Func<MonitorInfo?> SelectMonitor,
    Func<MonitorInfo, (BitmapSource? Image, bool Wallpaper)> Capture,
    Func<FrameworkElement, DesktopGlassFrame, Rect>? SourceBounds = null);

/// <summary>A bounded live desktop feed, suspended while hidden, minimized or switching.</summary>
internal sealed class LiquidGlassCoordinator : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _scene;
    private readonly LiquidGlassPlatform _platform;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private DispatcherTimer? _backdropRelease;
    private LiquidGlassBrush? _globalMaterial;
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;
    private ResourceDictionary? _palette;
    private bool _capturing;
    private bool _refreshPending;
    private bool _active;
    private bool _disposed;
    private int _generation;
    private int _backdropGeneration;
    private SkinPreference _requested;
    public bool IsEnabled { get; private set; }
    public bool IsActive => _active && CanCapture;
    public bool IsDark => ThemeManager.IsDark;
    public DesktopGlassFrame? Frame { get; private set; }
    public bool UsesWallpaperFallback { get; private set; }
    public event EventHandler? Changed;

    public LiquidGlassCoordinator(Window window, FrameworkElement scene)
        : this(window, scene, new LiquidGlassPlatform(
            () => window.IsVisible, () => window.WindowState, () => SystemParameters.HighContrast,
            () => TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 280 : 0),
            () => SelectMonitor(window), CaptureImage)) { }

    internal LiquidGlassCoordinator(Window window, FrameworkElement scene, LiquidGlassPlatform platform)
    {
        window.Dispatcher.VerifyAccess();
        scene.Dispatcher.VerifyAccess();
        _window = window;
        _scene = scene;
        _platform = platform;
        _refresh.Tick += OnRefresh;
        _window.IsVisibleChanged += OnVisibleChanged;
        _window.StateChanged += OnStateChanged;
        _window.LocationChanged += OnLocationChanged;
        _window.SizeChanged += OnLocationChanged;
        ThemeManager.Changed += OnThemeChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    public void Apply(SkinPreference preference)
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _requested = preference;
        bool enabled = preference == SkinPreference.LiquidGlass && !_platform.HighContrast();
        if (IsEnabled == enabled && (enabled == (_palette is not null))) return;
        IsEnabled = enabled;
        InvalidateCapture();
        CancelBackdropRelease();
        UpdatePalette();
        TimeSpan duration = _platform.HighContrast() ? TimeSpan.Zero : _platform.FadeDuration();
        double from = _scene.Opacity, target = enabled ? 1 : 0;
        _scene.BeginAnimation(UIElement.OpacityProperty, null);
        // This layer is owned by the coordinator. SetCurrentValue is discarded
        // when BeginAnimation replaces the value modifier, reverting to XAML's 0.
        _scene.SetValue(UIElement.OpacityProperty, target);
        int backdropGeneration = _backdropGeneration;
        if (duration > TimeSpan.Zero)
        {
            var fade = new DoubleAnimation(from, target, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop,
            };
            if (!enabled) fade.Completed += (_, _) => ReleaseBackdrop(backdropGeneration);
            _scene.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        UpdateActivity(preserveBackdrop: !enabled && duration > TimeSpan.Zero);
        if (!enabled)
        {
            Frame = null;
            UsesWallpaperFallback = false;
            if (duration <= TimeSpan.Zero) ReleaseBackdrop(backdropGeneration);
            else
            {
                // Also release when the scene is detached and its animation clock cannot complete.
                var timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher) { Interval = duration };
                timer.Tick += (_, _) => { timer.Stop(); ReleaseBackdrop(backdropGeneration); };
                _backdropRelease = timer;
                timer.Start();
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool CanCapture => !_disposed && IsEnabled && _platform.IsVisible() && _platform.WindowState() != WindowState.Minimized;

    internal void UpdateActivity() => UpdateActivity(preserveBackdrop: false);

    private void UpdateActivity(bool preserveBackdrop)
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed) return;
        bool active = CanCapture;
        if (active == _active)
        {
            if (!active && !preserveBackdrop)
            {
                Frame = null;
                UsesWallpaperFallback = false;
                CancelBackdropRelease();
                _window.Resources.Remove("Brush.DesktopBackdrop");
                _globalMaterial = null;
            }
            return;
        }
        _active = active;
        InvalidateCapture();
        if (active) _refresh.Start();
        else
        {
            _refresh.Stop();
            Frame = null;
            UsesWallpaperFallback = false;
            if (!preserveBackdrop)
            {
                CancelBackdropRelease();
                _window.Resources.Remove("Brush.DesktopBackdrop");
                _globalMaterial = null;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (active) _ = RefreshAsync();
    }

    private void InvalidateCapture()
    {
        _generation++;
        _refreshPending = false;
        _captureCancellation?.Cancel();
    }

    public Task RefreshAsync()
    {
        _window.Dispatcher.VerifyAccess();
        if (!IsActive || _window.DataContext is ViewModels.MainViewModel { IsBusy: true }) return Task.CompletedTask;
        if (_capturing) { _refreshPending = true; return _captureTask ?? Task.CompletedTask; }
        Task capture = CaptureAsync();
        _captureTask = capture.IsCompleted ? null : capture;
        return capture;
    }

    private async Task CaptureAsync()
    {
        _capturing = true;
        int generation = _generation;
        bool liveFrame = false;
        using var cancellation = new CancellationTokenSource();
        _captureCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        try
        {
            MonitorInfo? monitor = _platform.SelectMonitor();
            if (monitor is null) return;
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var captured = _platform.Capture(monitor);
                token.ThrowIfCancellationRequested();
                return (Frame: captured.Image is null ? null : PrepareFrame(captured.Image, monitor, token), captured.Wallpaper);
            }, token);
            if (token.IsCancellationRequested || generation != _generation || !IsActive || result.Frame is null ||
                _window.DataContext is ViewModels.MainViewModel { IsBusy: true }) return;
            Frame = result.Frame;
            UsesWallpaperFallback = result.Wallpaper;
            liveFrame = !UsesWallpaperFallback;
            RefreshSceneBrush();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or OverflowException or FileFormatException or System.Runtime.InteropServices.COMException) { }
        finally
        {
            _captureCancellation = null;
            _captureTask = null;
            _capturing = false;
            TimeSpan cadence = TimeSpan.FromMilliseconds(liveFrame ? 33 : 250);
            if (_refresh.Interval != cadence) _refresh.Interval = cadence;
            if (!liveFrame) _refreshPending = false;
            if (_refreshPending && IsActive && !_window.Dispatcher.HasShutdownStarted)
            {
                _refreshPending = false;
                _ = _window.Dispatcher.BeginInvoke(() => { if (IsActive && !_capturing) _ = RefreshAsync(); });
            }
        }
    }

    internal static DesktopGlassFrame PrepareFrame(BitmapSource image, MonitorInfo monitor, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        double scale = Math.Min(1, Math.Min(1280d / image.PixelWidth, 720d / image.PixelHeight));
        BitmapSource bounded = scale < 1 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;
        if (bounded.Format != PixelFormats.Bgra32) bounded = new FormatConvertedBitmap(bounded, PixelFormats.Bgra32, null, 0);
        int width = bounded.PixelWidth, height = bounded.PixelHeight;
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        token.ThrowIfCancellationRequested();
        bounded.CopyPixels(pixels, stride, 0);
        token.ThrowIfCancellationRequested();
        // Materialize a detached, 96-DPI bitmap: retaining a TransformedBitmap would retain its full-size source.
        var frozen = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        frozen.Freeze();
        return new(frozen, pixels, monitor);
    }

    private static MonitorInfo? SelectMonitor(Window window)
    {
        Point center = window.PointToScreen(new Point(window.ActualWidth / 2, window.ActualHeight / 2));
        var monitors = MonitorTopologyReader.Capture().Monitors;
        return monitors.FirstOrDefault(m => center.X >= m.X && center.X < m.X + m.Width &&
            center.Y >= m.Y && center.Y < m.Y + m.Height) ?? monitors.FirstOrDefault(m => m.IsPrimary);
    }

    private static (BitmapSource? Image, bool Wallpaper) CaptureImage(MonitorInfo monitor)
    {
        var preview = new DesktopPreviewService().Capture(monitor.DeviceName);
        if (preview is not null) return (DesktopBitmap.FromPreviewForGlass(preview), false);
        string? path = new WallpaperService().Read().Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return (null, false);
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = Math.Min(1920, monitor.Width);
        image.StreamSource = stream; image.EndInit(); image.Freeze();
        return (image, true);
    }

    public Rect SourceBounds(FrameworkElement surface)
    {
        var frame = Frame ?? throw new InvalidOperationException("Desktop backdrop unavailable.");
        if (_platform.SourceBounds is { } bounds) return bounds(surface, frame);
        Point origin = surface.PointToScreen(new Point());
        DpiScale dpi = VisualTreeHelper.GetDpi(surface);
        return new((origin.X - frame.Monitor.X) * frame.Image.PixelWidth / frame.Monitor.Width,
            (origin.Y - frame.Monitor.Y) * frame.Image.PixelHeight / frame.Monitor.Height,
            surface.ActualWidth * dpi.DpiScaleX * frame.Image.PixelWidth / frame.Monitor.Width,
            surface.ActualHeight * dpi.DpiScaleY * frame.Image.PixelHeight / frame.Monitor.Height);
    }

    private void RefreshSceneBrush()
    {
        if (!IsActive || Frame is not { } frame || !_scene.IsLoaded || _scene.ActualWidth <= 0 || _scene.ActualHeight <= 0) return;
        _globalMaterial ??= new LiquidGlassBrush();
        _globalMaterial.Update(frame, SourceBounds(_scene), _scene.RenderSize, 12, IsDark, global: true);
        if (!_window.Resources.Contains("Brush.DesktopBackdrop") ||
            !ReferenceEquals(_window.Resources["Brush.DesktopBackdrop"], _globalMaterial.Brush))
            _window.Resources["Brush.DesktopBackdrop"] = _globalMaterial.Brush;
    }

    private void CancelBackdropRelease()
    {
        _backdropGeneration++;
        _backdropRelease?.Stop();
        _backdropRelease = null;
    }

    private void ReleaseBackdrop(int generation)
    {
        if (generation != _backdropGeneration || IsEnabled || _disposed) return;
        CancelBackdropRelease();
        _window.Resources.Remove("Brush.DesktopBackdrop");
        _globalMaterial = null;
    }

    private void UpdatePalette()
    {
        if (_palette is not null) _window.Resources.MergedDictionaries.Remove(_palette);
        _palette = null;
        if (!IsEnabled) return;
        _palette = new ResourceDictionary();
        _palette["Brush.DesktopVeil"] = new SolidColorBrush(IsDark ? Color.FromArgb(0, 8, 18, 36) : Color.FromArgb(0, 243, 247, 252));
        _palette["Brush.Text.Muted"] = new SolidColorBrush(IsDark ? Color.FromRgb(220, 230, 243) : Color.FromRgb(63, 76, 95));
        _palette["Brush.Text.Secondary"] = new SolidColorBrush(IsDark ? Color.FromRgb(224, 232, 245) : Color.FromRgb(63, 76, 95));
        _palette["Brush.Panel"] = new SolidColorBrush(IsDark ? Color.FromArgb(38, 13, 24, 43) : Color.FromArgb(42, 231, 241, 253));
        _palette["Brush.Card"] = new SolidColorBrush(IsDark ? Color.FromArgb(32, 18, 33, 55) : Color.FromArgb(40, 247, 251, 255));
        _palette["Brush.Input"] = new SolidColorBrush(IsDark ? Color.FromArgb(100, 18, 33, 55) : Color.FromArgb(105, 247, 251, 255));
        _palette["Brush.Sunken"] = new SolidColorBrush(IsDark ? Color.FromArgb(28, 19, 34, 56) : Color.FromArgb(36, 230, 242, 254));
        _palette["Brush.Hairline"] = new SolidColorBrush(IsDark ? Color.FromArgb(178, 168, 203, 232) : Color.FromArgb(230, 208, 229, 249));
        _window.Resources.MergedDictionaries.Add(_palette);
    }

    private void OnRefresh(object? sender, EventArgs e) => _ = RefreshAsync();
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateActivity();
    private void OnStateChanged(object? sender, EventArgs e) => UpdateActivity();
    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!IsActive || Frame is null) return;
        RefreshSceneBrush();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        UpdatePalette();
        RefreshSceneBrush();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_disposed && !_window.Dispatcher.HasShutdownStarted &&
            e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
            _window.Dispatcher.BeginInvoke(() => { if (!_disposed) Apply(_requested); });
    }
    public void Dispose()
    {
        if (_disposed) return;
        _window.Dispatcher.VerifyAccess();
        _disposed = true;
        IsEnabled = false;
        _active = false;
        InvalidateCapture();
        CancelBackdropRelease();
        _scene.BeginAnimation(UIElement.OpacityProperty, null);
        _scene.SetCurrentValue(UIElement.OpacityProperty, 0d);
        _window.Resources.Remove("Brush.DesktopBackdrop");
        _globalMaterial = null;
        if (_palette is not null) _window.Resources.MergedDictionaries.Remove(_palette);
        _palette = null;
        _refresh.Stop();
        _refresh.Tick -= OnRefresh;
        _window.IsVisibleChanged -= OnVisibleChanged;
        _window.StateChanged -= OnStateChanged;
        _window.LocationChanged -= OnLocationChanged;
        _window.SizeChanged -= OnLocationChanged;
        ThemeManager.Changed -= OnThemeChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        Frame = null;
        UsesWallpaperFallback = false;
        Changed?.Invoke(this, EventArgs.Empty);
        Changed = null;
    }
}
