using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

// Every capture and asset is synthetic. Never invoke CaptureCurrent from tests.
public sealed class SafetyBackupExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "safety-backup-test-" + Guid.NewGuid().ToString("N"));

    public SafetyBackupExporterTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExportsLayoutTopologyRawPathsWallpaperShortcutsAndVerifiableManifest()
    {
        var wallpaper = Write("engine-temp.png", [1, 2, 3, 4]);
        var shortcut = Write("演示.LNK", [9, 8, 7]);
        var ignored = Write("private.txt", [6]);
        var capture = Capture(wallpaper, shortcut, ignored);
        string output = Path.Combine(_root, "backup.zip");
        var result = new SafetyBackupExporter(() => capture).Export(output);

        Assert.Empty(result.Warnings);
        Assert.True(result.WallpaperCopied);
        Assert.Equal(1, result.ShortcutCount);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(wallpaper));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(shortcut));
        Assert.Equal(new byte[] { 6 }, File.ReadAllBytes(ignored));
        using var zip = ZipFile.OpenRead(output);
        using var layout = ReadJson(zip, "layout.json");
        Assert.Equal(-1920, layout.RootElement.GetProperty("Topology").GetProperty("Monitors")[0].GetProperty("X").GetInt32());
        Assert.Equal("Synthetic icon", layout.RootElement.GetProperty("Icons")[0].GetProperty("Name").GetString());
        using var paths = ReadJson(zip, "desktop-paths.json");
        Assert.Equal(@"%SYNTHETIC_USER%\Desktop", paths.RootElement.GetProperty("UserShellFoldersRaw").GetString());
        Assert.Equal("", paths.RootElement.GetProperty("ShellFoldersRaw").GetString());
        using var state = ReadJson(zip, "wallpaper-state.json");
        Assert.Equal(wallpaper, state.RootElement.GetProperty("Path").GetString());
        Assert.Equal("22", state.RootElement.GetProperty("Style").GetString());
        Assert.Equal("1", state.RootElement.GetProperty("Tile").GetString());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ReadBytes(Assert.Single(zip.Entries, e => e.FullName.StartsWith("wallpaper/"))));
        Assert.Equal(new byte[] { 9, 8, 7 }, ReadBytes(Assert.Single(zip.Entries, e => e.FullName.StartsWith("public-desktop/"))));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("private.txt"));
        using var metadata = ReadJson(zip, "metadata.json");
        Assert.Contains("backup for manual recovery, not an automatic full-machine backup", metadata.RootElement.GetProperty("Purpose").GetString());
        Assert.False(metadata.RootElement.GetProperty("AutomaticRestoreSupported").GetBoolean());
        using var manifest = ReadJson(zip, "manifest.json");
        var entries = manifest.RootElement.GetProperty("Entries").EnumerateArray().ToArray();
        Assert.Equal(zip.Entries.Count - 1, entries.Length);
        foreach (var entry in entries)
        {
            byte[] bytes = ReadBytes(zip.GetEntry(entry.GetProperty("EntryName").GetString()!)!);
            Assert.Equal(bytes.Length, entry.GetProperty("Length").GetInt64());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), entry.GetProperty("Sha256").GetString());
        }
        Assert.DoesNotContain(Directory.GetFiles(_root), p => p.EndsWith(".tmp"));
    }

    [Fact]
    public void CopiedWallpaperSurvivesDisappearingTemporarySource()
    {
        string wallpaper = Write("ephemeral.png", [10, 20, 30]);
        string output = Path.Combine(_root, "backup.zip");
        new SafetyBackupExporter(() => Capture(wallpaper)).Export(output);
        File.Delete(wallpaper); // Simulates Wallpaper Engine removing its temporary image.
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(new byte[] { 10, 20, 30 }, ReadBytes(Assert.Single(zip.Entries, e => e.FullName.StartsWith("wallpaper/"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableOrDisappearingAssetsProduceWarningsWithoutPartialEntries(bool failsDuringRead)
    {
        string output = Path.Combine(_root, "backup.zip");
        var exporter = new SafetyBackupExporter(() => Capture("synthetic.png", "synthetic.lnk"),
            _ => failsDuringRead ? new FailingReadStream() : throw new UnauthorizedAccessException("Synthetic denial"));
        var result = exporter.Export(output);
        Assert.Equal(2, result.Warnings.Count);
        Assert.False(result.WallpaperCopied);
        Assert.Equal(0, result.ShortcutCount);
        using var zip = ZipFile.OpenRead(output);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("wallpaper/") || e.FullName.StartsWith("public-desktop/"));
        using var metadata = ReadJson(zip, "metadata.json");
        Assert.Equal(2, metadata.RootElement.GetProperty("Warnings").GetArrayLength());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void CapturedWarningsAndEmptyWallpaperAreReportedHonestly()
    {
        string output = Path.Combine(_root, "backup.zip");
        var result = new SafetyBackupExporter(() => Capture(null) with { Warnings = ["Synthetic public desktop unavailable"] }).Export(output);
        Assert.Single(result.Warnings);
        Assert.False(result.WallpaperCopied);
        using var zip = ZipFile.OpenRead(output);
        Assert.NotNull(zip.GetEntry("wallpaper-state.json"));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("wallpaper/"));
    }

    [Fact]
    public void ExistingOutputRequiresExplicitOverwriteAndCaptureIsNotInvoked()
    {
        string output = Write("existing.zip", [77]);
        bool captured = false;
        var exporter = new SafetyBackupExporter(() => { captured = true; return Capture(null); });
        Assert.Throws<IOException>(() => exporter.Export(output));
        Assert.False(captured);
        Assert.Equal(new byte[] { 77 }, File.ReadAllBytes(output));
    }

    [Fact]
    public void ExplicitOverwritePublishesCompleteZip()
    {
        string output = Write("existing.zip", [77]);
        new SafetyBackupExporter(() => Capture(null)).Export(output, overwriteConfirmed: true);
        using var zip = ZipFile.OpenRead(output);
        Assert.NotNull(zip.GetEntry("manifest.json"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void FailedAtomicPublicationPreservesLockedOutputAndUnrelatedTemp()
    {
        string output = Write("existing.zip", [77]);
        string unrelated = Write("unrelated.tmp", [88]);
        using (var locked = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exporter = new SafetyBackupExporter(() => Capture(null));
            Assert.Throws<IOException>(() => exporter.Export(output, overwriteConfirmed: true));
        }
        Assert.Equal(new byte[] { 77 }, File.ReadAllBytes(output));
        Assert.Equal(new byte[] { 88 }, File.ReadAllBytes(unrelated));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void CancellationBeforeExportDoesNotCaptureOrCreateOutput()
    {
        string output = Path.Combine(_root, "backup.zip");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exporter = new SafetyBackupExporter(() => throw new InvalidOperationException("Must not capture"));
        Assert.Throws<OperationCanceledException>(() => exporter.Export(output, cancellationToken: cancellation.Token));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void CaptureFailurePreservesExistingOutputAndUnrelatedTemporaryFiles()
    {
        string output = Write("existing.zip", [77]);
        string unrelated = Write("existing.zip.unrelated.tmp", [88]);
        var exporter = new SafetyBackupExporter(() => throw new InvalidOperationException("Synthetic capture failure"));
        Assert.Throws<InvalidOperationException>(() => exporter.Export(output, overwriteConfirmed: true));
        Assert.Equal(new byte[] { 77 }, File.ReadAllBytes(output));
        Assert.Equal(new byte[] { 88 }, File.ReadAllBytes(unrelated));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void CancellationDuringAssetReadPreservesOutputAndCleansOnlyOwnTemps()
    {
        string output = Write("existing.zip", [77]);
        string unrelated = Write("other.tmp", [88]);
        using var cancellation = new CancellationTokenSource();
        var exporter = new SafetyBackupExporter(() => Capture("synthetic.png"), _ =>
        {
            cancellation.Cancel();
            return new MemoryStream([1, 2, 3]);
        });
        Assert.Throws<OperationCanceledException>(() => exporter.Export(output, true, cancellation.Token));
        Assert.Equal(new byte[] { 77 }, File.ReadAllBytes(output));
        Assert.Equal(new byte[] { 88 }, File.ReadAllBytes(unrelated));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void OutputCreatedDuringCaptureIsNeverSilentlyReplaced()
    {
        string output = Path.Combine(_root, "race.zip");
        var exporter = new SafetyBackupExporter(() => { File.WriteAllBytes(output, [77]); return Capture(null); });
        Assert.Throws<IOException>(() => exporter.Export(output));
        Assert.Equal(new byte[] { 77 }, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void CannotOverwriteCapturedSourceEvenWithConfirmation()
    {
        string source = Write("wallpaper.zip", [1, 2, 3]);
        var exporter = new SafetyBackupExporter(() => Capture(source));
        Assert.Throws<IOException>(() => exporter.Export(source, overwriteConfirmed: true));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("not-a-zip.json")]
    [InlineData("relative.zip")]
    public void RejectsInvalidOutputBeforeCapture(string name)
    {
        string output = name == "relative.zip" ? name : Path.Combine(_root, name);
        var exporter = new SafetyBackupExporter(() => throw new InvalidOperationException("Must not capture"));
        Assert.Throws<ArgumentException>(() => exporter.Export(output));
    }

    [Fact]
    public void DuplicateShortcutNamesUseDistinctSafeEntriesAndPreserveBytes()
    {
        var assets = new Dictionary<string, byte[]> { [@"C:\synthetic-a\same.lnk"] = [1], [@"C:\synthetic-b\same.lnk"] = [2] };
        string output = Path.Combine(_root, "backup.zip");
        new SafetyBackupExporter(() => Capture(null, assets.Keys.ToArray()), path => new MemoryStream(assets[path])).Export(output);
        using var zip = ZipFile.OpenRead(output);
        var entries = zip.Entries.Where(e => e.FullName.StartsWith("public-desktop/")).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(2, entries.Select(e => e.FullName).Distinct().Count());
        Assert.All(entries, e => { Assert.DoesNotContain("..", e.FullName); Assert.DoesNotContain('\\', e.FullName); });
        Assert.Equal(new byte[] { 1, 2 }, entries.SelectMany(ReadBytes).ToArray());
    }

    private SafetyBackupCapture Capture(string? wallpaper, params string[] shortcuts) => new()
    {
        Layout = new LayoutSnapshot
        {
            CreatedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Topology = new MonitorTopology([new MonitorInfo("Synthetic display", -1920, 0, 1920, 1080, true)]),
            Icons = [new IconPosition("Synthetic icon", -1800, 100)],
        },
        DesktopPaths = new SafetyBackupDesktopPaths(@"%SYNTHETIC_USER%\Desktop", "", @"C:\Synthetic\Desktop", @"C:\SyntheticPublic\Desktop"),
        Wallpaper = new WallpaperState(wallpaper, "22", "1"),
        PublicShortcutPaths = shortcuts,
    };

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static JsonDocument ReadJson(ZipArchive zip, string name) => JsonDocument.Parse(ReadBytes(zip.GetEntry(name)!));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FailingReadStream : MemoryStream
    {
        private bool _readOnce;
        public FailingReadStream() : base(Encoding.UTF8.GetBytes("synthetic partial bytes")) { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_readOnce) throw new IOException("Synthetic disappearance during read");
            _readOnce = true;
            return base.Read(buffer, offset, Math.Min(count, 3));
        }
    }
}
