using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests.Workflows;

public sealed class PrepareCancellationRecordTests
{
    [Fact]
    public async Task CancellationBeforeBrokerStartsDoesNotLeaveARecoveryRecord()
    {
        using var harness = WorkflowHarness.Create();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(report =>
        {
            if (report.Key == ProgressKey.MovingPublicShortcuts) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop, false), progress, cancellation.Token));
        Assert.Empty(harness.Elevation.Requests);
        Assert.Empty(harness.Desktop.WrittenDesktopPaths);
        Assert.False(harness.Sessions.Exists);
    }

    [Fact]
    public async Task CancellationInsideBrokerKeepsRecordBecauseShortcutsMayHaveMoved()
    {
        using var harness = WorkflowHarness.Create();
        using var cancellation = new CancellationTokenSource();
        harness.Elevation.OnRun = _ => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.PrepareWorkflow().RunAsync(
            new(harness.MeetingDesktop, false), cancellationToken: cancellation.Token));
        Assert.Single(harness.Elevation.Requests);
        Assert.Empty(harness.Desktop.WrittenDesktopPaths);
        Assert.True(harness.Sessions.Exists);
    }

    private sealed class InlineProgress(Action<ProgressReport> report) : IProgress<ProgressReport>
    {
        public void Report(ProgressReport value) => report(value);
    }
}
