using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public sealed class RecoveryRecordArchiverTests
{
    private static IconPosition[] CurrentIcons => [new("Current", 123, 456), new("New", 321, 654), new("Manual", 777, 888)];

    [Fact]
    public void ArchivePreservesOldRecordAndSnapshotAndSavesCurrentLayoutWithoutDesktopMutations()
    {
        using var harness = ArchiveHarness.Create();
        string oldSnapshot = harness.CreateLayoutSnapshot();
        harness.SaveSession(snapshotPath: oldSnapshot);
        byte[] record = File.ReadAllBytes(harness.Sessions.FilePath);
        byte[] snapshot = File.ReadAllBytes(oldSnapshot);
        var archiver = new RecoveryRecordArchiver(harness.Paths, harness.Sessions, harness.Layouts, harness.Locations,
            _ => CurrentIcons, () => false, () => MonitorTopology.Empty);

        var result = archiver.Archive();

        Assert.False(harness.Sessions.Exists);
        Assert.Equal(record, File.ReadAllBytes(result.RecordPath));
        Assert.Equal(snapshot, File.ReadAllBytes(oldSnapshot));
        Assert.Equal(3, result.IconCount);
        var saved = harness.Layouts.Load(Path.GetFileNameWithoutExtension(result.CurrentLayoutPath));
        Assert.Equal(CurrentIcons, saved!.Icons);
    }

    [Theory]
    [InlineData("meeting")]
    [InlineData("different")]
    [InlineData("wallpaper")]
    [InlineData("public")]
    [InlineData("unreadable")]
    [InlineData("missing-original")]
    [InlineData("future-schema")]
    public void UnfinishedOrUnusableRecoveryRefusesBeforeReadingIconsOrChangingRecord(string fault)
    {
        using var harness = ArchiveHarness.Create();
        var session = harness.SaveSession(wallpaperChanged: fault == "wallpaper");
        if (fault == "meeting") harness.Locations.ResolvedPath = harness.MeetingDesktop;
        if (fault == "different") harness.Locations.ResolvedPath = Path.Combine(harness.Root, "Other");
        if (fault == "missing-original") Directory.Delete(harness.OriginalDesktop);
        if (fault == "unreadable") File.WriteAllText(harness.Sessions.FilePath, "{bad json");
        if (fault == "future-schema") harness.Sessions.Save(session with { SchemaVersion = 999 });
        byte[] record = File.ReadAllBytes(harness.Sessions.FilePath);
        bool readIcons = false;
        var archiver = new RecoveryRecordArchiver(harness.Paths, harness.Sessions, harness.Layouts, harness.Locations,
            _ => { readIcons = true; return CurrentIcons; }, () => fault == "public", () => MonitorTopology.Empty);

        Assert.Throws<InvalidOperationException>(() => archiver.Archive());

        Assert.False(readIcons);
        Assert.Equal(record, File.ReadAllBytes(harness.Sessions.FilePath));
        Assert.Empty(harness.Layouts.List());
    }

    [Theory]
    [InlineData("changed-record")]
    [InlineData("changed-desktop")]
    [InlineData("new-public-files")]
    [InlineData("capture-failed")]
    [InlineData("cancelled")]
    public void FailureOrChangedStateDuringCaptureNeverRetiresTheRecord(string fault)
    {
        using var harness = ArchiveHarness.Create();
        var session = harness.SaveSession();
        using var cancellation = new CancellationTokenSource();
        int publicChecks = 0;
        var archiver = new RecoveryRecordArchiver(harness.Paths, harness.Sessions, harness.Layouts, harness.Locations, _ =>
        {
            if (fault == "changed-record") harness.Sessions.Save(session with { CapturedIconCount = 999 });
            if (fault == "changed-desktop") harness.Locations.ResolvedPath = harness.MeetingDesktop;
            if (fault == "capture-failed") throw new DesktopViewVerificationException("Not original view.");
            if (fault == "cancelled") cancellation.Cancel();
            return CurrentIcons;
        }, () => ++publicChecks > 1 && fault == "new-public-files", () => MonitorTopology.Empty);

        Assert.ThrowsAny<Exception>(() => archiver.Archive(cancellation.Token));

        Assert.True(harness.Sessions.Exists);
        Assert.Equal(fault == "changed-record" ? 999 : session.CapturedIconCount, harness.Sessions.Load()!.CapturedIconCount);
        Assert.Empty(harness.Layouts.List());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedBackupWriteOrArchiveMoveKeepsOriginalRecord(bool failLayout)
    {
        using var harness = ArchiveHarness.Create();
        harness.SaveSession();
        byte[] record = File.ReadAllBytes(harness.Sessions.FilePath);
        var layouts = harness.Layouts;
        string blockingFile = Path.Combine(harness.Root, "blocking-file");
        File.WriteAllText(blockingFile, "do not overwrite");
        if (failLayout)
        {
            string brokenLayouts = Path.Combine(harness.Root, "BrokenLayouts");
            layouts = new LayoutStore(brokenLayouts);
            Directory.Delete(brokenLayouts);
            File.WriteAllText(brokenLayouts, "do not overwrite");
        }
        else File.WriteAllText(Path.Combine(harness.Paths.RecoveryDirectory, "ArchivedSessions"), "do not overwrite");
        var archiver = new RecoveryRecordArchiver(harness.Paths, harness.Sessions, layouts, harness.Locations,
            _ => CurrentIcons, () => false, () => MonitorTopology.Empty);

        Assert.ThrowsAny<IOException>(() => archiver.Archive());

        Assert.Equal(record, File.ReadAllBytes(harness.Sessions.FilePath));
        Assert.Equal("do not overwrite", File.ReadAllText(blockingFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriterAtFinalRenameCannotReplaceOrModifyVerifiedRecord(bool replace)
    {
        using var harness = ArchiveHarness.Create();
        harness.SaveSession();
        byte[] original = File.ReadAllBytes(harness.Sessions.FilePath);
        string replacement = Path.Combine(harness.Root, "replacement.json");
        byte[] changed = System.Text.Encoding.UTF8.GetBytes("a different recovery record");
        File.WriteAllBytes(replacement, changed);
        bool writerSucceeded = false;
        var archiver = new RecoveryRecordArchiver(harness.Paths, harness.Sessions, harness.Layouts, harness.Locations,
            _ => CurrentIcons, () => false, () => MonitorTopology.Empty, () =>
            {
                // Raw file I/O deliberately bypasses SessionStore and any cooperative lock.
                try
                {
                    if (replace) File.Move(replacement, harness.Sessions.FilePath, overwrite: true);
                    else File.WriteAllBytes(harness.Sessions.FilePath, changed);
                    writerSucceeded = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            });

        var result = archiver.Archive();

        Assert.Equal(original, File.ReadAllBytes(result.RecordPath));
        Assert.False(writerSucceeded);
        Assert.False(harness.Sessions.Exists);
        Assert.Equal(changed, File.ReadAllBytes(replacement));
    }

    [Fact]
    public void AlreadyOpenWriterRefusesArchiveAndKeepsActiveRecord()
    {
        using var harness = ArchiveHarness.Create();
        harness.SaveSession();
        byte[] original = File.ReadAllBytes(harness.Sessions.FilePath);
        using var writer = new FileStream(harness.Sessions.FilePath, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        var archiver = new RecoveryRecordArchiver(harness.Paths, harness.Sessions, harness.Layouts, harness.Locations,
            _ => CurrentIcons, () => false, () => MonitorTopology.Empty);

        Assert.ThrowsAny<IOException>(() => archiver.Archive());

        writer.Dispose();
        Assert.Equal(original, File.ReadAllBytes(harness.Sessions.FilePath));
        Assert.Empty(Directory.GetFiles(harness.Paths.RecoveryDirectory, "session-*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void CancellationAtFinalRenameKeepsActiveRecord()
    {
        using var harness = ArchiveHarness.Create();
        harness.SaveSession();
        byte[] original = File.ReadAllBytes(harness.Sessions.FilePath);
        using var cancellation = new CancellationTokenSource();
        var archiver = new RecoveryRecordArchiver(harness.Paths, harness.Sessions, harness.Layouts, harness.Locations,
            _ => CurrentIcons, () => false, () => MonitorTopology.Empty, cancellation.Cancel);

        Assert.ThrowsAny<OperationCanceledException>(() => archiver.Archive(cancellation.Token));

        Assert.Equal(original, File.ReadAllBytes(harness.Sessions.FilePath));
    }

    [Fact]
    public void ReplacementAfterLayoutBackupIsKeptActiveAndNeverArchived()
    {
        using var harness = ArchiveHarness.Create();
        var original = harness.SaveSession();
        byte[]? replacement = null;
        int checks = 0;
        var archiver = new RecoveryRecordArchiver(harness.Paths, harness.Sessions, harness.Layouts, harness.Locations,
            _ => CurrentIcons, () =>
            {
                if (++checks == 3)
                {
                    harness.Sessions.Save(original with { CapturedIconCount = 999 });
                    replacement = File.ReadAllBytes(harness.Sessions.FilePath);
                }
                return false;
            }, () => MonitorTopology.Empty);

        Assert.Throws<InvalidOperationException>(() => archiver.Archive());

        Assert.NotNull(replacement);
        Assert.Equal(replacement, File.ReadAllBytes(harness.Sessions.FilePath));
        Assert.Single(harness.Layouts.List());
        Assert.Empty(Directory.GetFiles(harness.Paths.RecoveryDirectory, "session-*.json", SearchOption.AllDirectories));
    }

    private sealed class ArchiveHarness : IDisposable
    {
        private ArchiveHarness()
        {
            Root = Path.Combine(Path.GetTempPath(), "MeetingDesktopArchiveTests", Guid.NewGuid().ToString("N"));
            Paths = AppPaths.Resolve(["--data-dir", Root]);
            Paths.EnsureCreated();
            OriginalDesktop = Directory.CreateDirectory(Path.Combine(Root, "Original")).FullName;
            MeetingDesktop = Directory.CreateDirectory(Path.Combine(Root, "Meeting")).FullName;
            Sessions = new SessionStore(Paths.SessionFilePath);
            Layouts = new LayoutStore(Paths.LayoutsDirectory);
            Locations = new FakeDesktopLocationService(OriginalDesktop);
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public string OriginalDesktop { get; }
        public string MeetingDesktop { get; }
        public SessionStore Sessions { get; }
        public LayoutStore Layouts { get; }
        public FakeDesktopLocationService Locations { get; }
        public static ArchiveHarness Create() => new();

        public string CreateLayoutSnapshot() => Layouts.SaveAutomatic(new LayoutSnapshot
        {
            Topology = MonitorTopology.Empty,
            Icons = [new("Old", 1, 2)],
        }, DateTimeOffset.UnixEpoch);

        public SessionInfo SaveSession(bool wallpaperChanged = false, string? snapshotPath = null)
        {
            var session = new SessionInfo
            {
                OriginalDesktopPath = OriginalDesktop,
                MeetingDesktopPathAtSwap = MeetingDesktop,
                AutoLayoutSnapshotPath = snapshotPath,
                WallpaperChanged = wallpaperChanged,
                TopologyAtSwap = MonitorTopology.Empty,
            };
            Sessions.Save(session);
            return session;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
