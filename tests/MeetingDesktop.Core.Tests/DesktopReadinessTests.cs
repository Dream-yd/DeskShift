namespace MeetingDesktop.Core.Tests;

public sealed class DesktopReadinessTests
{
    [Fact]
    public void InspectionCountsOnlyTopLevelFilesAndDoesNotChangeContents()
    {
        string root = Path.Combine(Path.GetTempPath(), "readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "folder"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "file.bin"), new byte[13]);
            File.WriteAllBytes(Path.Combine(root, "folder", "nested.bin"), new byte[21]);
            var facts = DesktopReadiness.Inspect(root);
            Assert.Equal(1, facts.FileCount);
            Assert.Equal(1, facts.FolderCount);
            Assert.Equal(13, facts.TopLevelBytes);
            Assert.Equal(13, new FileInfo(Path.Combine(root, "file.bin")).Length);
            Assert.Equal(21, new FileInfo(Path.Combine(root, "folder", "nested.bin")).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InspectionDoesNotCreateAMissingTarget()
    {
        string target = Path.Combine(Path.GetTempPath(), "readiness-missing-" + Guid.NewGuid().ToString("N"));
        var facts = DesktopReadiness.Inspect(target);
        Assert.Equal(0, facts.FileCount);
        Assert.Equal(0, facts.FolderCount);
        Assert.Equal(0, facts.TopLevelBytes);
        Assert.False(Directory.Exists(target));
    }
}
