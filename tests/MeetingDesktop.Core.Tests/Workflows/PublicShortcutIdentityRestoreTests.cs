using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public sealed class PublicShortcutIdentityRestoreTests
{
    private static string PublicDir(WorkflowHarness h) => Path.Combine(h.Root, "PublicDesktop");
    private static string BackupDir(WorkflowHarness h) => Path.Combine(h.Root, "Backup");
    private static RestoreWorkflow Restore(WorkflowHarness h) => new(h.Paths, h.Layouts, h.Sessions, h.Elevation, h.Desktop,
        publicShortcutDirectory: PublicDir(h), shortcutBackupDirectory: BackupDir(h));

    private static ShortcutConflict Conflict(WorkflowHarness h) => new(
        Path.Combine(BackupDir(h), "Alpha.lnk"),
        Path.Combine(PublicDir(h), "Alpha.lnk"),
        Path.Combine(PublicDir(h), "Alpha (restored 1).lnk"));

    private static void Save(WorkflowHarness h, IconPosition icon)
    {
        string path = h.Layouts.SaveAutomatic(new LayoutSnapshot { IsAutomatic = true, CreatedUtc = DateTimeOffset.UtcNow,
            Topology = MonitorTopology.Empty, Icons = [icon] }, DateTimeOffset.Now);
        h.Sessions.Save(h.SaveSession() with { AutoLayoutSnapshotPath = path, CapturedIconCount = 1 });
    }

    [Fact]
    public async Task ConflictUsesPreservedIdentityAndSurvivesRetry()
    {
        using var h = WorkflowHarness.Create();
        var c = Conflict(h);
        Save(h, new("Alpha", 10, 20, c.RequestedPath, true));
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(true, new(1, [c]));
        h.Desktop.Placement = new(1, 0, [], ["Alpha"], false, true);
        await Assert.ThrowsAsync<RestoreIncompleteException>(() => Restore(h).RunAsync());
        Assert.Equal(c.PreservedPath, Assert.Single(h.Desktop.ReadinessRequests.Last().Expected!).Identity);
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(true, new(0, []));
        h.Desktop.Placement = null;
        await Restore(h).RunAsync();
        Assert.Equal(c.PreservedPath, Assert.Single(h.Desktop.ReadinessRequests.Last().Expected!).Identity);
    }

    [Fact]
    public async Task PartialFailurePersistsConflictBeforeRetry()
    {
        using var h = WorkflowHarness.Create();
        var c = Conflict(h);
        Save(h, new("Alpha", 10, 20, c.RequestedPath, true));
        h.Elevation.Outcome = ElevationOutcome.Failed;
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(false, new(1, [c]), 1, Path.Combine(BackupDir(h), "Beta.lnk"));
        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => Restore(h).RunAsync());
        h.Elevation.Outcome = ElevationOutcome.Succeeded;
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(true, new(0, []));
        await Restore(h).RunAsync();
        Assert.Equal(c.PreservedPath, Assert.Single(h.Desktop.ReadinessRequests.Last().Expected!).Identity);
    }

    [Fact]
    public async Task IdentitylessCaptureAndCollisionMustNotBeDeclaredExact()
    {
        using var h = WorkflowHarness.Create();
        Save(h, new("Alpha", 10, 20));
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(true, new(1, [Conflict(h)]));
        var error = await Assert.ThrowsAsync<RestoreIncompleteException>(() => Restore(h).RunAsync());
        Assert.True(h.Sessions.Exists);
        Assert.Contains(h.OriginalDesktop, h.Desktop.WrittenDesktopPaths);
        Assert.False(error.Result.Layout!.IsExact);
        Assert.Contains("Alpha", error.Result.Layout.NotFound);
        Assert.Equal("Alpha", Assert.Single(error.Result.UnmappedIcons));
    }


    [Fact]
    public async Task CancelAfterProgressBeforeDispatchCanRetry()
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ProgressReport>(report =>
        {
            if (report.Key == ProgressKey.RestoringPublicShortcuts) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Restore(h).RunAsync(progress, cancellation.Token));
        Assert.Empty(h.Elevation.Requests);
        Assert.False(h.Sessions.Load()!.ShortcutRestoreUncertain);
        await Restore(h).RunAsync();
        Assert.False(h.Sessions.Exists);
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    [Fact]
    public async Task InvalidConflictIsNeverUsedAndRetainsSession()
    {
        using var h = WorkflowHarness.Create();
        var c = Conflict(h) with { PreservedPath = Path.Combine(h.Root, "elsewhere.lnk") };
        Save(h, new("Alpha", 10, 20, c.RequestedPath, true));
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(true, new(1, [c]));
        await Assert.ThrowsAnyAsync<Exception>(() => Restore(h).RunAsync());
        Assert.True(h.Sessions.Exists);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceIcons), h.Desktop.Calls);
    }
    [Fact]
    public async Task MissingReportKeepsUncertaintyAcrossRetry()
    {
        using var h = WorkflowHarness.Create();
        Save(h, new("Alpha", 10, 20, Conflict(h).RequestedPath, true));
        h.Elevation.Outcome = ElevationOutcome.Failed;
        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => Restore(h).RunAsync());
        Assert.True(h.Sessions.Load()!.ShortcutRestoreUncertain);
        Assert.True(h.Sessions.Load()!.SchemaVersion > 1, "Older releases must refuse a record with uncertain shortcut state.");
        h.Elevation.Outcome = ElevationOutcome.Succeeded;
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(true, new(0, []));
        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => Restore(h).RunAsync());
        Assert.True(h.Sessions.Exists);
        Assert.Single(h.Elevation.Requests);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.PlaceIcons), h.Desktop.Calls);
    }

    [Fact]
    public async Task StaleReportIsNotAuthenticatedForCurrentRun()
    {
        using var h = WorkflowHarness.Create();
        Save(h, new("Alpha", 10, 20, Conflict(h).RequestedPath, true));
        h.Elevation.LastReport = new(true, new(1, [Conflict(h)]));
        h.Elevation.AutoReport = false;
        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => Restore(h).RunAsync());
        Assert.True(h.Sessions.Load()!.ShortcutRestoreUncertain);
        Assert.Empty(h.Desktop.Calls);
    }

    [Fact]
    public async Task LegacySessionWithFreshEmptyReportRestoresNormally()
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession();
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(true, new(0, []));
        await Restore(h).RunAsync();
        Assert.False(h.Sessions.Exists);
    }

    [Fact]
    public async Task InvalidPersistedMappingIsRejectedBeforeBrokerRuns()
    {
        using var h = WorkflowHarness.Create();
        var session = h.SaveSession();
        h.Sessions.Save(session with { RestoredShortcutConflicts = [Conflict(h) with { SourcePath = Path.Combine(h.Root, "Alpha.lnk") }] });
        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => Restore(h).RunAsync());
        Assert.Empty(h.Elevation.Requests);
        Assert.True(h.Sessions.Exists);
    }

    [Fact]
    public async Task DeclinedPromptRestoresPriorRecordWithoutIntroducingUncertainty()
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession();
        h.Elevation.Outcome = ElevationOutcome.Declined;
        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => Restore(h).RunAsync());
        Assert.False(h.Sessions.Load()!.ShortcutRestoreUncertain);
        Assert.Empty(h.Desktop.Calls);
    }
    [Fact]
    public async Task ContradictorySuccessReportCannotClearSession()
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession();
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(false, new(0, []), 1);
        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => Restore(h).RunAsync());
        Assert.True(h.Sessions.Load()!.ShortcutRestoreUncertain);
        Assert.Empty(h.Desktop.Calls);
    }
    [Fact]
    public async Task UnclassifiedFailureReportCannotProveNoMoves()
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession();
        h.Elevation.Outcome = ElevationOutcome.Failed;
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(false, new(0, []));
        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => Restore(h).RunAsync());
        Assert.True(h.Sessions.Load()!.ShortcutRestoreUncertain);
    }

    [Fact]
    public async Task CollisionMovesOnlyTemporaryFilesAndRetryUsesPersistedIdentity()
    {
        using var h = WorkflowHarness.Create();
        Directory.CreateDirectory(PublicDir(h));
        Directory.CreateDirectory(BackupDir(h));
        var c = Conflict(h);
        File.WriteAllText(c.SourcePath, "original");
        File.WriteAllText(c.RequestedPath, "new occupant");
        Save(h, new("Alpha", 10, 20, c.RequestedPath, true));
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(true,
            PublicDesktopService.MoveShortcuts(BackupDir(h), PublicDir(h)));
        h.Desktop.Placement = new(1, 0, [], ["Alpha"], false, true);
        await Assert.ThrowsAsync<RestoreIncompleteException>(() => Restore(h).RunAsync());
        Assert.Equal("new occupant", File.ReadAllText(c.RequestedPath));
        Assert.Equal("original", File.ReadAllText(c.PreservedPath));
        Assert.Equal(c.PreservedPath, Assert.Single(h.Sessions.Load()!.RestoredShortcutConflicts!).PreservedPath);
        h.Desktop.Placement = null;
        await Restore(h).RunAsync();
        Assert.Equal(c.PreservedPath, Assert.Single(h.Desktop.ReadinessRequests.Last().Expected!).Identity);
        Assert.Equal("new occupant", File.ReadAllText(c.RequestedPath));
        Assert.Equal("original", File.ReadAllText(c.PreservedPath));
    }

    [Fact]
    public async Task CollisionWithNoFilesystemIdentityCannotBeDeclaredExact()
    {
        using var h = WorkflowHarness.Create();
        var c = Conflict(h);
        Save(h, new("Alpha", 10, 20, c.RequestedPath, false));
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(true, new(1, [c]));
        var error = await Assert.ThrowsAsync<RestoreIncompleteException>(() => Restore(h).RunAsync());
        Assert.True(h.Sessions.Exists);
        Assert.Contains(h.OriginalDesktop, h.Desktop.WrittenDesktopPaths);
        Assert.Contains("Alpha", error.Result.Layout!.NotFound);
    }

    [Fact]
    public async Task MixedLegacyCollisionRestoresSafeIdentitiesAndReportsUnmappedAcrossRetry()
    {
        using var h = WorkflowHarness.Create();
        var c = Conflict(h);
        var safe = new IconPosition("Beta", 30, 40, Path.Combine(h.OriginalDesktop, "Beta.lnk"), true);
        string path = h.Layouts.SaveAutomatic(new LayoutSnapshot { IsAutomatic = true, CreatedUtc = DateTimeOffset.UtcNow,
            Topology = MonitorTopology.Empty, Icons = [new("Alpha", 10, 20), safe] }, DateTimeOffset.Now);
        h.Sessions.Save(h.SaveSession() with { AutoLayoutSnapshotPath = path, CapturedIconCount = 2 });
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(true, new(1, [c]));
        var first = await Assert.ThrowsAsync<RestoreIncompleteException>(() => Restore(h).RunAsync());
        Assert.Equal("Alpha", Assert.Single(first.Result.Layout!.NotFound));
        Assert.Equal(safe, Assert.Single(h.Desktop.ReadinessRequests.Last().Expected!));
        Assert.Equal(2, first.Result.Layout.Requested);
        h.Elevation.OnRun = _ => h.Elevation.LastReport = new(true, new(0, []));
        await Assert.ThrowsAsync<RestoreIncompleteException>(() => Restore(h).RunAsync());
        Assert.True(h.Sessions.Exists);
        Assert.Equal(c, Assert.Single(h.Sessions.Load()!.RestoredShortcutConflicts!));
    }

    [Fact]
    public async Task ProvenNotDispatchedCanBeRetriedAfterAuthorization()
    {
        using var h = WorkflowHarness.Create();
        h.SaveSession();
        var broker = new NoSessionBroker();
        var workflow = new RestoreWorkflow(h.Paths, h.Layouts, h.Sessions, broker, h.Desktop,
            publicShortcutDirectory: PublicDir(h), shortcutBackupDirectory: BackupDir(h));
        await Assert.ThrowsAsync<RestoreNotPossibleException>(() => workflow.RunAsync());
        Assert.False(h.Sessions.Load()!.ShortcutRestoreUncertain);
        Assert.Empty(h.Desktop.Calls);
        broker.Authorized = true;
        await workflow.RunAsync();
        Assert.False(h.Sessions.Exists);
    }

    private sealed class NoSessionBroker : IElevationBroker
    {
        public bool Authorized { get; set; }
        public ElevatedOperationReport? LastReport { get; private set; }
        public ElevationOutcome Run(string argument) => RunWithEvidence(argument).Outcome;
        public ElevationRunResult RunWithEvidence(string argument)
        {
            if (!Authorized) return new(ElevationOutcome.Failed, true);
            LastReport = new(true, new(0, []));
            return new(ElevationOutcome.Succeeded);
        }
    }
}
