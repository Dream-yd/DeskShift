using MeetingDesktop.App.Services;
using MeetingDesktop.Core.Models;
using System.IO;

namespace MeetingDesktop.App.Tests;

public sealed class QuickSwitchControllerTests
{
    [Theory]
    [InlineData(SessionState.Normal, false, false, "prepare")]
    [InlineData(SessionState.Swapped, true, false, "restore")]
    [InlineData(SessionState.Inconsistent, true, false, "review")]
    [InlineData(SessionState.Adopted, false, false, "review")]
    [InlineData(SessionState.Swapped, false, false, "review")]
    [InlineData(SessionState.Normal, true, false, "review")]
    [InlineData(SessionState.Normal, false, true, "")]
    public async Task DispatchesOnlyAnUnambiguousOperation(SessionState session, bool record, bool busy, string expected)
    {
        string action = "";
        var controller = new QuickSwitchController(
            () => new(session, record, busy, MatchingRecord: true),
            () => { action = "prepare"; return Task.CompletedTask; },
            () => { action = "restore"; return Task.CompletedTask; },
            () => action = "review");
        await controller.ExecuteAsync();
        Assert.Equal(expected, action);
    }

    [Fact]
    public async Task RepeatedTriggerDoesNotQueueOrReverseAnActiveSwitch()
    {
        var pending = new TaskCompletionSource();
        int calls = 0;
        var controller = new QuickSwitchController(() => new(SessionState.Normal, false, false),
            () => { calls++; return pending.Task; }, () => Task.CompletedTask, () => { });
        Task first = controller.ExecuteAsync();
        await controller.ExecuteAsync();
        Assert.Equal(1, calls);
        pending.SetResult();
        await first;
        await controller.ExecuteAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ReadsStateOnEveryTriggerAndRecoversAfterAFailedOperation()
    {
        bool fail = true;
        int reads = 0, restores = 0;
        var controller = new QuickSwitchController(() => { reads++; return new(SessionState.Swapped, true, false, MatchingRecord: true); },
            () => Task.CompletedTask,
            () => { restores++; return fail ? Task.FromException(new IOException("test")) : Task.CompletedTask; }, () => { });
        await Assert.ThrowsAsync<IOException>(controller.ExecuteAsync);
        fail = false;
        await controller.ExecuteAsync();
        Assert.Equal(2, reads);
        Assert.Equal(2, restores);
    }

    [Fact]
    public async Task UnreadableOrMismatchedRecoveryRecordRequiresReviewEvenOnTheConfiguredMeetingDesktop()
    {
        int restores = 0, reviews = 0;
        var controller = new QuickSwitchController(() => new(SessionState.Swapped, true, false, MatchingRecord: false),
            () => Task.CompletedTask, () => { restores++; return Task.CompletedTask; }, () => reviews++);
        await controller.ExecuteAsync();
        Assert.Equal(0, restores);
        Assert.Equal(1, reviews);
    }
}
