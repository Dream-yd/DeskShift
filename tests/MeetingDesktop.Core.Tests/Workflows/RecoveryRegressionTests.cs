using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public class RecoveryRegressionTests
{
    [Fact]
    public void ImportingAnotherWallpaperPreservesEveryProfileAsset()
    {
        using var harness = WorkflowHarness.Create();
        string source = Path.Combine(harness.Root, "poster.jpg");
        string assets = Path.Combine(harness.Root, "assets");
        var service = new WallpaperService();
        File.WriteAllBytes(source, [1, 2, 3]);
        string first = Assert.IsType<string>(service.Import(source, assets));
        var profile = new DesktopProfile("First", harness.MeetingDesktop, first);

        File.WriteAllBytes(source, [4, 5, 6]);
        string second = Assert.IsType<string>(service.Import(source, assets));

        Assert.NotEqual(first, second);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(profile.Wallpaper!));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(second));
        Assert.Equal(".jpg", Path.GetExtension(second));
    }

    [Fact]
    public async Task LegacyRestoreIgnoresBlobLeftByAnEarlierSession()
    {
        using var harness = WorkflowHarness.Create();
        string record = Path.Combine(harness.Root, "original_desktop_path.txt");
        File.WriteAllText(record, harness.OriginalDesktop);
        harness.Sessions.Save(LegacyRecoveryRecord.Read(record, harness.MeetingDesktop));
        string blob = Path.Combine(harness.Paths.RecoveryDirectory, "IconLayouts.bin");
        File.WriteAllBytes(blob, [42]);

        var result = await harness.RestoreWorkflow().RunAsync();

        Assert.Null(result.Layout);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.ImportIconLayoutBlob), harness.Desktop.Calls);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.WriteIconLayoutBlob), harness.Desktop.Calls);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(blob));
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task CoordinateSnapshotDoesNotProveSharedBlobOwnership()
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot());
        File.WriteAllBytes(Path.Combine(harness.Paths.RecoveryDirectory, "IconLayouts.bin"), [42]);

        var result = await harness.RestoreWorkflow().RunAsync();

        Assert.True(result.Layout!.IsExact);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.ImportIconLayoutBlob), harness.Desktop.Calls);
        Assert.DoesNotContain(nameof(RecordingDesktopOperations.WriteIconLayoutBlob), harness.Desktop.Calls);
    }

    [Fact]
    public async Task InexactLiveRestoreCarriesDetailsAndKeepsRecordForRetry()
    {
        using var harness = WorkflowHarness.Create();
        var session = harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot(), wallpaperChanged: true);
        harness.Sessions.Save(session with { SwitchMode = DesktopSwitchMode.Live });
        string before = File.ReadAllText(harness.Sessions.FilePath);
        File.WriteAllText(Path.Combine(harness.MeetingDesktop, "notes.txt"), "presenter notes");
        harness.Desktop.ShellPlacement = new(2, 1, [new("Alpha", 0, 0, 10, 20)], ["Beta"], false, true);

        var error = await Assert.ThrowsAsync<RestoreIncompleteException>(() => harness.RestoreWorkflow().RunAsync());
        var result = error.Result;

        Assert.Same(harness.Desktop.ShellPlacement, result.Layout);
        Assert.Equal(harness.OriginalDesktop, result.RestoredDesktopPath);
        Assert.Equal(new[] { "notes.txt" }, result.FilesLeftOnMeetingDesktop);
        Assert.Equal(before, File.ReadAllText(harness.Sessions.FilePath));
        Assert.NotEmpty(harness.Desktop.AppliedWallpapers);

        harness.Desktop.ShellPlacement = null;
        Assert.True((await harness.RestoreWorkflow().RunAsync()).Layout!.IsExact);
        Assert.False(harness.Sessions.Exists);
    }
}
