using System.Collections.Concurrent;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests.Workflows;

public sealed class WorkflowResponsivenessTests
{
    [Fact]
    public void SlowWallpaperRestoreLeavesTheCallerResponsive()
    {
        using var context = new CallerContext();
        context.Run(async () =>
        {
            using var harness = WorkflowHarness.Create();
            harness.SaveSession(wallpaperChanged: true);
            using var dispatched = new ManualResetEventSlim();
            harness.Desktop.OnCall = call =>
            {
                if (call != nameof(RecordingDesktopOperations.ApplyWallpaper)) return;
                Assert.NotSame(context, SynchronizationContext.Current);
                context.Post(_ => dispatched.Set(), null);
                Assert.True(dispatched.Wait(2000), "Wallpaper recovery must not block input dispatch.");
            };
            var result = await harness.RestoreWorkflow().RunAsync();
            Assert.True(result.WallpaperRestored);
            Assert.False(harness.Sessions.Exists);
        });
    }

    [Fact]
    public void SlowPreparationReadLeavesTheCallerResponsiveAndKeepsTheVisualCallbackOnItsContext()
    {
        using var context = new CallerContext();
        context.Run(async () =>
        {
            using var harness = WorkflowHarness.Create();
            using var dispatched = new ManualResetEventSlim();
            bool visualCallback = false;
            harness.Desktop.OnCall = call =>
            {
                if (call != nameof(RecordingDesktopOperations.ReadWallpaper)) return;
                Assert.NotSame(context, SynchronizationContext.Current);
                context.Post(_ => dispatched.Set(), null);
                Assert.True(dispatched.Wait(2000), "The caller must dispatch input while preparation reads are blocked.");
            };
            await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, CapturePreview: false,
                SwitchMode: DesktopSwitchMode.Live), beforeDesktopChange: _ =>
                {
                    Assert.Same(context, SynchronizationContext.Current);
                    Assert.True(harness.Sessions.Exists);
                    visualCallback = true;
                    return Task.CompletedTask;
                });
            Assert.True(visualCallback);
        });
    }

    private sealed class CallerContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add(() => d(state));
        public void Run(Func<Task> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                Task task = action();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!task.IsCompleted)
                {
                    if (_queue.TryTake(out var callback, 100)) callback();
                    Assert.True(DateTime.UtcNow < deadline, "Caller context stalled.");
                }
                task.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); }
        }
        public void Dispose() => _queue.Dispose();
    }
}
