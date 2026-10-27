using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Tests.Workflows;

namespace MeetingDesktop.Core.Tests;

/// <summary>
/// Tests for resolving the meeting-desktop folder.
/// </summary>
/// <remarks>
/// <para>
/// The folder is named in two places - the user's settings and the built-in
/// default - and on a fresh install only the second one exists. Resolving them
/// in one place is what keeps a swap and the session detector agreeing about
/// where the desktop went.
/// </para>
/// <para>
/// This was not a cosmetic inconsistency. The prepare step swapped to the
/// default while the session detector was handed the raw null setting, so after
/// the swap the app could not recognise its own work: it reported the desktop as
/// <see cref="SessionState.Adopted"/> - "swapped, with no record of by whom" -
/// on a machine that was holding the record. To the user that reads as the app
/// having lost track of their real desktop.
/// </para>
/// </remarks>
public class AppPathsTests
{
    private static AppPaths PathsFor(string root) => AppPaths.Resolve(["--data-dir", root]);

    [Fact]
    public void FallsBackToTheBuiltInDefault_WhenNothingIsConfigured()
    {
        // The normal case on a fresh install, not an edge case.
        AppPaths paths = PathsFor(@"C:\scratch");

        Assert.Equal(paths.MeetingDesktopDirectory, paths.ResolveMeetingDesktopDirectory(new AppSettings()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackToTheBuiltInDefault_ForABlankValue(string stored)
    {
        // A value that survived a partial save, or a settings file edited by
        // hand. Blank has to mean the same thing as absent, or the desktop gets
        // pointed at nothing.
        AppPaths paths = PathsFor(@"C:\scratch");

        Assert.Equal(
            paths.MeetingDesktopDirectory,
            paths.ResolveMeetingDesktopDirectory(new AppSettings { MeetingDesktopPath = stored }));
    }

    [Fact]
    public void UsesTheConfiguredFolder_WhenThereIsOne()
    {
        AppPaths paths = PathsFor(@"C:\scratch");

        Assert.Equal(
            @"D:\Presentations\Desktop",
            paths.ResolveMeetingDesktopDirectory(
                new AppSettings { MeetingDesktopPath = @"D:\Presentations\Desktop" }));
    }

    [Fact]
    public void RaisesForANullSettings()
    {
        // Not a null-tolerant method. Returning the default here would hide a
        // caller that never loaded the settings at all, which is the shape of
        // the bug this test file exists for.
        AppPaths paths = PathsFor(@"C:\scratch");

        Assert.Throws<ArgumentNullException>(() => paths.ResolveMeetingDesktopDirectory(null!));
    }

    [Fact]
    public void ASwapToTheDefaultFolder_IsRecognisedAsSwapped()
    {
        // The end-to-end shape of the bug, at the level Core can see: after a
        // swap to the default folder, resolving the folder the same way the
        // swap did has to hand the detector the folder the desktop is actually
        // pointing at.
        AppPaths paths = DefaultShapedPaths();

        var locations = new FakeDesktopLocationService(paths.MeetingDesktopDirectory);

        Assert.Equal(
            SessionState.Swapped,
            locations.DetectSessionState(paths.ResolveMeetingDesktopDirectory(new AppSettings())));
    }

    [Fact]
    public void ASwapToTheDefaultFolder_IsMistakenForSomeoneElsesSwap_WhenThePathIsNotResolved()
    {
        // The failure itself, kept as documentation of what the resolution step
        // is for, because the mistake is an easy one to make again. Handed
        // nothing to compare against, the detector cannot match the folder the
        // desktop points at, falls through to the shape test, and sees a folder
        // sitting inside a directory named like the app's own data folder - so
        // it reports the app's swap as somebody else's.
        //
        AppPaths paths = DefaultShapedPaths();

        var locations = new FakeDesktopLocationService(paths.MeetingDesktopDirectory);

        Assert.Equal(SessionState.Adopted, locations.DetectSessionState(null));
    }

    /// <summary>
    /// A scratch data directory laid out the way the real default is:
    /// <c>&lt;root&gt;\MeetingDesktop\Desktop</c>, mirroring
    /// <c>%LOCALAPPDATA%\MeetingDesktop\Desktop</c>.
    /// </summary>
    /// <remarks>
    /// The parent's name is load-bearing. The detector's last-resort shape test
    /// recognises the app's own swaps by a folder called <c>Desktop</c> whose
    /// parent is named like the app data directory, so a scratch path with a
    /// GUID for a parent does not exercise the same code at all.
    /// </remarks>
    private static AppPaths DefaultShapedPaths() =>
        PathsFor(Path.Combine(ScratchRoot(), "MeetingDesktop"));

    private static string ScratchRoot() =>
        Path.Combine(Path.GetTempPath(), "MeetingDesktopTests", Guid.NewGuid().ToString("N"));
}
