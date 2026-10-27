using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class ResponsivePreferencesTests
{
    [Fact]
    public void LegacyQuickConfirmationDoesNotSurviveTheExplicitNoPromptUpgrade()
    {
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "MeetingDesktopPreferences", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string file = Path.Combine(directory, "settings.json");
            const string json = "{\"SchemaVersion\":1,\"ConfirmQuickSwitch\":true,\"Language\":\"English\",\"MeetingDesktopPath\":\"E:\\\\Meeting\"}";
            File.WriteAllText(file, json);
            var loaded = new SettingsStore(file).Load();
            Assert.False(loaded.ConfirmQuickSwitch);
            Assert.Equal(SwitchingExperience.Responsive, loaded.SwitchingExperience);
            Assert.Equal(LanguagePreference.English, loaded.Language);
            Assert.Equal(json, File.ReadAllText(file));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NewExplicitConfirmationAndCompatibilityChoicesRemainPersistent()
    {
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "MeetingDesktopPreferences", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            store.Save(new AppSettings { ConfirmQuickSwitch = true, SwitchingExperience = SwitchingExperience.Compatibility });
            var loaded = store.Load();
            Assert.True(loaded.ConfirmQuickSwitch);
            Assert.Equal(SwitchingExperience.Compatibility, loaded.SwitchingExperience);
        }
        finally { Directory.Delete(directory, true); }
    }
}
