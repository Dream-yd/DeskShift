using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MeetingDesktop.App.ViewModels;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taking a dependency on a MVVM toolkit. This app has
/// five views and about forty properties; a source generator would save a few
/// lines of boilerplate and cost every contributor a package restore and a
/// version to keep current.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Assigns <paramref name="value"/> and raises a change notification when
    /// it differs. Returns whether anything changed, so callers can chain
    /// dependent notifications.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
