using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class RecoveryRecordReviewTests
{
    [Theory]
    [InlineData(@"C:\Original", DesktopLocationStatus.Agreed, RecoveryRecordState.OriginalDesktopActive)]
    [InlineData(@"c:\original\", DesktopLocationStatus.SingleSource, RecoveryRecordState.OriginalDesktopActive)]
    [InlineData(@"C:\Meeting", DesktopLocationStatus.Agreed, RecoveryRecordState.MeetingDesktopActive)]
    [InlineData(@"C:\Other", DesktopLocationStatus.Agreed, RecoveryRecordState.DifferentDesktopActive)]
    [InlineData(null, DesktopLocationStatus.Conflicting, RecoveryRecordState.DesktopUnresolved)]
    [InlineData(null, DesktopLocationStatus.Unresolved, RecoveryRecordState.DesktopUnresolved)]
    public void ReviewExplainsRecordWithoutForcingOldLayout(string? current, DesktopLocationStatus status, RecoveryRecordState expected)
    {
        var session = new SessionInfo { OriginalDesktopPath = @"C:\Original", MeetingDesktopPathAtSwap = @"C:\Meeting" };
        Assert.Equal(expected, RecoveryRecordReview.Inspect(new(status, current, [], false), true, session));
    }

    [Theory]
    [InlineData(false, RecoveryRecordState.None)]
    [InlineData(true, RecoveryRecordState.Unreadable)]
    public void NoRecordAndUnreadableRecordHaveDifferentActions(bool exists, RecoveryRecordState expected) =>
        Assert.Equal(expected, RecoveryRecordReview.Inspect(new(DesktopLocationStatus.Agreed, @"C:\Original", [], false), exists, null));

    [Theory]
    [InlineData(@"C:\Original")]
    [InlineData("relative")]
    [InlineData(null)]
    public void AmbiguousOrMalformedRecordCannotOfferArchive(string? meeting)
    {
        var session = new SessionInfo { OriginalDesktopPath = @"C:\Original", MeetingDesktopPathAtSwap = meeting! };
        Assert.Equal(RecoveryRecordState.DesktopUnresolved, RecoveryRecordReview.Inspect(
            new(DesktopLocationStatus.Agreed, @"C:\Original", [], false), true, session));
    }
}
