using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class SettingsProtectionTests
{
    [Fact]
    public void StartupValidatesAndLoadsTheSameRead()
    {
        int reads = 0;
        var store = new SettingsStore(@"C:\SettingsRace\settings.json", _ =>
        {
            reads++;
            return reads == 1 ? "{\"SchemaVersion\":2,\"MeetingDesktopPath\":\"C:\\\\Meeting\"}" : "{broken";
        });
        Assert.Equal(@"C:\Meeting", store.LoadForStartup().MeetingDesktopPath);
        Assert.Equal(1, reads);
    }
    [Theory]
    [InlineData("{\"SchemaVersion\":999,\"Unknown\":\"keep\"}")]
    [InlineData("{broken")]
    [InlineData("")]
    [InlineData("null")]
    public void RefusesStartupAndSaveWithoutModifyingUnsupportedFile(string contents)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MeetingDesktopSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "settings.json");
        try
        {
            File.WriteAllText(file, contents);
            var store = new SettingsStore(file);
            AppSettings defaults = store.Load();
            Assert.Throws<IOException>(() => store.LoadForStartup());
            Assert.Throws<IOException>(() => store.Save(defaults));
            Assert.Equal(contents, File.ReadAllText(file));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MissingAndSupportedFilesCanBeUsedNormally()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MeetingDesktopSettings-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(file);
            Assert.NotNull(store.LoadForStartup());
            store.Save(new AppSettings { MeetingDesktopPath = @"C:\Meeting" });
            Assert.Equal(@"C:\Meeting", store.LoadForStartup().MeetingDesktopPath);
            store.Save(store.Load() with { MeetingDesktopPath = @"C:\Other" });
            Assert.Equal(@"C:\Other", store.Load().MeetingDesktopPath);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
