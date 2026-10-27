using MeetingDesktop.App.Services;

namespace MeetingDesktop.App.Tests;

public sealed class LiveIconTransitionTests
{
    [Fact]
    public async Task UnavailablePreparationDoesNotSendHideOrDelayFallback()
    {
        var commands = new List<string>();
        using var transition = new LiveIconTransition((command, _) =>
        {
            commands.Add(command);
            return Task.FromResult(false);
        });
        Task hide = transition.HideAsync(CancellationToken.None);
        Assert.True(hide.IsCompletedSuccessfully);
        await hide;
        Assert.Equal(new[] { "begin", "end" }, commands);
    }

    [Fact]
    public async Task DisposalWhileWaitingCannotApplyALateHide()
    {
        var begin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int hides = 0;
        using var transition = new LiveIconTransition((command, _) =>
        {
            if (command == "begin") return begin.Task;
            if (command == "hide") hides++;
            if (command == "end") released.TrySetResult();
            return Task.FromResult(true);
        });
        Task hide = transition.HideAsync(CancellationToken.None);
        transition.Dispose();
        begin.SetResult(true);
        await hide.WaitAsync(TimeSpan.FromSeconds(2));
        await released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, hides);
    }

    [Fact]
    public async Task CancellationDuringPreparationDoesNotHideAfterTheCallerHasLeft()
    {
        var begin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int hides = 0;
        using var transition = new LiveIconTransition((command, _) =>
        {
            if (command == "begin") return begin.Task;
            if (command == "hide") hides++;
            if (command == "end") released.TrySetResult();
            return Task.FromResult(true);
        });
        using var cancellation = new CancellationTokenSource();
        Task hide = transition.HideAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hide);
        transition.Dispose();
        begin.SetResult(true);
        await released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, hides);
    }

    [Fact]
    public async Task InFlightPreparationMustFinishBeforeFolderChangeCanStart()
    {
        var begin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int hides = 0;
        using var transition = new LiveIconTransition((command, _) =>
        {
            if (command == "begin") return begin.Task;
            if (command == "hide") hides++;
            if (command == "end") released.TrySetResult();
            return Task.FromResult(true);
        });
        Task hide = transition.HideAsync(CancellationToken.None);
        Assert.False(hide.IsCompleted);
        Assert.False(begin.Task.IsCompleted);
        begin.SetResult(true);
        await hide.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, hides);
        Assert.False(released.Task.IsCompleted);
        transition.Dispose();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, hides);
    }

    [Fact]
    public async Task SuccessfulSwitchRevealsAfterHideWithoutWaitingForRevealReply()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new List<string>();
        var transition = new LiveIconTransition((command, _) =>
        {
            commands.Add(command);
            return command == "end" ? release.Task : Task.FromResult(true);
        });
        await transition.HideAsync(CancellationToken.None);
        transition.Dispose();
        Assert.Equal(new[] { "begin", "hide", "end" }, commands);
        Assert.False(release.Task.IsCompleted);
        release.SetResult(true);
        transition.Dispose();
        Assert.Equal(3, commands.Count);
    }

    [Fact]
    public async Task CancellationReleasesTheAlreadyAcquiredLayer()
    {
        var commands = new List<string>();
        var transition = new LiveIconTransition((command, _) => { commands.Add(command); return Task.FromResult(true); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transition.HideAsync(new CancellationToken(true)));
        transition.Dispose();
        Assert.Equal(new[] { "begin", "end" }, commands);
    }
}
