using System.Runtime.InteropServices;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public class CompatibilityDesktopReadinessTests
{
    private const string Target = @"C:\Meeting";
    private const string Personal = @"C:\Personal";
    private const string Public = @"C:\Public";

    private static IconPosition[] Icons(int count, string folder = Target) => Enumerable.Range(0, count)
        .Select(i => new IconPosition($"Icon {i}", i * 75, 0, Path.Combine(folder, $"Icon {i}.lnk"), true)).ToArray();

    private static CompatibilityDesktopContents Contents(IReadOnlyList<IconPosition> icons) =>
        new(icons.Where(i => i.IsFileSystem == true).Select(i => i.Identity!).ToArray(), []);

    [Theory]
    [InlineData(Personal)]
    [InlineData(null)]
    [InlineData("::{Desktop}")]
    public void RegistryTargetAndMatchingIconsCannotReplaceProofOfActualFolder(string? actualFolder)
    {
        var icons = Icons(2);
        Assert.False(CompatibilityDesktopReadiness.IsReady(Target, Public, icons,
            new(actualFolder, icons, icons), Contents(icons)));
    }

    [Fact]
    public void CorrectFolderAndSameCountOfOldPersonalIconsAreNotReady()
    {
        var expected = Icons(141);
        var old = Icons(141, Personal);
        Assert.False(CompatibilityDesktopReadiness.IsReady(Target, Public, expected,
            new(Target, old, old), Contents(expected)));
    }

    [Fact]
    public void ThreeStablePartialListsCannotSatisfyFullExpectedRestore()
    {
        var full = Icons(141);
        var partial = full.Take(42).ToArray();
        int captures = 0;
        CompatibilityDesktopReadiness.Wait(Target, Public, full, _ =>
        {
            captures++;
            var current = captures <= 3 ? partial : full;
            return new(Target, current, current);
        }, () => Contents(full), default, pause: _ => { });

        Assert.Equal(6, captures);
    }

    [Fact]
    public void FullShellListingMustWaitForCompatibilityControlToFinishLoading()
    {
        var full = Icons(141);
        int captures = 0;
        CompatibilityDesktopReadiness.Wait(Target, Public, full, _ =>
        {
            captures++;
            return new(Target, full, captures <= 3 ? full.Take(42).ToArray() : full);
        }, () => Contents(full), default, pause: _ => { });

        Assert.Equal(6, captures);
    }

    [Fact]
    public void SameCountWithChangingIdentitiesResetsStability()
    {
        var first = Icons(2);
        var second = Icons(2).Select(i => i with { Name = i.Name + " new", Identity = i.Identity + ".new" }).ToArray();
        var current = first;
        int captures = 0;
        CompatibilityDesktopReadiness.Wait(Target, Public, null, _ =>
        {
            captures++;
            current = captures <= 2 ? first : second;
            return new(Target, current, current);
        }, () => Contents(current), default, pause: _ => { });

        Assert.Equal(5, captures);
    }

    [Fact]
    public void MissingSavedVirtualIconCannotPassUsingOnlyFilesystemCount()
    {
        var files = Icons(2);
        IconPosition[] saved = [.. files, new("Recycle Bin", 0, 200, "::{Bin}", false)];
        Assert.False(CompatibilityDesktopReadiness.IsReady(Target, Public, saved,
            new(Target, files, files), Contents(files)));
    }

    [Fact]
    public void OptionalHiddenEntriesNeedNotAppearButSavedHiddenIconsMustAppear()
    {
        var required = Icons(1);
        var hidden = new IconPosition("Hidden", 100, 0, Path.Combine(Target, "hidden.txt"), true);
        var contents = new CompatibilityDesktopContents([required[0].Identity!], [hidden.Identity!]);
        var snapshot = new CompatibilityDesktopSnapshot(Target, required, required);
        Assert.True(CompatibilityDesktopReadiness.IsReady(Target, Public, null, snapshot, contents));
        Assert.False(CompatibilityDesktopReadiness.IsReady(Target, Public, [.. required, hidden], snapshot, contents));
    }

    [Fact]
    public void RestoredPublicShortcutsMustFinishLoadingToo()
    {
        var targetIcons = Icons(2);
        var publicIcon = Icons(1, Public)[0];
        IconPosition[] expected = [.. targetIcons, publicIcon];
        Assert.False(CompatibilityDesktopReadiness.IsReady(Target, Public, null,
            new(Target, targetIcons, targetIcons), Contents(expected)));
        Assert.True(CompatibilityDesktopReadiness.IsReady(Target, Public, expected,
            new(Target, expected, expected), Contents(expected)));
    }

    [Fact]
    public void EmptyFolderStillRequiresActualBindingAndThreeStableEnumerations()
    {
        int captures = 0;
        CompatibilityDesktopReadiness.Wait(Target, Public, [], _ =>
        {
            captures++;
            return new(captures == 1 ? Personal : Target, [], []);
        }, () => new([], []), default, pause: _ => { });
        Assert.Equal(4, captures);
    }

    [Fact]
    public void TransientComFailureResetsConsecutiveCompleteSamples()
    {
        var icons = Icons(2);
        int captures = 0;
        CompatibilityDesktopReadiness.Wait(Target, Public, icons, _ =>
        {
            if (++captures == 3) throw new COMException("View is rebuilding");
            return new(Target, icons, icons);
        }, () => Contents(icons), default, pause: _ => { });
        Assert.Equal(6, captures);
    }

    [Fact]
    public void TimeoutReportsUnverifiedFolderAndIncompleteListing()
    {
        var expected = Icons(141);
        var partial = expected.Take(42).ToArray();
        var error = Assert.Throws<DesktopViewVerificationException>(() => CompatibilityDesktopReadiness.Wait(Target, Public, expected,
            _ => new(Personal, partial, partial), () => Contents(expected), default, timeout: TimeSpan.Zero));
        Assert.Contains(Personal, error.Message);
        Assert.Contains("42", error.Message);
        Assert.Contains("141", error.Message);
    }

    [Fact]
    public void CancellationWhileWaitingNeverAcceptsNextSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        int captures = 0;
        var icons = Icons(2);
        Assert.ThrowsAny<OperationCanceledException>(() => CompatibilityDesktopReadiness.Wait(Target, Public, icons,
            _ => { captures++; return new(Target, icons, icons); }, () => Contents(icons), cancellation.Token,
            pause: _ => cancellation.Cancel()));
        Assert.Equal(1, captures);
    }

    [Fact]
    public void VirtualDesktopRootAcceptsCompleteParsedTargetContentsAndMatchingKnownFolder()
    {
        var icons = Icons(141);
        var snapshot = new CompatibilityDesktopSnapshot(null, icons, icons) { KnownFolderPath = Target };
        Assert.True(CompatibilityDesktopReadiness.IsReady(Target, Public, icons, snapshot, Contents(icons)));
    }

    [Fact]
    public void MatchingKnownFolderCannotAcceptStalePersonalIcons()
    {
        var expected = Icons(141);
        var old = Icons(141, Personal);
        var snapshot = new CompatibilityDesktopSnapshot(null, old, old) { KnownFolderPath = Target };
        Assert.False(CompatibilityDesktopReadiness.IsReady(Target, Public, null, snapshot, Contents(expected)));
    }

    [Fact]
    public void ParsedTargetContentsCannotOverrideWrongKnownFolderOrExplicitWrongPhysicalFolder()
    {
        var icons = Icons(2);
        var snapshot = new CompatibilityDesktopSnapshot(null, icons, icons) { KnownFolderPath = Personal };
        Assert.False(CompatibilityDesktopReadiness.IsReady(Target, Public, icons, snapshot, Contents(icons)));
        snapshot = snapshot with { FolderPath = Personal, KnownFolderPath = Target };
        Assert.False(CompatibilityDesktopReadiness.IsReady(Target, Public, icons, snapshot, Contents(icons)));
    }

    [Fact]
    public void EmptyVirtualDesktopCannotBeProvedByPublicIconsAndKnownFolderAlone()
    {
        var icons = Icons(42, Public);
        var snapshot = new CompatibilityDesktopSnapshot(null, icons, icons) { KnownFolderPath = Target };
        Assert.False(CompatibilityDesktopReadiness.IsReady(Target, Public, null, snapshot, Contents(icons)));
    }

    [Fact]
    public void EmptyTargetMarkerProvesBindingAndIsRemovedBeforeFinalStableListing()
    {
        using var harness = WorkflowHarness.Create();
        int stages = 0, samples = 0;
        CompatibilityDesktopReadiness.Verify(harness.MeetingDesktop, harness.OriginalDesktop, proof =>
        {
            stages++;
            if (stages == 1) Assert.True(File.Exists(proof!.MarkerPath));
            else Assert.Empty(Directory.GetFileSystemEntries(harness.MeetingDesktop));
            return CompatibilityDesktopReadiness.Wait(harness.MeetingDesktop, harness.OriginalDesktop, null, _ =>
            {
                samples++;
                var icons = Directory.GetFiles(harness.MeetingDesktop)
                    .Select(path => new IconPosition(Path.GetFileName(path), 0, 0, path, true)).ToArray();
                return new(null, icons, icons)
                {
                    KnownFolderPath = Personal, // Marker evidence must work despite a stale Known Folder cache.
                    ViewWindow = 123,
                    ProcessId = 456,
                };
            }, () => CompatibilityDesktopReadiness.EnumerateContents(harness.MeetingDesktop, harness.OriginalDesktop),
                default, pause: _ => { }, bindingProof: proof);
        }, default);

        Assert.Equal(2, stages);
        Assert.Equal(6, samples);
        Assert.Empty(Directory.GetFileSystemEntries(harness.MeetingDesktop));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MarkerIsRemovedOnReadinessFailureOrCancellation(bool cancel)
    {
        using var harness = WorkflowHarness.Create();
        using var cancellation = new CancellationTokenSource();
        string? marker = null;
        var failure = new TimeoutException("Target view never showed our marker");
        var error = Record.Exception(() => CompatibilityDesktopReadiness.Verify(harness.MeetingDesktop, harness.OriginalDesktop, proof =>
        {
            marker = proof!.MarkerPath;
            Assert.True(File.Exists(marker));
            if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw failure;
        }, cancellation.Token));

        Assert.NotNull(error);
        if (!cancel) Assert.Same(failure, error);
        else Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.False(File.Exists(marker));
        Assert.Empty(Directory.GetFileSystemEntries(harness.MeetingDesktop));
    }

    [Fact]
    public void NonemptyReadOnlyVerificationNeverCreatesAMarker()
    {
        using var harness = WorkflowHarness.Create();
        string original = Path.Combine(harness.OriginalDesktop, "original.txt");
        File.WriteAllText(original, "untouched");
        CompatibilityDesktopReadiness.Verify(harness.OriginalDesktop, harness.MeetingDesktop, proof =>
        {
            Assert.Null(proof);
            Assert.Equal(new[] { original }, Directory.GetFiles(harness.OriginalDesktop));
            return new(harness.OriginalDesktop, [], []);
        }, default);
        Assert.Equal("untouched", File.ReadAllText(original));
        Assert.Equal(new[] { original }, Directory.GetFiles(harness.OriginalDesktop));
    }

    [Theory]
    [InlineData(123, 999)]
    [InlineData(999, 456)]
    [InlineData(0, 0)]
    public void RemovedMarkerProofCannotCarryAcrossAnotherShellViewOrProcess(int window, int processId)
    {
        var snapshot = new CompatibilityDesktopSnapshot(null, [], [])
        {
            KnownFolderPath = Target, ViewWindow = window, ProcessId = (uint)processId,
        };
        Assert.False(CompatibilityDesktopReadiness.IsReady(Target, Public, null, snapshot, new([], []), new(Target, 123, 456)));
    }
}
