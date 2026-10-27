namespace MeetingDesktop.Core.Tests;

public sealed class MeetingFolderArchiveTests
{
    [Fact]
    public void ArchiveCopiesNestedFilesAndPreservesOriginals()
    {
        string root = Path.Combine(Path.GetTempPath(), "archive-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string source = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(source, "nested"));
            File.WriteAllText(Path.Combine(source, "nested", "slides.txt"), "original");
            string archive = MeetingFolderArchive.Copy(source, Path.Combine(root, "destination"));
            Assert.Equal("original", File.ReadAllText(Path.Combine(archive, "nested", "slides.txt")));
            Assert.True(File.Exists(Path.Combine(source, "nested", "slides.txt")));
            string second = MeetingFolderArchive.Copy(source, Path.Combine(root, "destination"));
            Assert.NotEqual(archive, second);
            Assert.Throws<ArgumentException>(() => MeetingFolderArchive.Copy(source, Path.Combine(source, "recursive")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
