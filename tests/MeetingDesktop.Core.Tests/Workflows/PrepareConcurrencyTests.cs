using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests.Workflows;

public sealed class PrepareConcurrencyTests
{
    [Theory]
    [InlineData(DesktopSwitchMode.Live)]
    [InlineData(DesktopSwitchMode.RestartExplorer)]
    public async Task LayoutAndPreviewOverlapButBothFinishBeforeAnyDesktopChange(DesktopSwitchMode mode)
    {
        using var harness = WorkflowHarness.Create();
        using var release = new ManualResetEventSlim();
        var layoutStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Desktop.OnCall = call =>
        {
            if (call is nameof(RecordingDesktopOperations.ReadIcons) or nameof(RecordingDesktopOperations.ReadShellIcons))
                layoutStarted.TrySetResult();
            else if (call == nameof(RecordingDesktopOperations.CapturePreview)) previewStarted.TrySetResult();
            else return;
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test release missing.");
        };
        var run = harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, SwitchMode: mode));
        var both = Task.WhenAll(layoutStarted.Task, previewStarted.Task);
        bool overlapped;
        try
        {
            overlapped = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(2))) == both;
            Assert.Empty(harness.Elevation.Requests);
            Assert.False(harness.Sessions.Exists);
        }
        finally { release.Set(); await run; }
        Assert.True(overlapped, "Preview must start while the layout read is still in flight.");
        Assert.NotNull((await run).PreviewPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLayoutWaitsForPreviewWriterBeforeReturning(bool cancel)
    {
        using var harness = WorkflowHarness.Create();
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var previewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var layoutFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Desktop.OnCall = call =>
        {
            if (call == nameof(RecordingDesktopOperations.CapturePreview))
            {
                previewStarted.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test release missing.");
            }
            if (call != nameof(RecordingDesktopOperations.ReadShellIcons)) return;
            if (!previewStarted.Task.Wait(TimeSpan.FromSeconds(2))) throw new IOException("Preview never started.");
            layoutFailed.TrySetResult();
            if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw new IOException("Layout unreadable.");
        };
        var run = harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, SwitchMode: DesktopSwitchMode.Live),
            cancellationToken: cancellation.Token);
        bool pendingAfterFailure = false;
        try
        {
            if (await Task.WhenAny(layoutFailed.Task, Task.Delay(TimeSpan.FromSeconds(3))) == layoutFailed.Task)
            {
                await Task.Delay(50);
                pendingAfterFailure = !run.IsCompleted;
            }
        }
        finally { release.Set(); }
        if (cancel && layoutFailed.Task.IsCompleted)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        else await Assert.ThrowsAsync<IOException>(() => run);
        Assert.True(pendingAfterFailure, "Failure must join the active preview writer before a retry can begin.");
        Assert.False(harness.Sessions.Exists);
        Assert.Empty(harness.Elevation.Requests);
        Assert.Empty(harness.Desktop.WrittenDesktopPaths);
    }
}
