using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public class LiveSwitchWorkflowTests
{
    [Fact]
    public async Task SurvivingShellAfterFailedRestartMustReloadOriginalDesktop()
    {
        using var harness = WorkflowHarness.Create();
        harness.Desktop.ThrowOn(nameof(RecordingDesktopOperations.RestartExplorer));
        harness.Desktop.ExplorerRunning = true;
        await Assert.ThrowsAsync<IOException>(() => harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop)));
        Assert.Contains(nameof(RecordingDesktopOperations.StopExplorer), harness.Desktop.Calls);
        Assert.Contains(nameof(RecordingDesktopOperations.StartExplorer), harness.Desktop.Calls);
        Assert.Contains(nameof(RecordingDesktopOperations.PlaceIcons), harness.Desktop.Calls);
    }
    [Fact]
    public async Task ExistingRecoveryRecordCannotBeOverwrittenByPrepare()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession();
        string original = File.ReadAllText(harness.Sessions.FilePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop)));
        Assert.Equal(original, File.ReadAllText(harness.Sessions.FilePath));
        Assert.Empty(harness.Desktop.Calls);
    }

    [Fact]
    public async Task MissingLiveSnapshotRefusesRestoreBeforeMovingShortcuts()
    {
        using var harness = WorkflowHarness.Create();
        var session = harness.SaveSession();
        harness.Sessions.Save(session with { SwitchMode = DesktopSwitchMode.Live, CapturedIconCount = 2 });
        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => harness.RestoreWorkflow().RunAsync());
        Assert.True(harness.Sessions.Exists);
        Assert.Empty(harness.Desktop.Calls);
    }

    [Fact]
    public async Task DeclinedElevationNeverShowsTransition()
    {
        using var harness = WorkflowHarness.Create();
        harness.Elevation.Outcome = ElevationOutcome.Declined;
        bool shown = false;
        await Assert.ThrowsAsync<ElevationDeclinedException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop), beforeDesktopChange: _ => { shown = true; return Task.CompletedTask; }));
        Assert.False(shown);
    }

    [Fact]
    public async Task TransitionIsShownBeforeAnyDesktopMutation()
    {
        using var harness = WorkflowHarness.Create();
        bool shown = false;
        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, SwitchMode: DesktopSwitchMode.Live),
            beforeDesktopChange: _ =>
            {
                Assert.Empty(harness.Desktop.WrittenDesktopPaths);
                shown = true;
                return Task.CompletedTask;
            });
        Assert.True(shown);
    }
    [Fact]
    public async Task PrepareCapturesShellIdentitiesAndDoesNotRestartExplorer()
    {
        using var harness = WorkflowHarness.Create();
        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, SwitchMode: DesktopSwitchMode.Live));
        Assert.Contains(nameof(RecordingDesktopOperations.ReadShellIcons), harness.Desktop.Calls);
        Assert.Contains(nameof(RecordingDesktopOperations.SwitchLiveDesktop), harness.Desktop.Calls);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.RestartExplorer), harness.Desktop.Calls);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.WriteDesktopPath), harness.Desktop.Calls);
        Assert.Equal(DesktopSwitchMode.Live, harness.Sessions.Load()!.SwitchMode);
    }

    [Fact]
    public async Task RestoreUsesRecordedLiveModeAndNeverWritesNativeBlob()
    {
        using var harness = WorkflowHarness.Create();
        var session = harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot());
        harness.Sessions.Save(session with { SwitchMode = DesktopSwitchMode.Live });
        var result = await harness.RestoreWorkflow().RunAsync();
        Assert.True(result.Layout!.IsExact);
        Assert.Contains(nameof(RecordingDesktopOperations.SwitchLiveDesktop), harness.Desktop.Calls);
        Assert.Contains(nameof(RecordingDesktopOperations.PlaceShellIcons), harness.Desktop.Calls);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.StopExplorer), harness.Desktop.Calls);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.WriteIconLayoutBlob), harness.Desktop.Calls);
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task IncompleteLiveLayoutKeepsRecoveryRecord()
    {
        using var harness = WorkflowHarness.Create();
        var session = harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot());
        harness.Sessions.Save(session with { SwitchMode = DesktopSwitchMode.Live });
        harness.Desktop.ShellPlacement = new(2, 1, [], ["Beta"], false, true);
        await Assert.ThrowsAsync<RestoreIncompleteException>(() => harness.RestoreWorkflow().RunAsync());
        Assert.True(harness.Sessions.Exists);
    }

    [Fact]
    public async Task FailedRollbackElevationDoesNotDiscardRecoveryRecord()
    {
        using var harness = WorkflowHarness.Create();
        harness.Desktop.ThrowOn(nameof(RecordingDesktopOperations.RestartExplorer));
        harness.Elevation.OutcomeFor = action => action == ElevatedOperations.RestoreShortcuts
            ? ElevationOutcome.Declined : ElevationOutcome.Succeeded;
        await Assert.ThrowsAsync<IOException>(() => harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop)));
        Assert.True(harness.Sessions.Exists);
    }

    [Fact]
    public async Task FailedElevatedMoveMayBePartialAndKeepsRecoveryRecord()
    {
        using var harness = WorkflowHarness.Create();
        harness.Elevation.Outcome = ElevationOutcome.Failed;
        await Assert.ThrowsAsync<ElevationDeclinedException>(() =>
            harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop)));
        Assert.True(harness.Sessions.Exists);
        Assert.Empty(harness.Desktop.WrittenDesktopPaths);
    }

    [Fact]
    public async Task CompatibilityRestoreStartsShellEvenIfStoppingThrows()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession();
        File.WriteAllBytes(Path.Combine(harness.Paths.RecoveryDirectory, "IconLayouts.bin"), [1]);
        harness.Desktop.ThrowOn(nameof(RecordingDesktopOperations.StopExplorer));
        await Assert.ThrowsAsync<IOException>(() => harness.RestoreWorkflow().RunAsync());
        Assert.Contains(nameof(RecordingDesktopOperations.StartExplorer), harness.Desktop.Calls);
        Assert.True(harness.Sessions.Exists);
    }
}
