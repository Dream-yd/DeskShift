using System.Text.Json.Nodes;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public class WorkflowSafetyTests
{
    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer, "missing")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "unrecorded")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "corrupt")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "incomplete")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "null-icons")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "null-icon")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "null-topology")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "missing-topology")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "missing-monitors")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "null-monitors")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "null-monitor")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "missing-coordinate")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "blank-name")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "new-schema")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "wrong-path")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "locked")]
    [InlineData(DesktopSwitchMode.Live, "corrupt")]
    [InlineData(DesktopSwitchMode.Live, "incomplete")]
    [InlineData(DesktopSwitchMode.Live, "null-icons")]
    [InlineData(DesktopSwitchMode.Live, "wrong-path")]
    public async Task InvalidSavedCoordinatesRefuseBeforeAnyMutation(DesktopSwitchMode mode, string fault)
    {
        using var harness = WorkflowHarness.Create();
        string path = harness.CreateLayoutSnapshot();
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        switch (fault)
        {
            case "missing": break;
            case "unrecorded": break;
            case "corrupt": File.WriteAllText(path, "{truncated"); break;
            case "wrong-path":
                // An unrelated file with the same stem must not authorize the store's copy.
                string other = Path.Combine(harness.Root, Path.GetFileName(path));
                File.WriteAllText(other, "{}");
                path = other;
                break;
            default:
                switch (fault)
                {
                    case "incomplete": json["Icons"]!.AsArray().RemoveAt(1); break;
                    case "null-icons": json["Icons"] = null; break;
                    case "null-icon": json["Icons"]![0] = null; break;
                    case "null-topology": json["Topology"] = null; break;
                    case "missing-topology": json.AsObject().Remove("Topology"); break;
                    case "missing-monitors": json["Topology"]!.AsObject().Remove("Monitors"); break;
                    case "null-monitors": json["Topology"]!["Monitors"] = null; break;
                    case "null-monitor": json["Topology"]!["Monitors"] = new JsonArray((JsonNode?)null); break;
                    case "missing-coordinate": json["Icons"]![0]!.AsObject().Remove("X"); break;
                    case "blank-name": json["Icons"]![0]!["Name"] = " "; break;
                    case "new-schema": json["SchemaVersion"] = 999; break;
                }
                File.WriteAllText(path, json.ToJsonString());
                break;
        }
        var session = harness.SaveSession(snapshotPath: path);
        harness.Sessions.Save(session with
        {
            SwitchMode = mode,
            AutoLayoutSnapshotPath = fault == "unrecorded" ? null : path,
        });
        if (fault == "missing") File.Delete(path);
        string record = File.ReadAllText(harness.Sessions.FilePath);
        bool transitionShown = false;
        using var locked = fault == "locked" ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;

        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => harness.RestoreWorkflow().RunAsync(
            beforeDesktopChange: _ => { transitionShown = true; return Task.CompletedTask; }));

        Assert.Empty(harness.Elevation.Requests);
        Assert.Empty(harness.Desktop.Calls);
        Assert.False(transitionShown);
        Assert.Equal(record, File.ReadAllText(harness.Sessions.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompatibilityPartialRestoreRetainsDetailsAndCanBeRetried(bool missingIcon)
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession(wallpaperChanged: true, snapshotPath: harness.CreateLayoutSnapshot());
        string record = File.ReadAllText(harness.Sessions.FilePath);
        File.WriteAllText(Path.Combine(harness.MeetingDesktop, "notes.txt"), "keep me");
        File.WriteAllText(Path.Combine(harness.MeetingDesktop, "desktop.ini"), "hidden");
        harness.Desktop.Placement = missingIcon
            ? new(2, 1, [], ["Beta"], true, true)
            : new(2, 2, [new("Alpha", 0, 0, 10, 20)], [], true, true);

        var error = await Assert.ThrowsAsync<RestoreIncompleteException>(() => harness.RestoreWorkflow().RunAsync());

        Assert.Same(harness.Desktop.Placement, error.Result.Layout);
        Assert.Equal(harness.OriginalDesktop, error.Result.RestoredDesktopPath);
        Assert.Equal(new[] { "notes.txt" }, error.Result.FilesLeftOnMeetingDesktop);
        Assert.Single(harness.Desktop.AppliedWallpapers);
        Assert.Equal(record, File.ReadAllText(harness.Sessions.FilePath));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(harness.MeetingDesktop, "notes.txt")));

        harness.Desktop.Placement = null;
        Assert.True((await harness.RestoreWorkflow().RunAsync()).Layout!.IsExact);
        Assert.False(harness.Sessions.Exists);
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer, false)]
    [InlineData(DesktopSwitchMode.RestartExplorer, true)]
    [InlineData(DesktopSwitchMode.Live, false)]
    [InlineData(DesktopSwitchMode.Live, true)]
    public async Task WallpaperFailurePreservesRecoveryAndAllowsRetry(DesktopSwitchMode mode, bool throws)
    {
        using var harness = WorkflowHarness.Create();
        var session = harness.SaveSession(wallpaperChanged: true, snapshotPath: harness.CreateLayoutSnapshot());
        harness.Sessions.Save(session with { SwitchMode = mode });
        string record = File.ReadAllText(harness.Sessions.FilePath);
        harness.Desktop.ApplyWallpaperSucceeds = false;
        var failure = new IOException("Wallpaper restore failed");
        harness.Desktop.OnCall = call =>
        {
            if (throws && call == nameof(RecordingDesktopOperations.ApplyWallpaper)) throw failure;
        };

        var error = await Assert.ThrowsAsync<RestoreIncompleteException>(() => harness.RestoreWorkflow().RunAsync());
        Assert.False(error.Result.WallpaperRestored);
        Assert.True(error.Result.Layout!.IsExact);
        Assert.Equal(throws ? failure : null, error.InnerException);
        Assert.Equal(record, File.ReadAllText(harness.Sessions.FilePath));
        Assert.Contains(mode == DesktopSwitchMode.Live ? nameof(RecordingDesktopOperations.PlaceShellIcons)
            : nameof(RecordingDesktopOperations.PlaceIcons), harness.Desktop.Calls);

        harness.Desktop.OnCall = null;
        harness.Desktop.ApplyWallpaperSucceeds = true;
        Assert.True((await harness.RestoreWorkflow().RunAsync()).Layout!.IsExact);
        Assert.False(harness.Sessions.Exists);
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    [InlineData(DesktopSwitchMode.Live)]
    public async Task CancelledTransitionUnwindsPrepareWithoutUsingCancelledToken(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop, false, SwitchMode: mode), cancellationToken: cancellation.Token,
            beforeDesktopChange: token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }));

        Assert.Equal(new[] { ElevatedOperations.MoveShortcutsAside, ElevatedOperations.RestoreShortcuts }, harness.Elevation.Requests);
        Assert.Equal(harness.OriginalDesktop, harness.Desktop.WrittenDesktopPaths[^1]);
        Assert.All(harness.Desktop.Tokens.Skip(1), observed => Assert.False(observed.Token.CanBeCanceled));
        Assert.False(harness.Sessions.Exists);
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    [InlineData(DesktopSwitchMode.Live)]
    public async Task CancellationReturningFromTransitionCannotApplyMeetingWallpaper(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        using var cancellation = new CancellationTokenSource();
        var previous = new WallpaperState(@"C:\before.png", "6", "1");
        harness.Desktop.Wallpaper = previous;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper(), mode), cancellationToken: cancellation.Token,
            beforeDesktopChange: _ => { cancellation.Cancel(); return Task.CompletedTask; }));

        Assert.DoesNotContain(harness.Desktop.AppliedWallpapers, state => !state.Matches(previous));
        Assert.False(harness.Sessions.Exists);
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer, "placement-cancelled")]
    [InlineData(DesktopSwitchMode.Live, "placement-cancelled")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "placement-inexact")]
    [InlineData(DesktopSwitchMode.Live, "placement-inexact")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "wallpaper-false")]
    [InlineData(DesktopSwitchMode.Live, "wallpaper-false")]
    [InlineData(DesktopSwitchMode.RestartExplorer, "wallpaper-throws")]
    [InlineData(DesktopSwitchMode.Live, "wallpaper-throws")]
    public async Task IncompletePrepareRollbackPreservesOriginalFailureAndRecovery(DesktopSwitchMode mode, string fault)
    {
        using var harness = WorkflowHarness.Create();
        var failure = new IOException("Original transition failure");
        var previous = new WallpaperState(@"C:\before.png", "6", "1");
        harness.Desktop.Wallpaper = previous;
        var inexact = new PlaceResult(2, 1, [], ["Beta"], false, true);
        if (fault == "placement-inexact") harness.Desktop.Placement = harness.Desktop.ShellPlacement = inexact;
        if (fault == "wallpaper-false") harness.Desktop.ApplyWallpaperSucceeds = false;
        harness.Desktop.OnCall = call =>
        {
            if (fault == "placement-cancelled" && call is nameof(RecordingDesktopOperations.PlaceIcons) or nameof(RecordingDesktopOperations.PlaceShellIcons))
                throw new OperationCanceledException("Rollback operation was cancelled independently");
            if (fault == "wallpaper-throws" && call == nameof(RecordingDesktopOperations.ApplyWallpaper))
                throw new IOException("Rollback wallpaper failed");
        };

        var error = await Assert.ThrowsAsync<IOException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper(), mode),
            beforeDesktopChange: _ => throw failure));

        Assert.Same(failure, error);
        Assert.True(harness.Sessions.Exists);
        Assert.True(harness.Sessions.Load()!.WallpaperChanged);
        Assert.Equal(previous, harness.Sessions.Load()!.WallpaperAtSwap);
        Assert.Contains(nameof(RecordingDesktopOperations.ApplyWallpaper), harness.Desktop.Calls);
        if (fault != "wallpaper-throws") Assert.Equal(previous, Assert.Single(harness.Desktop.AppliedWallpapers));
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    [InlineData(DesktopSwitchMode.Live)]
    public async Task CancelledRestoreTransitionPreservesRecordWithoutRedirectingDesktop(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        var session = harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot());
        harness.Sessions.Save(session with { SwitchMode = mode });
        string record = File.ReadAllText(harness.Sessions.FilePath);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RestoreWorkflow().RunAsync(
            cancellationToken: cancellation.Token,
            beforeDesktopChange: _ => { cancellation.Cancel(); return Task.CompletedTask; }));

        Assert.Empty(harness.Desktop.WrittenDesktopPaths);
        Assert.Equal(record, File.ReadAllText(harness.Sessions.FilePath));
    }

    [Fact]
    public async Task FailedRestoreElevationAdmitsPossiblePartialShortcutMoves()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot());
        harness.Elevation.Outcome = ElevationOutcome.Failed;
        var error = await Assert.ThrowsAsync<RestoreNotPossibleException>(() => harness.RestoreWorkflow().RunAsync());
        Assert.DoesNotContain("nothing was changed", error.Message);
        Assert.True(harness.Sessions.Exists);
        Assert.Empty(harness.Desktop.Calls);
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    [InlineData(DesktopSwitchMode.Live)]
    public async Task CancellationDuringPrepareSwitchRollsBackInsteadOfReportingSuccess(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        using var cancellation = new CancellationTokenSource();
        bool cancelled = false;
        harness.Desktop.OnCall = call =>
        {
            if (!cancelled && call == (mode == DesktopSwitchMode.Live
                ? nameof(RecordingDesktopOperations.SwitchLiveDesktop) : nameof(RecordingDesktopOperations.RestartExplorer)))
            {
                cancelled = true;
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop, false, SwitchMode: mode), cancellationToken: cancellation.Token));

        Assert.Equal(new[] { ElevatedOperations.MoveShortcutsAside, ElevatedOperations.RestoreShortcuts }, harness.Elevation.Requests);
        Assert.Equal(harness.OriginalDesktop, harness.Desktop.WrittenDesktopPaths[^1]);
        Assert.False(harness.Sessions.Exists);
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    [InlineData(DesktopSwitchMode.Live)]
    public async Task CancellationDuringRestorePlacementRetainsRecoveryForRetry(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        var session = harness.SaveSession(wallpaperChanged: true, snapshotPath: harness.CreateLayoutSnapshot());
        harness.Sessions.Save(session with { SwitchMode = mode });
        string record = File.ReadAllText(harness.Sessions.FilePath);
        using var cancellation = new CancellationTokenSource();
        harness.Desktop.OnCall = call =>
        {
            if (call is nameof(RecordingDesktopOperations.PlaceIcons) or nameof(RecordingDesktopOperations.PlaceShellIcons))
                cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RestoreWorkflow().RunAsync(cancellationToken: cancellation.Token));

        Assert.Equal(record, File.ReadAllText(harness.Sessions.FilePath));
        Assert.Single(harness.Desktop.AppliedWallpapers);
        harness.Desktop.OnCall = null;
        await harness.RestoreWorkflow().RunAsync();
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task CancellationDuringCompatibilityStopStillStartsShellAndRetainsRecord()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot());
        using var cancellation = new CancellationTokenSource();
        harness.Desktop.OnCall = call =>
        {
            if (call == nameof(RecordingDesktopOperations.StopExplorer))
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RestoreWorkflow().RunAsync(cancellationToken: cancellation.Token));

        Assert.Contains(nameof(RecordingDesktopOperations.StartExplorer), harness.Desktop.Calls);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceIcons), harness.Desktop.Calls);
        Assert.True(harness.Sessions.Exists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancelledWorkflowMakesNoDesktopOrElevationCalls(bool restore)
    {
        using var harness = WorkflowHarness.Create();
        if (restore) harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore
            ? (Task)harness.RestoreWorkflow().RunAsync(cancellationToken: cancellation.Token)
            : harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop), cancellationToken: cancellation.Token));

        Assert.Empty(harness.Desktop.Calls);
        Assert.Empty(harness.Elevation.Requests);
        Assert.Equal(restore, harness.Sessions.Exists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousElevationFailureRetainsRecordWithoutFurtherDesktopChanges(bool restore)
    {
        using var harness = WorkflowHarness.Create();
        if (restore) harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot());
        var failure = new IOException("Helper may have moved shortcuts before losing contact");
        harness.Elevation.OnRun = _ => throw failure;

        var error = await Assert.ThrowsAsync<IOException>(() => restore
            ? (Task)harness.RestoreWorkflow().RunAsync()
            : harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false)));

        Assert.Same(failure, error);
        Assert.True(harness.Sessions.Exists);
        Assert.Empty(harness.Desktop.WrittenDesktopPaths);
        Assert.Empty(harness.Desktop.AppliedWallpapers);
        Assert.Single(harness.Elevation.Requests);
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    [InlineData(DesktopSwitchMode.Live)]
    public async Task EmptyCapturedLayoutIsAValidRestore(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        string snapshot = harness.Layouts.SaveAutomatic(new() { Icons = [] }, DateTimeOffset.Now);
        var session = harness.SaveSession(snapshotPath: snapshot);
        harness.Sessions.Save(session with { CapturedIconCount = 0, SwitchMode = mode });

        var result = await harness.RestoreWorkflow().RunAsync();

        Assert.Null(result.Layout);
        Assert.False(harness.Sessions.Exists);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceIcons), harness.Desktop.Calls);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceShellIcons), harness.Desktop.Calls);
    }
}
