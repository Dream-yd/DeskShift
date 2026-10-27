using MeetingDesktop.App.Localization;
using MeetingDesktop.App.ViewModels;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.App.Tests;

public sealed class RestoreUncertaintyMessageTests
{
    [Fact]
    public void UncertainShortcutRecoveryExplainsWhyTheRecordWasKept()
    {
        var session = new SessionInfo { OriginalDesktopPath = @"C:\Original", MeetingDesktopPathAtSwap = @"C:\Alternate", ShortcutRestoreUncertain = true };
        Assert.Equal(LocalizationManager.Instance["ErrorShortcutRestoreUncertain"], RestoreViewModel.FailureStatus(session));
        Assert.Equal(LocalizationManager.Instance["ErrorRestoreRecordKept"], RestoreViewModel.FailureStatus(session with { ShortcutRestoreUncertain = false }));
    }

    [Fact]
    public void ExistingButAmbiguousIconsAreExplainedRatherThanReportedMissing()
    {
        var result = new RestoreResult(@"C:\Original", 0,
            new PlaceResult(1, 0, [], ["Alpha"], false, true), false, []) { UnmappedIcons = ["Alpha"] };
        string text = RestoreViewModel.Describe(result);
        Assert.Contains(LocalizationManager.Instance.Format("ResultRestoreUnmapped", 1), text);
        Assert.DoesNotContain(LocalizationManager.Instance.Format("ResultRestoreNotFound", 1), text);
    }
}
