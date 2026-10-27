using System.Text;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "MeetingDesktopSessionTests", Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void ArchiveCollisionPreservesBothRecordsAndReleasesHandle()
    {
        var store = CreateStore();
        byte[] original = File.ReadAllBytes(store.FilePath);
        string archive = Path.Combine(_root, "existing.json");
        File.WriteAllText(archive, "existing archive");

        Assert.Throws<IOException>(() => store.ArchiveVerified(original, archive));

        Assert.Equal(original, File.ReadAllBytes(store.FilePath));
        Assert.Equal("existing archive", File.ReadAllText(archive));
        store.Save(store.Load()! with { CapturedIconCount = 123 });
        Assert.Equal(123, store.Load()!.CapturedIconCount);
    }

    [Fact]
    public void MismatchRefusesArchiveAndReleasesHandleWithoutChangingActiveRecord()
    {
        var store = CreateStore();
        byte[] reviewed = File.ReadAllBytes(store.FilePath);
        store.Save(store.Load()! with { CapturedIconCount = 999 });
        byte[] active = File.ReadAllBytes(store.FilePath);
        string archive = Path.Combine(_root, "archive.json");
        bool reachedRename = false;

        Assert.Throws<InvalidOperationException>(() => store.ArchiveVerified(reviewed, archive, () => reachedRename = true));

        Assert.False(reachedRename);
        Assert.False(File.Exists(archive));
        Assert.Equal(active, File.ReadAllBytes(store.FilePath));
        // A retry after failure can acquire and rename the same active record.
        store.ArchiveVerified(active, archive);
        Assert.False(store.Exists);
        Assert.Equal(active, File.ReadAllBytes(archive));
    }

    [Fact]
    public void NativeRenameFailureKeepsActiveRecord()
    {
        var store = CreateStore();
        byte[] original = File.ReadAllBytes(store.FilePath);

        Assert.Throws<IOException>(() => store.ArchiveVerified(original, Path.Combine(_root, "missing", "archive.json")));

        Assert.Equal(original, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public void VerifiedReaderDoesNotPreventHandleRename()
    {
        var store = CreateStore();
        byte[] original = File.ReadAllBytes(store.FilePath);
        using var reader = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        string archive = Path.Combine(_root, "归档.json");

        store.ArchiveVerified(original, archive);

        using var contents = new MemoryStream();
        reader.CopyTo(contents);
        Assert.Equal(original, contents.ToArray());
        Assert.Equal(original, File.ReadAllBytes(archive));
        Assert.False(store.Exists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsingReviewedBytesPreservesBomSupportAndDoesNotRereadActivePath(bool utf16)
    {
        var store = CreateStore();
        string json = File.ReadAllText(store.FilePath);
        Encoding encoding = utf16 ? Encoding.Unicode : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(store.FilePath, json, encoding);
        byte[] reviewed = File.ReadAllBytes(store.FilePath);
        store.Save(store.Load()! with { CapturedIconCount = 999 });

        Assert.Equal(0, SessionStore.ReadRecord(reviewed)!.CapturedIconCount);
        Assert.Equal(999, store.Load()!.CapturedIconCount);
    }

    private SessionStore CreateStore()
    {
        var store = new SessionStore(Path.Combine(_root, "session.json"));
        store.Save(new SessionInfo
        {
            OriginalDesktopPath = Path.Combine(_root, "Original"),
            MeetingDesktopPathAtSwap = Path.Combine(_root, "Meeting"),
        });
        return store;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
