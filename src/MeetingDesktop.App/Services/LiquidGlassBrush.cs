using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace MeetingDesktop.App.Services;

/// <summary>A live background visual; the compositor shades it without re-rasterizing every card on the CPU.</summary>
internal sealed class LiquidGlassBrush
{
    private readonly ImageBrush _source = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
    private readonly Grid _visual = new() { IsHitTestVisible = false };
    private readonly Rectangle _image = new();
    private readonly Rectangle _frost = new();
    private readonly GlassRefractionEffect? _effect;
    private (double Width, double Height, double Radius)? _geometry;
    public VisualBrush Brush { get; }
    internal bool UsesRefraction => _effect is not null;

    public LiquidGlassBrush()
    {
        _image.Fill = _source;
        if (RenderCapability.IsPixelShaderVersionSupported(2, 0) || RenderCapability.IsPixelShaderVersionSupportedInSoftware(2, 0))
        {
            _effect = new GlassRefractionEffect();
            _image.Effect = _effect;
        }
        else _image.Effect = new BlurEffect { Radius = 8, RenderingBias = RenderingBias.Performance };
        _visual.Children.Add(_image);
        _visual.Children.Add(_frost);
        Brush = new VisualBrush(_visual) { Stretch = Stretch.Fill, AutoLayoutContent = false };
    }

    public void Update(DesktopGlassFrame frame, Rect crop, Size size, double radius, bool dark, bool global = false)
    {
        if (size.Width < 1 || size.Height < 1 || crop.Width <= 0 || crop.Height <= 0) return;
        _source.ImageSource = frame.Image;
        _source.Viewbox = crop;
        bool resized = _visual.Width != size.Width || _visual.Height != size.Height;
        _visual.Width = size.Width;
        _visual.Height = size.Height;
        double tint = global ? (dark ? .70 : .85) : (dark ? .87 : .97);
        Color color = dark ? Color.FromRgb(18, 30, 48) : Color.FromRgb(244, 244, 244);
        if (_effect is not null)
        {
            double corner = Math.Max(1, radius * crop.Width / size.Width);
            var geometry = (crop.Width, crop.Height, corner);
            if (_geometry != geometry)
            {
                GlassOpticalMaps maps = LiquidGlassOptics.Create(crop.Width, crop.Height, corner);
                _effect.Optics = new ImageBrush(maps.Displacement) { Stretch = Stretch.Fill };
                _effect.Rim = new ImageBrush(maps.Rim) { Stretch = Stretch.Fill };
                _geometry = geometry;
            }
            _effect.Geometry = new Point4D(crop.Width, crop.Height, corner, tint);
            _effect.TintColor = color;
            _frost.Fill = Brushes.Transparent;
        }
        else
        {
            _frost.Fill = new SolidColorBrush(Color.FromArgb((byte)(255 * tint), color.R, color.G, color.B));
        }
        if (resized)
        {
            _visual.Measure(size);
            _visual.Arrange(new Rect(size));
            _visual.UpdateLayout();
        }
    }
}

internal sealed class GlassRefractionEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
        nameof(Input), typeof(GlassRefractionEffect), 0);
    public static readonly DependencyProperty OpticsProperty = RegisterPixelShaderSamplerProperty(
        nameof(Optics), typeof(GlassRefractionEffect), 1);
    public static readonly DependencyProperty RimProperty = RegisterPixelShaderSamplerProperty(
        nameof(Rim), typeof(GlassRefractionEffect), 2);
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry), typeof(Point4D), typeof(GlassRefractionEffect),
        new UIPropertyMetadata(new Point4D(1, 1, 1, .87), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty TintColorProperty = DependencyProperty.Register(
        nameof(TintColor), typeof(Color), typeof(GlassRefractionEffect),
        new UIPropertyMetadata(Colors.Black, PixelShaderConstantCallback(1)));
    public Brush? Input { get => (Brush?)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Brush? Optics { get => (Brush?)GetValue(OpticsProperty); set => SetValue(OpticsProperty, value); }
    public Brush? Rim { get => (Brush?)GetValue(RimProperty); set => SetValue(RimProperty, value); }
    public Point4D Geometry { get => (Point4D)GetValue(GeometryProperty); set => SetValue(GeometryProperty, value); }
    public Color TintColor { get => (Color)GetValue(TintColorProperty); set => SetValue(TintColorProperty, value); }

    public GlassRefractionEffect()
    {
        PixelShader = new PixelShader
        {
            UriSource = new Uri("pack://application:,,,/MeetingDesktop;component/Resources/Shaders/LiquidGlass.ps"),
        };
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(OpticsProperty);
        UpdateShaderValue(RimProperty);
        UpdateShaderValue(GeometryProperty);
        UpdateShaderValue(TintColorProperty);
    }
}
