using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public sealed class DesktopViewFailureWorkflowTests
{
    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer, false)]
    [InlineData(DesktopSwitchMode.RestartExplorer, true)]
    [InlineData(DesktopSwitchMode.Live, false)]
    [InlineData(DesktopSwitchMode.Live, true)]
    public async Task PreparePreservesFailureCategoryAndRecoveryRecordUntilRollbackIsExact(
        DesktopSwitchMode mode, bool rollbackFails)
    {
        using var harness = WorkflowHarness.Create();
        var failure = new DesktopViewVerificationException("Target desktop not verified.");
        harness.Desktop.OnWaitForCompatibleDesktop = (path, _, _) =>
        {
            if (path == harness.MeetingDesktop || rollbackFails) throw failure;
        };
        harness.Desktop.OnCall = call =>
        {
            if (call == nameof(RecordingDesktopOperations.SwitchLiveDesktop) &&
                (harness.Desktop.WrittenDesktopPaths.Last() == harness.MeetingDesktop || rollbackFails)) throw failure;
        };

        var actual = await Assert.ThrowsAsync<DesktopViewVerificationException>(() =>
            harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, SwitchMode: mode)));

        Assert.Same(failure, actual);
        Assert.Equal(new[] { ElevatedOperations.MoveShortcutsAside, ElevatedOperations.RestoreShortcuts }, harness.Elevation.Requests);
        Assert.Equal(harness.OriginalDesktop, harness.Desktop.WrittenDesktopPaths.Last());
        Assert.Equal(rollbackFails, harness.Sessions.Exists);
        string placement = mode == DesktopSwitchMode.Live
            ? nameof(RecordingDesktopOperations.PlaceShellIcons) : nameof(RecordingDesktopOperations.PlaceIcons);
        Assert.Equal(!rollbackFails, harness.Desktop.Calls.Contains(placement));
        if (rollbackFails) Assert.Equal(harness.OriginalDesktop, harness.Sessions.Load()!.OriginalDesktopPath);
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    [InlineData(DesktopSwitchMode.Live)]
    public async Task UnverifiedRestoreKeepsExactRecoveryRecordAndNeverPlacesIconsOrWallpaper(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        var session = harness.SaveSession(wallpaperChanged: true, snapshotPath: harness.CreateLayoutSnapshot());
        harness.Sessions.Save(session with { SwitchMode = mode });
        string originalRecord = File.ReadAllText(harness.Sessions.FilePath);
        var failure = new DesktopViewVerificationException("Original desktop not verified.");
        harness.Desktop.OnWaitForCompatibleDesktop = (_, _, _) => throw failure;
        harness.Desktop.OnCall = call =>
        {
            if (call == nameof(RecordingDesktopOperations.SwitchLiveDesktop)) throw failure;
        };

        var actual = await Assert.ThrowsAsync<DesktopViewVerificationException>(() => harness.RestoreWorkflow().RunAsync());

        Assert.Same(failure, actual);
        Assert.Equal(originalRecord, File.ReadAllText(harness.Sessions.FilePath));
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceIcons), harness.Desktop.Calls);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceShellIcons), harness.Desktop.Calls);
        Assert.Empty(harness.Desktop.AppliedWallpapers);
    }
}
