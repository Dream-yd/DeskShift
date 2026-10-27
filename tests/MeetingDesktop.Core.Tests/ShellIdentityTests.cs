using System.Text.Json;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public class ShellIdentityTests
{
    [Fact]
    public void StaleFilesystemItemCannotPassByAcquiringANamespaceStyleIdentity()
    {
        Assert.False(ShellViewReadiness.Matches(@"C:\Target", @"C:\Public", [],
            [new("Old file", 0, 0, "::{Stale}", true)]));
        Assert.False(ShellViewReadiness.Matches(@"C:\Target", @"C:\Public", [],
            [new("Old file", 0, 0, "::{Stale}", false)], allowedVirtualIdentities: new HashSet<string> { "::{Bin}" }));
    }

    [Fact]
    public void RelativeFileNameCannotBeTreatedAsAVirtualSystemIcon()
    {
        Assert.False(ShellViewReadiness.Matches(@"C:\Target", @"C:\Public", [],
            [new("Old file", 0, 0, "Old file.lnk")]));
    }

    [Fact]
    public void OptionalHiddenItemsDoNotHaveToAppearButUnknownItemsAreRejected()
    {
        Assert.True(ShellViewReadiness.Matches(@"C:\Target", @"C:\Public", [@"C:\Target\A"],
            [new("A", 0, 0, @"C:\Target\A")], [@"C:\Target\.hidden"]));
        Assert.True(ShellViewReadiness.Matches(@"C:\Target", @"C:\Public", [@"C:\Target\A"],
            [new("A", 0, 0, @"C:\Target\A"), new("Hidden", 75, 0, @"C:\Target\.hidden")],
            [@"C:\Target\.hidden"]));
        Assert.False(ShellViewReadiness.Matches(@"C:\Target", @"C:\Public", [],
            [new("Unknown", 0, 0, @"C:\Target\Unknown")], [@"C:\Target\.hidden"]));
    }

    [Fact]
    public void SameCaptionUsesFullIdentityToChooseCorrectItem()
    {
        IconPosition[] current = [new("Tool", 0, 0, @"C:\Users\A\Desktop\Tool.lnk"),
            new("Tool", 75, 0, @"C:\Users\Public\Desktop\Tool.lnk")];
        var match = Assert.Single(ShellIconMatcher.Match([current[1] with { X = 150 }], current));
        Assert.Equal(1, match.CurrentIndex);
    }

    [Fact]
    public void IdentityMismatchDoesNotFallBackToSameCaption()
    {
        Assert.Empty(ShellIconMatcher.Match([new("Tool", 0, 0, @"C:\Old\Tool.lnk")],
            [new("Tool", 0, 0, @"C:\Other\Tool.lnk")]));
    }

    [Fact]
    public void LegacyCaptionOnlyMatchesWhenBothSidesAreUnambiguous()
    {
        Assert.Empty(ShellIconMatcher.Match([new("Tool", 0, 0)],
            [new("Tool", 0, 0), new("Tool", 75, 0)]));
        Assert.Empty(ShellIconMatcher.Match([new("Tool", 0, 0), new("Tool", 75, 0)],
            [new("Tool", 0, 0)]));
        Assert.Single(ShellIconMatcher.Match([new("Tool", 0, 0)], [new("Tool", 75, 0)]));
    }

    [Fact]
    public void TwoRequestsCannotClaimOneCurrentIcon()
    {
        Assert.Empty(ShellIconMatcher.Match(
            [new("A", 0, 0, @"C:\Desk\A"), new("A", 75, 0, @"C:\Desk\A")],
            [new("A", 0, 0, @"C:\Desk\A")]));
    }

    [Fact]
    public void SameCountAndCaptionInWrongDirectoryIsNotReady()
    {
        Assert.False(ShellViewReadiness.Matches(@"C:\Target", @"C:\Public", [@"C:\Target\A"],
            [new("A", 0, 0, @"C:\Old\A")]));
    }

    [Fact]
    public void EmptyTargetMustHaveNoStaleUserItems()
    {
        Assert.False(ShellViewReadiness.Matches(@"C:\Target", @"C:\Public", [],
            [new("A", 0, 0, @"C:\Old\A")]));
        Assert.True(ShellViewReadiness.Matches(@"C:\Target", @"C:\Public", [],
            [new("Bin", 0, 0, "::{Bin}"), new("Tool", 0, 75, @"C:\Public\Tool")]));
    }

    [Fact]
    public void MissingExpectedItemIsNotReady()
    {
        Assert.False(ShellViewReadiness.Matches(@"C:\Target", @"C:\Public",
            [@"C:\Target\A", @"C:\Target\B"], [new("A", 0, 0, @"C:\Target\A")]));
    }

    [Fact]
    public void OldJsonKeepsCompatibilityModeAndCaptionOnlyLayout()
    {
        var icon = JsonSerializer.Deserialize<IconPosition>("{\"Name\":\"A\",\"X\":1,\"Y\":2}");
        Assert.Null(icon!.Identity);
        var session = JsonSerializer.Deserialize<SessionInfo>(
            "{\"OriginalDesktopPath\":\"C:\\\\Original\",\"MeetingDesktopPathAtSwap\":\"C:\\\\Meeting\"}");
        Assert.Equal(DesktopSwitchMode.RestartExplorer, session!.SwitchMode);
    }
}
