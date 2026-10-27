using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public sealed class AutomaticSnapshotWorkflowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SessionRecordsWhetherThisSwitchCapturedAPreview(bool capture)
    {
        using var harness = WorkflowHarness.Create();
        File.WriteAllText(Path.Combine(harness.Paths.StateDirectory, "return-preview.png"), "old preview");
        await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, CapturePreview: capture));
        Assert.Equal(capture, harness.Sessions.Load()!.HasReturnPreview);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(7, false)]
    [InlineData(15, false)]
    [InlineData(30, false)]
    public async Task SuccessfulSwitchPrunesOnlyExpiredHistoryAndProtectsItsActiveSnapshot(int retention, bool oldRemains)
    {
        using var harness = WorkflowHarness.Create();
        var initial = await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, CapturePreview: false));
        var snapshot = harness.Layouts.Load(Path.GetFileNameWithoutExtension(initial.AutomaticSnapshotPath!))!;
        harness.Sessions.Clear();
        string old = harness.Layouts.SaveAutomatic(snapshot with
        {
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-40),
            Icons = [new("Old icon", 900, 900)],
        }, DateTimeOffset.UtcNow.AddDays(-40));
        bool readyBeforePruning = false;
        var result = await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, CapturePreview: false,
            AutomaticSnapshotRetentionDays: retention), desktopReady: () => readyBeforePruning = File.Exists(old));
        Assert.True(readyBeforePruning);
        Assert.Equal(oldRemains, File.Exists(old));
        Assert.True(File.Exists(result.AutomaticSnapshotPath));
        Assert.Equal(result.AutomaticSnapshotPath, harness.Sessions.Load()!.AutoLayoutSnapshotPath);
    }

    [Fact]
    public async Task RepeatedSwitchWithUnchangedLayoutReusesItsRecoverySnapshot()
    {
        using var harness = WorkflowHarness.Create();
        var request = new PrepareRequest(harness.MeetingDesktop, CapturePreview: false, SwitchMode: DesktopSwitchMode.Live);
        var first = await harness.PrepareWorkflow().RunAsync(request);
        harness.Sessions.Clear(); // Fake desktop operations never changed the real desktop.
        var second = await harness.PrepareWorkflow().RunAsync(request);
        Assert.Equal(first.AutomaticSnapshotPath, second.AutomaticSnapshotPath);
        Assert.Single(harness.Layouts.List());
        Assert.Equal(second.AutomaticSnapshotPath, harness.Sessions.Load()!.AutoLayoutSnapshotPath);
    }
}
