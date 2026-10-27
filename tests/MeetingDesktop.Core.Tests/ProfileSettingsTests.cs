using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class ProfileSettingsTests
{
    [Theory]
    [InlineData("null", 0)]
    [InlineData("[]", 0)]
    [InlineData("[null]", 0)]
    [InlineData("[{}]", 0)]
    [InlineData("[{\"Name\":\"missing folder\"}]", 0)]
    [InlineData("[{\"Folder\":\"C:\\\\Meeting\"}]", 0)]
    [InlineData("[{\"Name\":\" \",\"Folder\":\"C:\\\\Meeting\"}]", 0)]
    [InlineData("[null,{}, {\"Name\":\"Meeting\",\"Folder\":\"C:\\\\Meeting\"}]", 1)]
    public void MalformedProfileEntriesDoNotDiscardValidPreferencesOrRewriteTheFile(string profiles, int expectedCount)
    {
        string file = Path.Combine(Path.GetTempPath(), "profiles-invalid-" + Guid.NewGuid().ToString("N") + ".json");
        string json = "{\"SchemaVersion\":1,\"Language\":\"English\",\"Theme\":\"Light\",\"Profiles\":" + profiles + "}";
        try
        {
            File.WriteAllText(file, json);
            var loaded = new SettingsStore(file).Load();
            Assert.NotNull(loaded.Profiles);
            Assert.Equal(expectedCount, loaded.Profiles.Count);
            Assert.Equal(LanguagePreference.English, loaded.Language);
            Assert.Equal(ThemePreference.Light, loaded.Theme);
            if (expectedCount > 0) Assert.Equal("Meeting", loaded.Profiles[0].Name);
            Assert.Equal(json, File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void NamedProfilesRoundTripWithoutLosingOtherPreferences()
    {
        string root = Path.Combine(Path.GetTempPath(), "desktop-profile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(Path.Combine(root, "settings.json"));
            store.Save(new AppSettings
            {
                Language = LanguagePreference.SimplifiedChinese,
                SmoothDesktopTransition = false,
                Profiles = [new DesktopProfile("Meeting", Path.Combine(root, "meeting"), null),
                    new DesktopProfile("Recording", Path.Combine(root, "recording"), Path.Combine(root, "background.png"))],
            });
            var result = store.Load();
            Assert.Equal(2, result.Profiles.Count);
            Assert.Equal("Recording", result.Profiles[1].Name);
            Assert.Equal(LanguagePreference.SimplifiedChinese, result.Language);
            Assert.False(result.SmoothDesktopTransition);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OlderSettingsHaveAnEmptyProfileList()
    {
        string path = Path.Combine(Path.GetTempPath(), "desktop-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"SchemaVersion\":1,\"Language\":\"English\"}");
            Assert.Empty(new SettingsStore(path).Load().Profiles);
        }
        finally { File.Delete(path); }
    }
}
