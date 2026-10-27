using MeetingDesktop.Core;

namespace MeetingDesktop.Core.Tests;

public sealed class LegacyRecoveryRecordTests
{
    [Fact]
    public void ImportHasNoInventedLayoutAndDoesNotModifyRecord()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string original = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
        string current = Directory.CreateDirectory(Path.Combine(root, "meeting")).FullName;
        string record = Path.Combine(root, "original_desktop_path.txt");
        File.WriteAllText(record, original + Environment.NewLine);
        var result = LegacyRecoveryRecord.Read(record, current);
        Assert.Equal(original, result.OriginalDesktopPath);
        Assert.Equal(current, result.MeetingDesktopPathAtSwap);
        Assert.Null(result.AutoLayoutSnapshotPath);
        Assert.Equal(0, result.CapturedIconCount);
        Assert.False(result.WallpaperChanged);
        Assert.Equal(original + Environment.NewLine, File.ReadAllText(record));
    }

    [Theory]
    [InlineData("relative-folder")]
    [InlineData("")]
    [InlineData("C:\\missing-legacy-folder-27555")]
    public void RejectsUnusableOriginalFolders(string original)
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        string record = Path.Combine(root, "original_desktop_path.txt");
        File.WriteAllText(record, original);
        Assert.Throws<ArgumentException>(() => LegacyRecoveryRecord.Read(record, root));
    }

    [Fact]
    public void RejectsCurrentFolderIncludingTrailingSeparator()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        string record = Path.Combine(root, "original_desktop_path.txt");
        File.WriteAllText(record, root + Path.DirectorySeparatorChar);
        Assert.Throws<ArgumentException>(() => LegacyRecoveryRecord.Read(record, root));
    }
}
