using System.IO;
using System.Windows;
using MeetingDesktop.Core.Models;
using Microsoft.Win32;

namespace MeetingDesktop.App.Services;

internal static class ThemeManager
{
    private static ResourceDictionary? _darkPalette;
    private static bool _initialized;
    internal static ThemePreference Preference { get; private set; } = ThemePreference.Dark;
    internal static bool IsDark { get; private set; } = true;
    internal static event EventHandler? Changed;

    internal static void Initialize(ThemePreference preference)
    {
        if (!_initialized)
        {
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            _initialized = true;
        }
        Apply(preference);
    }

    internal static void Apply(ThemePreference preference)
    {
        Preference = preference;
        SetResolvedTheme(ThemePreferencePolicy.IsDark(preference, ReadSystemUsesLightTheme()));
    }

    internal static void RefreshSystem()
        => RefreshSystem(ReadSystemUsesLightTheme());

    internal static void RefreshSystem(bool? systemUsesLightTheme)
    {
        if (Preference == ThemePreference.System)
            SetResolvedTheme(ThemePreferencePolicy.IsDark(Preference, systemUsesLightTheme));
    }

    private static void SetResolvedTheme(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.VerifyAccess();
        bool changed = IsDark != dark;
        IsDark = dark;
        if (dark && _darkPalette is null)
        {
            _darkPalette = new ResourceDictionary
            {
                Source = new Uri("/MeetingDesktop;component/Resources/Theme/Dark.xaml", UriKind.Relative),
            };
            app.Resources.MergedDictionaries.Add(_darkPalette);
            changed = true;
        }
        else if (!dark && _darkPalette is not null)
        {
            app.Resources.MergedDictionaries.Remove(_darkPalette);
            _darkPalette = null;
            changed = true;
        }
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool? ReadSystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value is 0 or 1 ? value == 1 : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.HasShutdownStarted)
            app.Dispatcher.BeginInvoke(new Action(RefreshSystem));
    }

    internal static void Shutdown()
    {
        if (_initialized) SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _initialized = false;
    }
}
