using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

/// <summary>
/// Tests for the order a prepare runs in.
/// </summary>
/// <remarks>
/// <para>
/// These do not test that the swap works - only a real machine can show that.
/// They test the property that makes a failed swap survivable:
/// <b>nothing outside the app's own data folder is touched until the UAC prompt
/// has been accepted, and once it has been, a failure unwinds.</b>
/// </para>
/// <para>
/// Both halves of that were broken in the same file at the same time. The
/// wallpaper was applied <em>above</em> the elevation guard, so dismissing the
/// prompt kept the meeting background on a desktop that reported "nothing was
/// changed"; and the error path cleared the session file that held the note of
/// what the wallpaper had been, so there was nothing left that knew how to put
/// it back. A test asserting the call order is the only thing that keeps a
/// reordering from silently reintroducing either.
/// </para>
/// </remarks>
public class PrepareWorkflowTests
{
    [Fact]
    public async Task MakesNoChangeOutsideAppData_BeforeThePromptIsAccepted()
    {
        // The load-bearing invariant. Everything up to the prompt is either
        // read-only or confined to the app's own data folder, so a user who
        // dismisses the prompt - or closes the window, or loses power - is left
        // with a desktop that was never touched, and the message saying so is
        // true.
        using var harness = WorkflowHarness.Create();
        harness.Desktop.Wallpaper = new WallpaperState(@"C:\before.png", "10", "0");

        IReadOnlyList<string> callsAtPrompt = [];
        harness.Elevation.OnRun = _ => callsAtPrompt = [.. harness.Desktop.Calls];

        await harness.PrepareWorkflow().RunAsync(
            new PrepareRequest(harness.MeetingDesktop, MeetingWallpaperPath: harness.CreateMeetingWallpaper()));

        Assert.Equal([ElevatedOperations.MoveShortcutsAside], harness.Elevation.Requests);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.ApplyWallpaper), callsAtPrompt);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.WriteDesktopPath), callsAtPrompt);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.RestartExplorer), callsAtPrompt);
    }

    [Fact]
    public async Task RecordsTheSession_BeforeThePrompt_SoAnInterruptedSwapIsRecoverable()
    {
        // The mirror of the test above. Nothing may be *changed* before the
        // prompt, but the record of what is about to be changed has to exist by
        // then, or a crash during the prompt leaves a swap with no way back.
        using var harness = WorkflowHarness.Create();

        bool sessionExistedAtPrompt = false;
        harness.Elevation.OnRun = _ => sessionExistedAtPrompt = harness.Sessions.Exists;

        await harness.PrepareWorkflow().RunAsync(new PrepareRequest(harness.MeetingDesktop));

        Assert.True(sessionExistedAtPrompt);

        SessionInfo? session = harness.Sessions.Load();
        Assert.NotNull(session);
        Assert.Equal(harness.OriginalDesktop, session.OriginalDesktopPath);
        Assert.Equal(harness.MeetingDesktop, session.MeetingDesktopPathAtSwap);
    }

    [Fact]
    public async Task ChangesNothing_WhenThePromptIsDismissed()
    {
        using var harness = WorkflowHarness.Create();
        harness.Elevation.Outcome = ElevationOutcome.Declined;

        await Assert.ThrowsAsync<ElevationDeclinedException>(() =>
            harness.PrepareWorkflow().RunAsync(
                new PrepareRequest(harness.MeetingDesktop, MeetingWallpaperPath: harness.CreateMeetingWallpaper())));

        Assert.Empty(harness.Desktop.AppliedWallpapers);
        Assert.Empty(harness.Desktop.WrittenDesktopPaths);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.RestartExplorer), harness.Desktop.Calls);

        // The session file is the app's own bookkeeping, and it described a
        // swap that did not happen. Clearing it is correct *because* of the
        // test above this one: there is no wallpaper change left unrecorded.
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task AppliesTheMeetingWallpaper_AfterThePromptIsAccepted()
    {
        using var harness = WorkflowHarness.Create();
        harness.Desktop.Wallpaper = new WallpaperState(@"C:\before.png", "10", "0");
        string wallpaper = harness.CreateMeetingWallpaper();

        await harness.PrepareWorkflow().RunAsync(
            new PrepareRequest(harness.MeetingDesktop, MeetingWallpaperPath: wallpaper));

        Assert.Equal(new WallpaperState(wallpaper, "10", "0"), Assert.Single(harness.Desktop.AppliedWallpapers));
        Assert.True(harness.Desktop.IndexOf(nameof(RecordingDesktopOperations.ApplyWallpaper)) >
                    harness.Desktop.IndexOf(nameof(RecordingDesktopOperations.ReadWallpaper)));
    }

    [Fact]
    public async Task SkipsAnIdenticalWallpaper()
    {
        // Re-applying the picture that is already up repaints the whole desktop
        // for no change. Restore makes the same judgement on the way back.
        using var harness = WorkflowHarness.Create();
        string wallpaper = harness.CreateMeetingWallpaper();
        harness.Desktop.Wallpaper = new WallpaperState(wallpaper, "10", "0");

        await harness.PrepareWorkflow().RunAsync(
            new PrepareRequest(harness.MeetingDesktop, MeetingWallpaperPath: wallpaper));

        Assert.Empty(harness.Desktop.AppliedWallpapers);
        Assert.False(harness.Sessions.Load()!.WallpaperChanged);
    }

    [Fact]
    public async Task LeavesAnUnconfiguredWallpaperAlone()
    {
        using var harness = WorkflowHarness.Create();
        harness.Desktop.Wallpaper = new WallpaperState(@"C:\before.png", "10", "0");

        await harness.PrepareWorkflow().RunAsync(new PrepareRequest(harness.MeetingDesktop));

        // Read, so the session can hold it, but never applied.
        Assert.Contains(nameof(RecordingDesktopOperations.ReadWallpaper), harness.Desktop.Calls);
        Assert.Empty(harness.Desktop.AppliedWallpapers);

        SessionInfo session = harness.Sessions.Load()!;
        Assert.False(session.WallpaperChanged);
        Assert.Equal(new WallpaperState(@"C:\before.png", "10", "0"), session.WallpaperAtSwap);
    }

    [Fact]
    public async Task RetainsTheRecord_WhenTheWallpaperApplyReportsFailure()
    {
        // Failure does not prove that no part of the wallpaper was changed.
        using var harness = WorkflowHarness.Create();
        harness.Desktop.Wallpaper = new WallpaperState(@"C:\before.png", "10", "0");
        harness.Desktop.ApplyWallpaperSucceeds = false;

        await harness.PrepareWorkflow().RunAsync(
            new PrepareRequest(harness.MeetingDesktop, MeetingWallpaperPath: harness.CreateMeetingWallpaper()));

        Assert.True(harness.Sessions.Load()!.WallpaperChanged);
    }

    [Fact]
    public async Task RollsBackTheParkedShortcuts_WhenALaterStepFails()
    {
        // Past the prompt the shortcuts are parked, so a failure with no unwind
        // leaves a desktop missing them and no route back except a manual
        // second UAC prompt - at the worst possible moment.
        using var harness = WorkflowHarness.Create();
        harness.Desktop.ThrowOn(nameof(RecordingDesktopOperations.RestartExplorer));
        harness.Desktop.ExplorerRunning = false;

        await Assert.ThrowsAsync<IOException>(() =>
            harness.PrepareWorkflow().RunAsync(new PrepareRequest(harness.MeetingDesktop)));

        Assert.Equal(
            [ElevatedOperations.MoveShortcutsAside, ElevatedOperations.RestoreShortcuts],
            harness.Elevation.Requests);

        // The desktop path is put back...
        Assert.Equal(harness.OriginalDesktop, harness.Desktop.WrittenDesktopPaths[^1]);

        // ...the shell is brought back up, because StopThenStart may have
        // thrown between its two halves...
        Assert.Contains(nameof(RecordingDesktopOperations.StartExplorer), harness.Desktop.Calls);

        // ...and the session file goes, because the machine is where it started.
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task KeepsTheSessionFile_WhenTheRollBackItselfFails()
    {
        // The session file is what makes the next launch report Inconsistent
        // rather than presenting a half-swapped machine as normal, so it must
        // survive an unwind that did not finish.
        using var harness = WorkflowHarness.Create();
        harness.Desktop.ThrowOn(nameof(RecordingDesktopOperations.RestartExplorer));

        int runs = 0;
        harness.Elevation.OnRun = _ =>
        {
            if (++runs == 2)
            {
                throw new InvalidOperationException("the elevated process could not be launched");
            }
        };

        await Assert.ThrowsAsync<IOException>(() =>
            harness.PrepareWorkflow().RunAsync(new PrepareRequest(harness.MeetingDesktop)));

        Assert.True(harness.Sessions.Exists);
    }

    [Fact]
    public async Task VerifiesCompatibleDesktopAfterRestart_OnTheHappyPath()
    {
        using var harness = WorkflowHarness.Create();
        harness.Desktop.IconLayoutBlob = [0x01, 0x02];

        await harness.PrepareWorkflow().RunAsync(new PrepareRequest(harness.MeetingDesktop));

        Assert.Equal(
            new[] { nameof(RecordingDesktopOperations.CapturePreview), nameof(RecordingDesktopOperations.ReadIcons) },
            harness.Desktop.Calls.Take(2).Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                nameof(RecordingDesktopOperations.ReadWallpaper),
                nameof(RecordingDesktopOperations.WriteDesktopPath),
                nameof(RecordingDesktopOperations.ReadIconLayoutBlob),
                nameof(RecordingDesktopOperations.ExportIconLayoutBlob),
                nameof(RecordingDesktopOperations.RestartExplorer),
                nameof(RecordingDesktopOperations.WaitForCompatibleDesktop),
            ],
            harness.Desktop.Calls.Skip(2));

        Assert.Equal(harness.MeetingDesktop, harness.Desktop.WrittenDesktopPaths[^1]);
    }
}
