namespace MeetingDesktop.Core.Models;

public enum ThemePreference { System, Light, Dark }

/// <summary>Application appearance only; never changes Windows personalization.</summary>
public static class ThemePreferencePolicy
{
    public static bool IsDark(ThemePreference preference, bool? systemUsesLightTheme) => preference switch
    {
        ThemePreference.Light => false,
        ThemePreference.System => systemUsesLightTheme != true,
        _ => true,
    };
}
