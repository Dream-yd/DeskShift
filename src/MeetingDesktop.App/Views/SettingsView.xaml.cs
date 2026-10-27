using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeetingDesktop.App.ViewModels;

namespace MeetingDesktop.App.Views;

/// <summary>
/// The Settings page. See <see cref="PrepareView"/> for why the constructor matters.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // The language picker's binding is two-way and its setter has a side
        // effect, so it stays inert until the tree is built. Loaded is the
        // first moment the combo box has settled on the value the view model
        // handed it, and therefore the first moment a change can only have
        // come from the user.
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Detach: Loaded fires again if the page is re-parented, and there is
        // nothing to do a second time.
        Loaded -= OnLoaded;

        (DataContext as SettingsViewModel)?.BeginTrackingUserLanguageChanges();
        (DataContext as SettingsViewModel)?.BeginTrackingQuickAccessChanges();
    }

    private void OnMeetingDesktopFolderLostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        (DataContext as SettingsViewModel)?.CommitMeetingDesktopPath();
}
