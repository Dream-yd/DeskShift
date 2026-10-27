using System.IO;
using MeetingDesktop.App.Services;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Tests;

public sealed class StartupDataTests
{
    [Fact]
    public void OpensOnlyTheExplicitPrivateDataDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetingDesktopStartupTests", Guid.NewGuid().ToString("N"));
        try
        {
            var data = StartupData.Open(["--data-dir", root], out var failure);
            Assert.Null(failure);
            Assert.NotNull(data);
            Assert.Equal(root, data.Paths.DataDirectory);
            Assert.True(Directory.Exists(data.Paths.LayoutsDirectory));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ConflictingLayoutsPathReportsOneFailureInTheStoredLanguageAndKeepsAllData()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetingDesktopStartupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string settings = "{\"Language\":\"English\"}";
            File.WriteAllText(Path.Combine(root, "settings.json"), settings);
            File.WriteAllText(Path.Combine(root, "Layouts"), "keep me");
            var data = StartupData.Open(["--data-dir", root], out var failure);
            Assert.Null(data);
            Assert.NotNull(failure);
            Assert.Equal(LanguagePreference.English, failure.Language);
            Assert.False(string.IsNullOrWhiteSpace(failure.Detail));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(root, "Layouts")));
            Assert.Equal(settings, File.ReadAllText(Path.Combine(root, "settings.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InvalidExplicitPathDoesNotFallBackToTheUsersNormalData()
    {
        var data = StartupData.Open(["--data-dir", "\0invalid"], out var failure);
        Assert.Null(data);
        Assert.NotNull(failure);
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":999,\"Future\":\"keep\"}")]
    [InlineData("{broken")]
    public void UnsafeSettingsStopStartupAndPreserveTheOriginal(string settings)
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetingDesktopStartupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, settings);
            var data = StartupData.Open(["--data-dir", root], out var failure);
            Assert.Null(data);
            Assert.NotNull(failure);
            Assert.Equal(settings, File.ReadAllText(path));
            Assert.False(Directory.Exists(Path.Combine(root, "Layouts")));
        }
        finally { Directory.Delete(root, true); }
    }
}
