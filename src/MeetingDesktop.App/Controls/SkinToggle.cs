using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MeetingDesktop.App.Controls;

public sealed class SkinToggle : ToggleButton
{
    private TranslateTransform? _thumb;
    private FrameworkElement? _rainbow;
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _thumb = GetTemplateChild("ThumbMotion") as TranslateTransform;
        _rainbow = GetTemplateChild("SpectralThumb") as FrameworkElement;
        UpdateThumb(false);
    }
    protected override void OnChecked(RoutedEventArgs e) { base.OnChecked(e); UpdateThumb(true); }
    protected override void OnUnchecked(RoutedEventArgs e) { base.OnUnchecked(e); UpdateThumb(true); }
    private void UpdateThumb(bool animate)
    {
        bool enabled = IsChecked == true;
        bool motion = animate && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        var duration = TimeSpan.FromMilliseconds(motion ? 280 : 0);
        _thumb?.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(enabled ? 52 : 0, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _rainbow?.BeginAnimation(OpacityProperty, new DoubleAnimation(enabled ? 1 : 0, duration));
    }
}
