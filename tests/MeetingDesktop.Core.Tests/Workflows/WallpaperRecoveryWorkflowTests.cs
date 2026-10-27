using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public class WallpaperRecoveryWorkflowTests
{
    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    [InlineData(DesktopSwitchMode.Live)]
    public async Task PartialApplyFailureIsRestoredAfterSuccessfulPrepare(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        string meeting = harness.CreateMeetingWallpaper();
        harness.Desktop.ApplyWallpaperResultFor = state =>
        {
            if (state.Path == meeting)
            {
                harness.Desktop.Wallpaper = original with { Style = state.Style, Tile = state.Tile };
                return false;
            }
            harness.Desktop.Wallpaper = state;
            return true;
        };

        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, meeting, mode));

        Assert.Equal(original with { Style = "10", Tile = "0" }, harness.Desktop.Wallpaper);
        Assert.True(harness.Sessions.Load()!.WallpaperChanged);
        await harness.RestoreWorkflow().RunAsync();
        Assert.Equal(original, harness.Desktop.Wallpaper);
        Assert.False(harness.Sessions.Exists);
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    [InlineData(DesktopSwitchMode.Live)]
    public async Task PartialApplyIsRestoredWhenLaterSwitchFails(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        string meeting = harness.CreateMeetingWallpaper();
        harness.Desktop.ApplyWallpaperResultFor = state =>
        {
            if (state.Path == meeting)
            {
                harness.Desktop.Wallpaper = original with { Style = state.Style, Tile = state.Tile };
                return false;
            }
            harness.Desktop.Wallpaper = state;
            return true;
        };
        bool failedSwitch = false;
        harness.Desktop.OnCall = call =>
        {
            if (!failedSwitch && call == (mode == DesktopSwitchMode.Live
                ? nameof(RecordingDesktopOperations.SwitchLiveDesktop)
                : nameof(RecordingDesktopOperations.RestartExplorer)))
            {
                failedSwitch = true;
                throw new IOException("Synthetic switch failure");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop, false, meeting, mode)));

        Assert.Equal(original, harness.Desktop.Wallpaper);
        Assert.Equal(original, harness.Desktop.AppliedWallpapers[^1]);
        Assert.False(harness.Sessions.Exists);
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    [InlineData(DesktopSwitchMode.Live)]
    public async Task PartialApplyAndFailedRollbackKeepWallpaperRecoveryForRetry(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        string meeting = harness.CreateMeetingWallpaper();
        bool failRecovery = true;
        harness.Desktop.ApplyWallpaperResultFor = state =>
        {
            if (state.Path == meeting)
            {
                harness.Desktop.Wallpaper = original with { Style = state.Style, Tile = state.Tile };
                return false;
            }
            if (failRecovery) return false;
            harness.Desktop.Wallpaper = state;
            return true;
        };
        bool failedSwitch = false;
        harness.Desktop.OnCall = call =>
        {
            if (!failedSwitch && call == (mode == DesktopSwitchMode.Live
                ? nameof(RecordingDesktopOperations.SwitchLiveDesktop)
                : nameof(RecordingDesktopOperations.RestartExplorer)))
            {
                failedSwitch = true;
                throw new IOException("Synthetic switch failure");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop, false, meeting, mode)));

        Assert.Equal(original with { Style = "10", Tile = "0" }, harness.Desktop.Wallpaper);
        Assert.Equal(original, harness.Desktop.AppliedWallpapers[^1]);
        Assert.True(harness.Sessions.Load()!.WallpaperChanged);
        failRecovery = false;
        await harness.RestoreWorkflow().RunAsync();
        Assert.Equal(original, harness.Desktop.Wallpaper);
        Assert.False(harness.Sessions.Exists);
    }

    private static WallpaperState Original(WorkflowHarness harness)
    {
        string path = Path.Combine(harness.Root, "engine-generated.jpg");
        File.WriteAllBytes(path, [0xff, 0xd8, 0xff, 1, 2, 3]);
        var state = new WallpaperState(path, "6", "1");
        harness.Desktop.Wallpaper = state;
        harness.Desktop.ApplyWallpaperResultFor = wallpaper => wallpaper.IsEmpty || File.Exists(wallpaper.Path);
        return state;
    }

    [Fact]
    public async Task CapturesOriginalImageBeforeElevationOrDesktopMutation()
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        byte[] bytes = File.ReadAllBytes(original.Path!);
        harness.Elevation.OnRun = _ =>
        {
            string backup = Assert.IsType<string>(harness.Sessions.Load()!.WallpaperBackupPath);
            Assert.StartsWith(harness.Paths.RecoveryDirectory, backup);
            Assert.NotEqual(original.Path, backup);
            Assert.Equal(bytes, File.ReadAllBytes(backup));
            Assert.Empty(harness.Desktop.AppliedWallpapers);
            Assert.Empty(harness.Desktop.WrittenDesktopPaths);
        };

        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper()));

        Assert.Equal(bytes, File.ReadAllBytes(original.Path!));
    }

    [Theory]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    [InlineData(DesktopSwitchMode.Live)]
    public async Task DeletedGeneratedOriginalUsesPrivateBackupWithSameStyleAndTile(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        byte[] bytes = File.ReadAllBytes(original.Path!);
        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper(), mode));
        string backup = Assert.IsType<string>(harness.Sessions.Load()!.WallpaperBackupPath);
        File.Delete(original.Path!);

        var result = await harness.RestoreWorkflow().RunAsync();

        Assert.True(result.WallpaperRestored);
        Assert.Equal(original with { Path = backup }, harness.Desktop.AppliedWallpapers[^1]);
        Assert.Equal(bytes, File.ReadAllBytes(backup));
        Assert.False(File.Exists(original.Path));
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task RollbackUsesBackupWhenRestartDeletesGeneratedOriginal()
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        var failure = new IOException("Explorer restart failed after the engine removed its old image");
        string? backup = null;
        harness.Desktop.OnCall = call =>
        {
            if (call == nameof(RecordingDesktopOperations.RestartExplorer))
            {
                backup = harness.Sessions.Load()!.WallpaperBackupPath;
                File.Delete(original.Path!);
                throw failure;
            }
        };

        var error = await Assert.ThrowsAsync<IOException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper())));

        Assert.Same(failure, error);
        Assert.NotNull(backup);
        Assert.Equal(original with { Path = backup }, harness.Desktop.AppliedWallpapers[^1]);
        Assert.False(File.Exists(original.Path));
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task MissingOriginalAndBackupRetainSessionForRetry()
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper()));
        string backup = Assert.IsType<string>(harness.Sessions.Load()!.WallpaperBackupPath);
        File.Delete(original.Path!);
        File.Delete(backup);
        string record = File.ReadAllText(harness.Sessions.FilePath);

        var error = await Assert.ThrowsAsync<RestoreIncompleteException>(() => harness.RestoreWorkflow().RunAsync());

        Assert.False(error.Result.WallpaperRestored);
        Assert.Equal(record, File.ReadAllText(harness.Sessions.FilePath));
        Assert.False(File.Exists(original.Path));
    }

    [Fact]
    public async Task ForeignBackupCannotBeUsedEvenWithSameFileName()
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper()));
        var session = harness.Sessions.Load()!;
        string owned = Assert.IsType<string>(session.WallpaperBackupPath);
        string foreign = Path.Combine(harness.Root, Path.GetFileName(owned));
        File.Copy(owned, foreign);
        File.Delete(original.Path!);
        harness.Sessions.Save(session with { WallpaperBackupPath = foreign });

        var error = await Assert.ThrowsAsync<RestoreIncompleteException>(() => harness.RestoreWorkflow().RunAsync());

        Assert.False(error.Result.WallpaperRestored);
        Assert.DoesNotContain(harness.Desktop.AppliedWallpapers, wallpaper => wallpaper.Path == foreign);
        Assert.True(harness.Sessions.Exists);
    }

    [Fact]
    public async Task ExistingOriginalIsPreferredAndNeverOverwritten()
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper()));
        File.WriteAllBytes(original.Path!, [9, 8, 7]);
        await harness.RestoreWorkflow().RunAsync();
        Assert.Equal(original, harness.Desktop.AppliedWallpapers[^1]);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(original.Path!));
    }

    [Fact]
    public async Task DefaultDynamicWallpaperNeverAppliesAnImageEvenIfGeneratedOriginalDisappears()
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false));
        Assert.False(harness.Sessions.Load()!.WallpaperChanged);
        File.Delete(original.Path!);
        await harness.RestoreWorkflow().RunAsync();
        Assert.Empty(harness.Desktop.AppliedWallpapers);
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task UnreadableOriginalWarnsButPrepareRemainsBestEffort()
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        using (var locked = new FileStream(original.Path!, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper()));
            Assert.True(result.WallpaperBackupUnavailable);
            Assert.Null(harness.Sessions.Load()!.WallpaperBackupPath);
            Assert.True(harness.Sessions.Load()!.WallpaperChanged);
        }
        var restored = await harness.RestoreWorkflow().RunAsync();
        Assert.True(restored.WallpaperRestored);
        Assert.Equal(original, harness.Desktop.AppliedWallpapers[^1]);
    }

    [Fact]
    public async Task OversizeOriginalIsNotCopied()
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        using (var source = new FileStream(original.Path!, FileMode.Open, FileAccess.Write))
            source.SetLength(WallpaperRecoverySnapshot.MaximumBytes + 1);
        var result = await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper()));
        Assert.True(result.WallpaperBackupUnavailable);
        Assert.Null(harness.Sessions.Load()!.WallpaperBackupPath);
        Assert.Empty(Directory.GetFiles(harness.Paths.RecoveryDirectory, "wallpaper-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CapturesAreUniqueAndPreserveEarlierBytes()
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        byte[] firstBytes = File.ReadAllBytes(original.Path!);
        string first = Assert.IsType<string>(await WallpaperRecoverySnapshot.CaptureAsync(harness.Paths, original, default));
        File.WriteAllBytes(original.Path!, [4, 5, 6]);
        string second = Assert.IsType<string>(await WallpaperRecoverySnapshot.CaptureAsync(harness.Paths, original, default));
        Assert.NotEqual(first, second);
        Assert.Equal(firstBytes, File.ReadAllBytes(first));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(second));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(original.Path!));
    }

    [Fact]
    public async Task CancellationDoesNotCreateACopy()
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WallpaperRecoverySnapshot.CaptureAsync(harness.Paths, original, cancellation.Token));
        Assert.Empty(Directory.GetFiles(harness.Paths.RecoveryDirectory, "wallpaper-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("locked")]
    [InlineData("wrong-name")]
    public async Task UnusablePrivateCopyCannotReportSuccessfulRecovery(string fault)
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper()));
        var session = harness.Sessions.Load()!;
        string backup = Assert.IsType<string>(session.WallpaperBackupPath);
        File.Delete(original.Path!);
        if (fault == "empty") File.WriteAllBytes(backup, []);
        if (fault == "wrong-name")
        {
            string other = Path.Combine(Path.GetDirectoryName(backup)!, "unrelated.jpg");
            File.Copy(backup, other);
            harness.Sessions.Save(session with { WallpaperBackupPath = other });
        }
        using var locked = fault == "locked" ? new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var error = await Assert.ThrowsAsync<RestoreIncompleteException>(() => harness.RestoreWorkflow().RunAsync());
        Assert.False(error.Result.WallpaperRestored);
        Assert.True(harness.Sessions.Exists);
        Assert.Equal(original, harness.Desktop.AppliedWallpapers[^1]);
    }

    [Fact]
    public async Task RollbackWithoutOriginalOrBackupKeepsRecoveryCredentials()
    {
        using var harness = WorkflowHarness.Create();
        var original = Original(harness);
        harness.Desktop.OnCall = call =>
        {
            if (call == nameof(RecordingDesktopOperations.RestartExplorer))
            {
                File.Delete(original.Path!);
                File.Delete(harness.Sessions.Load()!.WallpaperBackupPath!);
                throw new IOException("Restart failed");
            }
        };
        await Assert.ThrowsAsync<IOException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop, false, harness.CreateMeetingWallpaper())));
        Assert.True(harness.Sessions.Exists);
        Assert.True(harness.Sessions.Load()!.WallpaperChanged);
        Assert.Equal(original, harness.Sessions.Load()!.WallpaperAtSwap);
    }
}
