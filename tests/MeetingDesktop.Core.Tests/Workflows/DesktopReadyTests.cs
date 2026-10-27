using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests.Workflows;

public sealed class DesktopReadyTests
{
    [Fact]
    public async Task WallpaperFailureDoesNotSignalReadyOrClearRecovery()
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession(wallpaperChanged: true, snapshotPath: h.CreateLayoutSnapshot());
        h.Desktop.ApplyWallpaperSucceeds = false;
        bool ready = false;
        await Assert.ThrowsAsync<MeetingDesktop.Core.Workflows.RestoreIncompleteException>(() =>
            h.RestoreWorkflow().RunAsync(desktopReady: () => ready = true));
        Assert.False(ready);
        Assert.True(h.Sessions.Exists);
    }

    [Fact]
    public async Task InventoryErrorCannotHideAnIncompleteLayout()
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession(snapshotPath: h.CreateLayoutSnapshot());
        h.Desktop.Placement = new(2, 1, [], ["missing"], false, false);
        var workflow = new MeetingDesktop.Core.Workflows.RestoreWorkflow(h.Paths, h.Layouts,
            h.Sessions, h.Elevation, h.Desktop, _ => throw new IOException("Folder unavailable"));
        var error = await Assert.ThrowsAsync<MeetingDesktop.Core.Workflows.RestoreIncompleteException>(() => workflow.RunAsync());
        Assert.False(error.Result.LeftoverScanComplete);
        Assert.False(error.Result.Layout!.IsExact);
        Assert.True(h.Sessions.Exists);
    }

    [Fact]
    public async Task OptionalPresentationFailureDoesNotPreventCompletingRestore()
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession(snapshotPath: h.CreateLayoutSnapshot());
        int calls = 0;
        await h.RestoreWorkflow().RunAsync(desktopReady: () => { calls++; throw new InvalidOperationException(); });
        Assert.Equal(1, calls);
        Assert.False(h.Sessions.Exists);
    }

    [Fact]
    public async Task PrepareSignalsReadyAfterVerifiedSwitchBeforeDone()
    {
        using var h = WorkflowHarness.Create();
        int ready = 0;
        await h.PrepareWorkflow().RunAsync(new(h.MeetingDesktop, false, SwitchMode: DesktopSwitchMode.Live),
            desktopReady: () =>
            {
                Assert.Contains(nameof(RecordingDesktopOperations.SwitchLiveDesktop), h.Desktop.Calls);
                Assert.True(h.Sessions.Exists);
                ready++;
            });
        Assert.Equal(1, ready);
    }

    [Fact]
    public async Task RestoreSignalsReadyAfterLayoutAndWallpaperBeforeClearingRecovery()
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession(wallpaperChanged: true, snapshotPath: h.CreateLayoutSnapshot());
        int ready = 0;
        await h.RestoreWorkflow().RunAsync(desktopReady: () =>
        {
            Assert.Contains(nameof(RecordingDesktopOperations.PlaceIcons), h.Desktop.Calls);
            Assert.Contains(nameof(RecordingDesktopOperations.ApplyWallpaper), h.Desktop.Calls);
            Assert.True(h.Sessions.Exists);
            ready++;
        });
        Assert.Equal(1, ready);
        Assert.False(h.Sessions.Exists);
    }

    [Fact]
    public async Task FailedLayoutDoesNotSignalReadyAndKeepsRecovery()
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession(snapshotPath: h.CreateLayoutSnapshot());
        h.Desktop.Placement = new(2, 1, [], ["missing"], false, false);
        bool ready = false;
        await Assert.ThrowsAsync<MeetingDesktop.Core.Workflows.RestoreIncompleteException>(() =>
            h.RestoreWorkflow().RunAsync(desktopReady: () => ready = true));
        Assert.False(ready);
        Assert.True(h.Sessions.Exists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyDesktopIsRevealedBeforeSlowOrFailedFileInventory(bool fail)
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession(wallpaperChanged: true, snapshotPath: h.CreateLayoutSnapshot());
        bool ready = false;
        var workflow = new MeetingDesktop.Core.Workflows.RestoreWorkflow(
            h.Paths, h.Layouts, h.Sessions, h.Elevation, h.Desktop, _ =>
            {
                Assert.True(ready);
                Assert.Contains(nameof(RecordingDesktopOperations.ApplyWallpaper), h.Desktop.Calls);
                if (fail) throw new UnauthorizedAccessException("Folder unavailable");
                return ["notes.txt"];
            });
        var result = await workflow.RunAsync(desktopReady: () => ready = true);
        Assert.True(result.WallpaperRestored);
        Assert.Equal(!fail, result.LeftoverScanComplete);
        Assert.False(h.Sessions.Exists);
        if (!fail) Assert.Equal(["notes.txt"], result.FilesLeftOnMeetingDesktop);
    }

    [Fact]
    public async Task OptionalPresentationFailureDoesNotRollBackVerifiedSwitch()
    {
        using var h = WorkflowHarness.Create();
        int calls = 0;
        await h.PrepareWorkflow().RunAsync(new(h.MeetingDesktop, false, SwitchMode: DesktopSwitchMode.Live),
            desktopReady: () => { calls++; throw new InvalidOperationException("Presentation closed"); });
        Assert.Equal(1, calls);
        Assert.True(h.Sessions.Exists);
        Assert.Single(h.Desktop.WrittenDesktopPaths);
    }
}
