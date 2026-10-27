using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public class CompatibilityReadinessWorkflowTests
{
    private sealed class Reports : IProgress<ProgressReport>
    {
        public List<ProgressKey> Keys { get; } = [];
        public void Report(ProgressReport value) => Keys.Add(value.Key);
    }

    [Fact]
    public async Task PrepareCannotReportDoneWhileShellStillShowsPersonalFolder()
    {
        using var harness = WorkflowHarness.Create();
        var reports = new Reports();
        var failure = new TimeoutException("Registry is target, active Shell folder is still personal");
        harness.Desktop.Placement = new(2, 1, [], ["Beta"], false, true);
        harness.Desktop.OnWaitForCompatibleDesktop = (path, expected, _) =>
        {
            if (path == harness.MeetingDesktop)
            {
                Assert.Null(expected);
                Assert.Equal(harness.MeetingDesktop, harness.Desktop.WrittenDesktopPaths[^1]);
                throw failure;
            }
            Assert.Equal(harness.OriginalDesktop, path);
            Assert.Equal(2, expected!.Count);
        };

        var error = await Assert.ThrowsAsync<TimeoutException>(() => harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop), reports));

        Assert.Same(failure, error);
        Assert.DoesNotContain(ProgressKey.Done, reports.Keys);
        Assert.Equal(new[] { harness.MeetingDesktop, harness.OriginalDesktop }, harness.Desktop.ReadinessRequests.Select(r => r.Path));
        Assert.Equal(harness.OriginalDesktop, harness.Desktop.WrittenDesktopPaths[^1]);
        Assert.Equal(new[] { ElevatedOperations.MoveShortcutsAside, ElevatedOperations.RestoreShortcuts }, harness.Elevation.Requests);
        Assert.True(harness.Sessions.Exists);
    }

    [Fact]
    public async Task PrepareReadinessFailureClearsSessionOnlyAfterVerifiedExactRollback()
    {
        using var harness = WorkflowHarness.Create();
        harness.Desktop.OnWaitForCompatibleDesktop = (path, _, _) =>
        {
            if (path == harness.MeetingDesktop) throw new TimeoutException("Stale Shell folder");
        };

        await Assert.ThrowsAsync<TimeoutException>(() => harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop)));

        Assert.Contains(nameof(RecordingDesktopOperations.PlaceIcons), harness.Desktop.Calls);
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task RollbackReadinessFailureRetainsSessionAndStillRestoresWallpaper()
    {
        using var harness = WorkflowHarness.Create();
        var previous = new WallpaperState(@"C:\previous.png", "6", "0");
        harness.Desktop.Wallpaper = previous;
        harness.Desktop.OnWaitForCompatibleDesktop = (_, _, _) => throw new TimeoutException("No verified Shell folder");

        await Assert.ThrowsAsync<TimeoutException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop, MeetingWallpaperPath: harness.CreateMeetingWallpaper())));

        Assert.Equal(previous, harness.Desktop.AppliedWallpapers[^1]);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceIcons), harness.Desktop.Calls);
        Assert.True(harness.Sessions.Exists);
    }

    [Fact]
    public async Task RestoreWaitsForEverySavedIconBeforePlacement()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot());
        bool ready = false;
        harness.Desktop.OnWaitForCompatibleDesktop = (path, expected, _) =>
        {
            Assert.Equal(harness.OriginalDesktop, path);
            Assert.Equal(new[] { "Alpha", "Beta" }, expected!.Select(i => i.Name));
            Assert.Contains(nameof(RecordingDesktopOperations.StartExplorer), harness.Desktop.Calls);
            Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceIcons), harness.Desktop.Calls);
            ready = true;
        };
        harness.Desktop.OnCall = call =>
        {
            if (call == nameof(RecordingDesktopOperations.PlaceIcons)) Assert.True(ready);
        };

        await harness.RestoreWorkflow().RunAsync();

        Assert.True(ready);
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task IncompleteEnumerationDoesNotPlaceIconsOrClearRestoreRecord()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession(wallpaperChanged: true, snapshotPath: harness.CreateLayoutSnapshot());
        string record = File.ReadAllText(harness.Sessions.FilePath);
        var reports = new Reports();
        harness.Desktop.OnWaitForCompatibleDesktop = (_, _, _) => throw new TimeoutException("Only 42 of 141 icons loaded");

        await Assert.ThrowsAsync<TimeoutException>(() => harness.RestoreWorkflow().RunAsync(reports));

        Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceIcons), harness.Desktop.Calls);
        Assert.DoesNotContain(ProgressKey.Done, reports.Keys);
        Assert.Equal(record, File.ReadAllText(harness.Sessions.FilePath));
        harness.Desktop.OnWaitForCompatibleDesktop = null;
        Assert.True((await harness.RestoreWorkflow().RunAsync()).Layout!.IsExact);
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task CancelledReadinessRunsRollbackWithUncancelledToken()
    {
        using var harness = WorkflowHarness.Create();
        using var cancellation = new CancellationTokenSource();
        harness.Desktop.OnWaitForCompatibleDesktop = (path, _, token) =>
        {
            if (path == harness.MeetingDesktop) cancellation.Cancel();
            else Assert.False(token.CanBeCanceled);
            token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop), cancellationToken: cancellation.Token));

        Assert.False(harness.Sessions.Exists);
        Assert.Equal(2, harness.Desktop.ReadinessRequests.Count);
    }

    [Fact]
    public async Task SuccessfulPrepareChecksReadinessAfterRestartBeforeDone()
    {
        using var harness = WorkflowHarness.Create();
        var reports = new Reports();
        harness.Desktop.OnWaitForCompatibleDesktop = (path, _, _) =>
        {
            Assert.Equal(harness.MeetingDesktop, path);
            Assert.Contains(nameof(RecordingDesktopOperations.RestartExplorer), harness.Desktop.Calls);
            Assert.DoesNotContain(ProgressKey.Done, reports.Keys);
        };

        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop), reports);

        Assert.Single(harness.Desktop.ReadinessRequests);
        Assert.Equal(ProgressKey.Done, reports.Keys[^1]);
    }

    [Fact]
    public async Task LiveModeDoesNotInvokeCompatibilityReadiness()
    {
        using var harness = WorkflowHarness.Create();
        harness.Desktop.ThrowOn(nameof(RecordingDesktopOperations.WaitForCompatibleDesktop));
        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, SwitchMode: DesktopSwitchMode.Live));
        await harness.RestoreWorkflow().RunAsync();
        Assert.Empty(harness.Desktop.ReadinessRequests);
    }

    [Fact]
    public async Task LegacyRestoreStillRequiresVerifiedFolderBeforeDone()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession();
        var reports = new Reports();
        harness.Desktop.OnWaitForCompatibleDesktop = (path, expected, _) =>
        {
            Assert.Equal(harness.OriginalDesktop, path);
            Assert.Null(expected);
            Assert.DoesNotContain(ProgressKey.Done, reports.Keys);
            throw new TimeoutException("Virtual Desktop alias does not prove the physical folder");
        };

        await Assert.ThrowsAsync<TimeoutException>(() => harness.RestoreWorkflow().RunAsync(reports));

        Assert.Single(harness.Desktop.ReadinessRequests);
        Assert.True(harness.Sessions.Exists);
        Assert.DoesNotContain(ProgressKey.Done, reports.Keys);
    }

    [Fact]
    public async Task CancelledRestoreReadinessRetainsRecordAndDoesNotPlaceIcons()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot());
        string record = File.ReadAllText(harness.Sessions.FilePath);
        using var cancellation = new CancellationTokenSource();
        harness.Desktop.OnWaitForCompatibleDesktop = (_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RestoreWorkflow().RunAsync(cancellationToken: cancellation.Token));

        Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceIcons), harness.Desktop.Calls);
        Assert.Equal(record, File.ReadAllText(harness.Sessions.FilePath));
    }
}
