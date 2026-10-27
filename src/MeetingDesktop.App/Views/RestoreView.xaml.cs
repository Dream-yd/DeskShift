using System.Windows;
using System.Windows.Controls;

namespace MeetingDesktop.App.Views;

public partial class RestoreView : UserControl
{
    public RestoreView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            bool wide = ActualWidth >= 720;
            RestoreColumns.ColumnDefinitions[1].Width = new GridLength(wide ? 18 : 0);
            RestoreColumns.ColumnDefinitions[2].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(RestorePreviewCard, wide ? 2 : 0);
            Grid.SetRow(RestorePreviewCard, wide ? 0 : 1);
            RestorePreviewCard.Margin = wide ? new Thickness(0) : new Thickness(0, 16, 0, 0);
        };
    }
}
