using System.Windows.Controls;

namespace MeetingDesktop.App.Views;

/// <summary>
/// The Prepare page.
/// </summary>
/// <remarks>
/// The constructor is not optional boilerplate. The XAML build generates
/// <c>InitializeComponent</c> on this class but no constructor, so without
/// this the type compiles and the page renders empty when instantiated - a
/// failure with no error message attached to it.
/// </remarks>
public partial class PrepareView : UserControl
{
    public PrepareView() => InitializeComponent();
}
