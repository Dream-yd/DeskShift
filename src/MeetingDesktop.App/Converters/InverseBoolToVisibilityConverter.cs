using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MeetingDesktop.App.Converters;

/// <summary>
/// Visible when the bound boolean is false.
/// </summary>
/// <remarks>
/// The framework's <see cref="BooleanToVisibilityConverter"/> has no inverse
/// mode - <c>ConverterParameter</c> is ignored. Rather than invert the boolean
/// in the view model, which would mean exposing a second, negative property
/// for every condition, this handles the "show the placeholder when there is
/// nothing to show" case directly.
/// </remarks>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}
