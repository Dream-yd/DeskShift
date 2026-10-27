using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class PublicDesktopSafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MeetingDesktopSafety", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Destination => Path.Combine(root, "destination");
    public PublicDesktopSafetyTests() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Destination); }
    [Fact]
    public void CollisionPreservesBothAndSkipsOccupiedUniqueNames()
    {
        File.WriteAllText(Path.Combine(Source, "App.lnk"), "original");
        File.WriteAllText(Path.Combine(Destination, "App.lnk"), "new");
        File.WriteAllText(Path.Combine(Destination, "App (restored 1).lnk"), "other");
        var result = PublicDesktopService.MoveShortcuts(Source, Destination);
        Assert.Equal(1, result.MovedCount);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("original", File.ReadAllText(conflict.PreservedPath));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Destination, "App.lnk")));
        Assert.Equal("other", File.ReadAllText(Path.Combine(Destination, "App (restored 1).lnk")));
        Assert.Equal(0, PublicDesktopService.MoveShortcuts(Source, Destination).MovedCount);
    }
    [Fact]
    public void PrecheckListsResidualEntriesWithoutRecursingOrChangingFiles()
    {
        File.WriteAllText(Path.Combine(Source, "App.LNK"), "link");
        File.WriteAllText(Path.Combine(Source, "website.url"), "url");
        File.WriteAllText(Path.Combine(Source, "notes.txt"), "notes");
        File.WriteAllText(Path.Combine(Source, "desktop.ini"), "config");
        Directory.CreateDirectory(Path.Combine(Source, "folder.lnk"));
        var result = PublicDesktopService.Precheck(Source);
        Assert.Equal(1, result.ShortcutCount);
        Assert.Equal(3, result.ResidualItems.Count);
        Assert.Contains(result.ResidualItems, i => i.Name == "folder.lnk" && i.IsDirectory);
        Assert.Equal(5, Directory.GetFileSystemEntries(Source).Length);
    }
    [Fact]
    public void IdenticalCollisionAlsoPreservesBoth()
    {
        File.WriteAllText(Path.Combine(Source, "App.lnk"), "same");
        File.WriteAllText(Path.Combine(Destination, "App.lnk"), "same");
        Assert.Single(PublicDesktopService.MoveShortcuts(Source, Destination).Conflicts);
        Assert.Equal(2, Directory.GetFiles(Destination).Length);
    }
    [Fact]
    public void DirectoryCollisionIsPreserved()
    {
        File.WriteAllText(Path.Combine(Source, "App.lnk"), "original");
        Directory.CreateDirectory(Path.Combine(Destination, "App.lnk"));
        var conflict = Assert.Single(PublicDesktopService.MoveShortcuts(Source, Destination).Conflicts);
        Assert.True(Directory.Exists(conflict.RequestedPath));
        Assert.Equal("original", File.ReadAllText(conflict.PreservedPath));
    }
    [Fact]
    public void PartialFailureReportsCompletedConflictsAndCanBeRetried()
    {
        File.WriteAllText(Path.Combine(Source, "A.lnk"), "original");
        File.WriteAllText(Path.Combine(Destination, "A.lnk"), "new");
        string locked = Path.Combine(Source, "B.lnk");
        File.WriteAllText(locked, "locked");
        using (var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<PublicDesktopMoveException>(() => PublicDesktopService.MoveShortcuts(Source, Destination));
            Assert.Equal(1, error.MovedCount);
            Assert.Equal(1, error.RemainingCount);
            Assert.Equal(locked, error.FailedPath);
            Assert.Single(error.CompletedConflicts);
        }
        var retry = PublicDesktopService.MoveShortcuts(Source, Destination);
        Assert.Equal(1, retry.MovedCount);
        Assert.Empty(retry.Conflicts);
        Assert.Equal(3, Directory.GetFiles(Destination).Length);
    }
    [Fact]
    public void MissingDirectoryPrecheckDoesNotCreateIt()
    {
        string missing = Path.Combine(root, "missing");
        var check = PublicDesktopService.Precheck(missing);
        Assert.Empty(check.ResidualItems);
        Assert.Equal(0, check.ShortcutCount);
        Assert.False(Directory.Exists(missing));
    }
    [Fact]
    public void EmptyMoveDoesNotCreateBackupAndDoesNotMoveOtherFiles()
    {
        File.WriteAllText(Path.Combine(Source, "notes.txt"), "notes");
        string missing = Path.Combine(root, "missing");
        Assert.Equal(0, PublicDesktopService.MoveShortcuts(Source, missing).MovedCount);
        Assert.False(Directory.Exists(missing));
        Assert.Equal("notes", File.ReadAllText(Path.Combine(Source, "notes.txt")));
    }
    public void Dispose() { Directory.Delete(root, true); }
}

public sealed class CompatibilityIdentityTests
{
    [Fact]
    public void EnrichmentUsesCaptionAndCoordinatesInsteadOfEnumerationOrder()
    {
        IconPosition[] list = [new("Same", 10, 20), new("Same", 30, 40)];
        IconPosition[] shell = [new("Same", 30, 40) { Identity = "second" }, new("Same", 10, 20) { Identity = "first" }];
        var enriched = CompatibilityIconIdentity.Enrich(list, shell);
        Assert.Equal("first", enriched[0].Identity);
        Assert.Equal("second", enriched[1].Identity);
        Assert.Equal(2, ShellIconMatcher.Match(shell, enriched).Count);
    }
    [Fact]
    public void AmbiguousCoordinatesDoNotInventIdentity()
    {
        IconPosition[] list = [new("Same", 10, 20), new("Same", 10, 20)];
        IconPosition[] shell = [new("Same", 10, 20) { Identity = "first" }];
        Assert.All(CompatibilityIconIdentity.Enrich(list, shell), icon => Assert.Null(icon.Identity));
        Assert.Empty(ShellIconMatcher.Match(list, list));
    }
}
