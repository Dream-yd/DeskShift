using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class ThemePreferenceTests
{
    [Theory]
    [InlineData(ThemePreference.Dark, true, true)]
    [InlineData(ThemePreference.Dark, false, true)]
    [InlineData(ThemePreference.Light, true, false)]
    [InlineData(ThemePreference.Light, false, false)]
    [InlineData(ThemePreference.System, true, false)]
    [InlineData(ThemePreference.System, false, true)]
    [InlineData(ThemePreference.System, null, true)]
    [InlineData((ThemePreference)99, true, true)]
    public void ExplicitPreferenceOverridesSystemAndUnknownSystemFallsBackToDark(
        ThemePreference preference, bool? systemLight, bool expected) =>
        Assert.Equal(expected, ThemePreferencePolicy.IsDark(preference, systemLight));

    [Theory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.System)]
    public void ThemeRoundTripsWithoutLosingOtherSettings(ThemePreference preference)
    {
        string path = Path.Combine(Path.GetTempPath(), "theme-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { Theme = preference, Language = LanguagePreference.SimplifiedChinese,
                MeetingDesktopPath = @"D:\Meetings", CaptureReturnPreview = false });
            var saved = store.Load();
            Assert.Equal(preference, saved.Theme);
            Assert.Equal(LanguagePreference.SimplifiedChinese, saved.Language);
            Assert.Equal(@"D:\Meetings", saved.MeetingDesktopPath);
            Assert.False(saved.CaptureReturnPreview);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OlderSettingsDefaultToDarkWithoutLosingPreferences()
    {
        string path = Path.Combine(Path.GetTempPath(), "theme-old-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"SchemaVersion\":1,\"Language\":\"English\",\"CaptureReturnPreview\":false}");
            var saved = new SettingsStore(path).Load();
            Assert.Equal(ThemePreference.Dark, saved.Theme);
            Assert.Equal(LanguagePreference.English, saved.Language);
            Assert.False(saved.CaptureReturnPreview);
        }
        finally { File.Delete(path); }
    }
}
