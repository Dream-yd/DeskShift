using System.Globalization;
using System.Windows.Data;

namespace MeetingDesktop.App.Converters;

/// <summary>Stacks layout history and snapshot details when the page is narrow.</summary>
public sealed class LayoutSearchNarrowConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && width < 720;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
