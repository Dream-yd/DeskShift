using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

/// <summary>
/// Tests for which sources get to decide where the desktop is.
/// </summary>
/// <remarks>
/// <para>
/// The interesting input is a set of candidates that disagree, and no real
/// machine can be made to produce one on demand - so <c>Decide</c> takes the
/// candidates rather than reading them, and these build them by hand.
/// </para>
/// <para>
/// The case these exist for: <c>SHGetKnownFolderPath</c> answers from a
/// per-process cache, so an app that changes the desktop path and keeps running
/// holds a stale copy. Treating a disagreement with that copy as a conflict
/// means the app declares its own restore broken and refuses to switch again
/// until the cache expires.
/// </para>
/// </remarks>
public class DesktopLocationDecideTests
{
    /// <summary>A real folder, so the existence check passes honestly.</summary>
    private static readonly string ExistingFolder = Path.GetTempPath();

    private static readonly string OtherExistingFolder = Path.GetDirectoryName(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))!;

    private static DesktopPathCandidate Candidate(
        DesktopPathSource source,
        string? path,
        bool exists = true) =>
        new(source, path ?? string.Empty, path, exists);

    [Fact]
    public void StaleKnownFolder_DoesNotConflictWithTheRegistry()
    {
        // The failure that prompted this: the app restores the desktop, the
        // registry is read fresh and says the real folder, and the shell's
        // cached answer still says the meeting folder.
        DesktopLocation location = DesktopLocationService.Decide(
        [
            Candidate(DesktopPathSource.KnownFolder, OtherExistingFolder),
            Candidate(DesktopPathSource.UserShellFolders, ExistingFolder),
            Candidate(DesktopPathSource.ShellFolders, ExistingFolder),
            Candidate(DesktopPathSource.UserProfile, OtherExistingFolder),
        ]);

        Assert.Equal(DesktopLocationStatus.Agreed, location.Status);
        Assert.Equal(ExistingFolder, location.ResolvedPath);
    }

    [Fact]
    public void StaleKnownFolder_DoesNotMakeAnEmptyRegistryUnresolvable()
    {
        // Same shape with the registry keys unreadable. The cached answer is
        // still the best thing available, so it resolves - just on its own,
        // which is what SingleSource says.
        DesktopLocation location = DesktopLocationService.Decide(
        [
            Candidate(DesktopPathSource.KnownFolder, ExistingFolder),
            Candidate(DesktopPathSource.UserShellFolders, null),
            Candidate(DesktopPathSource.ShellFolders, null),
            Candidate(DesktopPathSource.UserProfile, OtherExistingFolder),
        ]);

        Assert.Equal(DesktopLocationStatus.SingleSource, location.Status);
        Assert.Equal(ExistingFolder, location.ResolvedPath);
    }

    [Fact]
    public void TheTwoRegistryValuesDisagreeing_IsStillAConflict()
    {
        // The check that must survive. Both are read fresh, so a real
        // disagreement between them is a machine in a state the app must not
        // guess about - it is what a half-applied folder redirection looks
        // like.
        DesktopLocation location = DesktopLocationService.Decide(
        [
            Candidate(DesktopPathSource.KnownFolder, ExistingFolder),
            Candidate(DesktopPathSource.UserShellFolders, ExistingFolder),
            Candidate(DesktopPathSource.ShellFolders, OtherExistingFolder),
            Candidate(DesktopPathSource.UserProfile, ExistingFolder),
        ]);

        Assert.Equal(DesktopLocationStatus.Conflicting, location.Status);
        Assert.Null(location.ResolvedPath);
    }

    [Fact]
    public void TheProfileDefaultIsNeverAVote()
    {
        // It differs legitimately on any machine where the desktop has been
        // moved, which is every machine using this app.
        DesktopLocation location = DesktopLocationService.Decide(
        [
            Candidate(DesktopPathSource.KnownFolder, null),
            Candidate(DesktopPathSource.UserShellFolders, ExistingFolder),
            Candidate(DesktopPathSource.ShellFolders, ExistingFolder),
            Candidate(DesktopPathSource.UserProfile, OtherExistingFolder),
        ]);

        Assert.Equal(DesktopLocationStatus.Agreed, location.Status);
        Assert.Equal(ExistingFolder, location.ResolvedPath);
    }

    [Fact]
    public void OneRegistryValueAlone_IsSingleSource()
    {
        DesktopLocation location = DesktopLocationService.Decide(
        [
            Candidate(DesktopPathSource.KnownFolder, null),
            Candidate(DesktopPathSource.UserShellFolders, null),
            Candidate(DesktopPathSource.ShellFolders, ExistingFolder),
            Candidate(DesktopPathSource.UserProfile, null),
        ]);

        Assert.Equal(DesktopLocationStatus.SingleSource, location.Status);
        Assert.Equal(ExistingFolder, location.ResolvedPath);
    }

    [Fact]
    public void NothingReadableAtAll_FallsBackToTheProfileDefault()
    {
        DesktopLocation location = DesktopLocationService.Decide(
        [
            Candidate(DesktopPathSource.KnownFolder, null),
            Candidate(DesktopPathSource.UserShellFolders, null),
            Candidate(DesktopPathSource.ShellFolders, null),
            Candidate(DesktopPathSource.UserProfile, ExistingFolder),
        ]);

        Assert.Equal(DesktopLocationStatus.SingleSource, location.Status);
        Assert.Equal(ExistingFolder, location.ResolvedPath);
    }

    [Fact]
    public void APathThatIsNotOnDisk_IsUnresolved_HoweverManySourcesAgreeAboutIt()
    {
        // Handing explorer a folder that does not exist makes the user's files
        // look like they have vanished.
        string missing = Path.Combine(Path.GetTempPath(), "MeetingDesktopTests", "does-not-exist");

        DesktopLocation location = DesktopLocationService.Decide(
        [
            Candidate(DesktopPathSource.KnownFolder, missing),
            Candidate(DesktopPathSource.UserShellFolders, missing),
            Candidate(DesktopPathSource.ShellFolders, missing, exists: false),
            Candidate(DesktopPathSource.UserProfile, null),
        ]);

        Assert.Equal(DesktopLocationStatus.Unresolved, location.Status);
        Assert.Null(location.ResolvedPath);
        Assert.False(location.IsUsable);
    }

    [Fact]
    public void CandidatesAreAllReported_EvenTheOnesThatDoNotVote()
    {
        // The Diagnostics page is the only way to work out what happened on
        // somebody else's machine, so nothing may be dropped from the report.
        DesktopLocation location = DesktopLocationService.Decide(
        [
            Candidate(DesktopPathSource.KnownFolder, OtherExistingFolder),
            Candidate(DesktopPathSource.UserShellFolders, ExistingFolder),
            Candidate(DesktopPathSource.ShellFolders, ExistingFolder),
            Candidate(DesktopPathSource.UserProfile, OtherExistingFolder),
        ]);

        Assert.Equal(4, location.Candidates.Count);
        Assert.Contains(location.Candidates, c => c.Source == DesktopPathSource.KnownFolder);
        Assert.Contains(location.Candidates, c => c.Source == DesktopPathSource.UserProfile);
    }
}
