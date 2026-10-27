using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MeetingDesktop.App.Services;

internal static class LiquidGlassMaterial
{
    private static readonly DependencyProperty SurfaceProperty = DependencyProperty.RegisterAttached("Surface",
        typeof(Surface), typeof(LiquidGlassMaterial), new PropertyMetadata(null));
    public static readonly DependencyProperty CoordinatorProperty = DependencyProperty.RegisterAttached("Coordinator",
        typeof(LiquidGlassCoordinator), typeof(LiquidGlassMaterial), new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.Inherits, OnCoordinatorChanged));
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool),
        typeof(LiquidGlassMaterial), new PropertyMetadata(false, OnEnabled));
    public static void SetCoordinator(DependencyObject target, LiquidGlassCoordinator? value) => target.SetValue(CoordinatorProperty, value);
    public static LiquidGlassCoordinator? GetCoordinator(DependencyObject target) => (LiquidGlassCoordinator?)target.GetValue(CoordinatorProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);

    private static void OnEnabled(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Border border) return;
        var surface = (Surface?)border.GetValue(SurfaceProperty);
        if (e.NewValue is true)
        {
            if (surface is null) { surface = new Surface(border); border.SetValue(SurfaceProperty, surface); }
            surface.Enable();
        }
        else surface?.Disable();
    }

    private static void OnCoordinatorChanged(DependencyObject target, DependencyPropertyChangedEventArgs e) =>
        ((Surface?)target.GetValue(SurfaceProperty))?.Reconnect();

    private sealed class Surface
    {
        private readonly Border _border;
        private LiquidGlassCoordinator? _coordinator;
        private ScrollViewer? _scroll;
        private LiquidGlassBrush? _material;
        private VisualBrush? _ownedBackground;
        private bool _enabled;
        private bool _loaded;

        public Surface(Border border)
        {
            _border = border;
        }

        public void Enable()
        {
            if (_enabled) return;
            _enabled = true;
            _border.Loaded += OnLoaded;
            _border.Unloaded += OnUnloaded;
            if (_border.IsLoaded) OnLoaded(_border, new RoutedEventArgs());
        }

        public void Disable()
        {
            if (!_enabled) return;
            _enabled = false;
            _border.Loaded -= OnLoaded;
            _border.Unloaded -= OnUnloaded;
            _loaded = false;
            Detach();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_enabled || _loaded) return;
            _loaded = true;
            Reconnect();
        }

        public void Reconnect()
        {
            if (!_enabled || !_loaded) return;
            Detach();
            _coordinator = GetCoordinator(_border);
            if (_coordinator is null) return;
            _coordinator.Changed += OnChanged;
            _border.SizeChanged += OnChanged;
            DependencyObject? parent = _border;
            while (parent is not null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
            _scroll = parent as ScrollViewer;
            if (_scroll is not null) _scroll.ScrollChanged += OnChanged;
            Schedule();
        }
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            Detach();
        }

        private void Detach()
        {
            if (_coordinator is not null) _coordinator.Changed -= OnChanged;
            _border.SizeChanged -= OnChanged;
            if (_scroll is not null) _scroll.ScrollChanged -= OnChanged;
            _coordinator = null;
            _scroll = null;
            RestoreBackground();
        }

        private void RestoreBackground()
        {
            // Do not erase a warning/selection brush installed by the owner after our render.
            if (_ownedBackground is not null && ReferenceEquals(_border.ReadLocalValue(Border.BackgroundProperty), _ownedBackground))
                _border.ClearValue(Border.BackgroundProperty);
            _ownedBackground = null;
            _material = null;
        }

        private bool MaySetBackground()
        {
            object local = _border.ReadLocalValue(Border.BackgroundProperty);
            return local == DependencyProperty.UnsetValue || ReferenceEquals(local, _ownedBackground);
        }
        private void OnChanged(object? sender, EventArgs e) => Schedule();
        private void Schedule()
        {
            if (!_enabled || !_loaded || _coordinator?.IsActive != true)
            {
                RestoreBackground();
                return;
            }
            // Explicit warning/selection backgrounds retain their semantic color.
            if (!MaySetBackground()) { _ownedBackground = null; _material = null; return; }
            if (_coordinator.Frame is not { } frame || _border.ActualWidth < 2 || _border.ActualHeight < 2) return;
            try
            {
                if (_scroll is not null)
                {
                    Rect visible = _border.TransformToAncestor(_scroll).TransformBounds(new Rect(_border.RenderSize));
                    if (visible.Bottom < 0 || visible.Top > _scroll.ActualHeight) return;
                }
                _material ??= new LiquidGlassBrush();
                _material.Update(frame, _coordinator.SourceBounds(_border), _border.RenderSize,
                    _border.CornerRadius.TopLeft, _coordinator.IsDark);
                bool entering = _ownedBackground is null;
                _ownedBackground = _material.Brush;
                _border.Background = _ownedBackground;
                if (entering && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                    _ownedBackground.BeginAnimation(Brush.OpacityProperty,
                        new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
            { RestoreBackground(); }
        }
    }
}
