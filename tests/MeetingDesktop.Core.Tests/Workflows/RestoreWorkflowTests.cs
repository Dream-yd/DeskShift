using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

/// <summary>
/// Tests for the order a restore runs in.
/// </summary>
/// <remarks>
/// The shell must reload the desktop binding before coordinate placement, and
/// the UAC prompt must come before any state change, as in prepare.
/// </remarks>
public class RestoreWorkflowTests
{
    [Fact]
    public async Task RefusesWhenThereIsNoRecordOfASwap()
    {
        using var harness = WorkflowHarness.Create();
        harness.Desktop.ThrowOn(nameof(RecordingDesktopOperations.ReadIcons));

        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => harness.RestoreWorkflow().RunAsync());

        Assert.Empty(harness.Elevation.Requests);
        Assert.Empty(harness.Desktop.Calls);
    }

    [Fact]
    public async Task RefusesWhenTheRecordedDesktopFolderIsGone()
    {
        // Pointing the desktop at a missing folder makes the user's files look
        // like they vanished. Refuse rather than guess.
        using var harness = WorkflowHarness.Create();
        harness.SaveSession();
        Directory.Delete(harness.OriginalDesktop);

        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => harness.RestoreWorkflow().RunAsync());

        Assert.Empty(harness.Elevation.Requests);
        Assert.Empty(harness.Desktop.Calls);
    }

    [Fact]
    public async Task ChangesNothing_WhenThePromptIsDismissed()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession(wallpaperChanged: true);
        harness.Elevation.Outcome = ElevationOutcome.Declined;

        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => harness.RestoreWorkflow().RunAsync());

        Assert.Empty(harness.Desktop.Calls);

        // The record survives: the swap is still in place and still needs
        // undoing.
        Assert.True(harness.Sessions.Exists);
    }

    [Fact]
    public async Task RestartsExplorerWithoutApplyingAnUnassociatedNativeBlob()
    {
        // The shared file may belong to any previous session.
        using var harness = WorkflowHarness.Create();
        harness.SaveSession();
        File.WriteAllBytes(Path.Combine(harness.Paths.RecoveryDirectory, "IconLayouts.bin"), [0x2A, 0x00]);

        await harness.RestoreWorkflow().RunAsync();

        int stop = harness.Desktop.IndexOf(nameof(RecordingDesktopOperations.StopExplorer));
        int start = harness.Desktop.IndexOf(nameof(RecordingDesktopOperations.StartExplorer));

        Assert.True(stop >= 0, "explorer was never stopped");
        Assert.True(stop < start, "explorer was not restarted after stopping");
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.ImportIconLayoutBlob), harness.Desktop.Calls);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.WriteIconLayoutBlob), harness.Desktop.Calls);
    }

    [Fact]
    public async Task PointsTheDesktopBack_BeforeThePromptIsRaised()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession();

        IReadOnlyList<string> callsAtPrompt = [];
        harness.Elevation.OnRun = _ => callsAtPrompt = [.. harness.Desktop.Calls];

        await harness.RestoreWorkflow().RunAsync();

        Assert.Equal([ElevatedOperations.RestoreShortcuts], harness.Elevation.Requests);
        Assert.Empty(callsAtPrompt);
    }

    [Fact]
    public async Task PutsTheWallpaperBack_OnlyWhenTheSwapReplacedIt()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession(wallpaperChanged: true);

        await harness.RestoreWorkflow().RunAsync();

        Assert.Equal(
            new WallpaperState(@"C:\before.png", "10", "0"),
            Assert.Single(harness.Desktop.AppliedWallpapers));
    }

    [Fact]
    public async Task LeavesTheWallpaperAlone_WhenTheSwapDidNotTouchIt()
    {
        // Re-applying an identical wallpaper flashes the desktop at the exact
        // moment the user is getting ready to present, for no change.
        using var harness = WorkflowHarness.Create();
        harness.SaveSession(wallpaperChanged: false);

        await harness.RestoreWorkflow().RunAsync();

        Assert.Empty(harness.Desktop.AppliedWallpapers);
    }

    [Fact]
    public async Task ReplacesTheIconsByName_AndClearsTheRecordLast()
    {
        using var harness = WorkflowHarness.Create();
        string snapshot = harness.CreateLayoutSnapshot();
        harness.SaveSession(wallpaperChanged: true, snapshotPath: snapshot);
        File.WriteAllBytes(Path.Combine(harness.Paths.RecoveryDirectory, "IconLayouts.bin"), [0x2A, 0x00]);

        RestoreResult result = await harness.RestoreWorkflow().RunAsync();

        Assert.Equal(harness.OriginalDesktop, result.RestoredDesktopPath);
        Assert.Equal(2, result.Layout?.Placed);

        Assert.Equal(
            [
                nameof(RecordingDesktopOperations.WriteDesktopPath),
                nameof(RecordingDesktopOperations.StopExplorer),
                nameof(RecordingDesktopOperations.StartExplorer),
                nameof(RecordingDesktopOperations.WaitForCompatibleDesktop),
                nameof(RecordingDesktopOperations.PlaceIcons),
                nameof(RecordingDesktopOperations.ApplyWallpaper),
            ],
            harness.Desktop.Calls);

        // Last, so that a crash between placing the icons and clearing the
        // record leaves a record - which is recoverable - rather than a
        // half-restored desktop the app believes is normal.
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task SkipsTheBlobStep_WhenNoRecoveryCopyExists()
    {
        // A swap that captured no blob, or a recovery folder a user cleaned
        // out. The name-based pass is the primary mechanism and still runs.
        using var harness = WorkflowHarness.Create();
        harness.SaveSession();

        await harness.RestoreWorkflow().RunAsync();

        Assert.DoesNotContain(nameof(RecordingDesktopOperations.WriteIconLayoutBlob), harness.Desktop.Calls);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceIcons), harness.Desktop.Calls);
    }
}
