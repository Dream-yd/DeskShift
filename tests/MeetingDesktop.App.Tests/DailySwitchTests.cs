using MeetingDesktop.App.ViewModels;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Tests;

// Pure routing and command tests: no desktop capture, Explorer, registry or elevation.
public sealed class DailySwitchTests
{
    private const string Original = @"C:\DailySwitchTests\Original";
    private const string Alternate = @"C:\DailySwitchTests\Alternate";
    private const string Other = @"C:\DailySwitchTests\Other";
    private static readonly SessionInfo Record = new()
    {
        OriginalDesktopPath = Original, MeetingDesktopPathAtSwap = Alternate,
        SwappedUtc = DateTimeOffset.Parse("2026-10-24T10:00:00Z")
    };
    private static DesktopLocation At(string? path, DesktopLocationStatus status = DesktopLocationStatus.Agreed) =>
        new(status, path, [], false);
    private static DailySwitchPlan Away(SessionInfo? record = null) =>
        DailySwitchPlan.Resolve(SessionState.Swapped, At(Alternate), true, record ?? Record, Other);
    private static DailySwitchPlan Home() =>
        DailySwitchPlan.Resolve(SessionState.Normal, At(Original), false, null, Alternate);

    [Fact]
    public void NormalStartupTargetsAlternateOnSwitchPage()
    {
        var plan = Home();
        Assert.Equal(DailySwitchDirection.ToAlternate, plan.Direction);
        Assert.Equal(Alternate, plan.TargetPath);
        Assert.Equal("Prepare", plan.StartupPage);
    }

    [Fact]
    public void MatchingSwappedStartupStaysOnSwitchPageAndUsesRecordedOriginalDespiteSettingsChange()
    {
        var plan = Away();
        Assert.Equal(DailySwitchDirection.ToOriginal, plan.Direction);
        Assert.Equal(Original, plan.TargetPath);
        Assert.Equal("Prepare", plan.StartupPage);
        Assert.Equal(Record, plan.Session);
    }

    [Theory]
    [InlineData(SessionState.Swapped, Alternate, false, false, "Diagnostics")]
    [InlineData(SessionState.Swapped, Alternate, true, false, "Diagnostics")]
    [InlineData(SessionState.Normal, Original, true, true, "Restore")]
    [InlineData(SessionState.Swapped, Other, true, true, "Restore")]
    [InlineData(SessionState.Inconsistent, Alternate, true, true, "Restore")]
    [InlineData(SessionState.Adopted, Alternate, false, false, "Diagnostics")]
    public void UncertainStateNeverOffersADailyTarget(SessionState state, string current, bool exists, bool readable, string page)
    {
        var plan = DailySwitchPlan.Resolve(state, At(current), exists, readable ? Record : null, Alternate);
        Assert.Equal(DailySwitchDirection.Recovery, plan.Direction);
        Assert.Null(plan.TargetPath);
        Assert.Equal(page, plan.StartupPage);
    }

    [Theory]
    [InlineData(DesktopLocationStatus.Conflicting)]
    [InlineData(DesktopLocationStatus.Unresolved)]
    public void ConflictingOrUnresolvedLocationCannotSwitchEvenIfStateSaysNormal(DesktopLocationStatus status)
    {
        Assert.Equal(DailySwitchDirection.Recovery,
            DailySwitchPlan.Resolve(SessionState.Normal, At(Original, status), false, null, Alternate).Direction);
    }

    [Theory]
    [InlineData(Original)]
    [InlineData("relative-folder")]
    [InlineData("")]
    public void InvalidOrSameTargetCannotBeUsedForDailySwitch(string target)
    {
        Assert.Equal(DailySwitchDirection.Recovery,
            DailySwitchPlan.Resolve(SessionState.Normal, At(Original), false, null, target).Direction);
    }

    [Theory]
    [InlineData(Alternate)]
    [InlineData("relative-folder")]
    [InlineData("")]
    public void InvalidReturnRecordCannotAuthorizeDailyReturn(string original)
    {
        Assert.Equal(DailySwitchDirection.Recovery, Away(Record with { OriginalDesktopPath = original }).Direction);
    }

    [Fact]
    public async Task PageCommandRefreshesBeforeDispatchAndUsesReturnCallback()
    {
        var events = new List<string>();
        var plan = Away();
        var command = new DailySwitchPageAction(() => false,
            () => { events.Add("refresh"); return plan; },
            () => { events.Add("prepare"); return Task.CompletedTask; },
            () => { events.Add("return-on-page"); return Task.CompletedTask; },
            () => events.Add("review"));
        await command.ExecuteAsync(plan);
        Assert.Equal(["refresh", "return-on-page"], events);
    }

    [Fact]
    public async Task PageCommandRefusesToReverseAStaleDisplayedDirection()
    {
        int operations = 0, reviews = 0;
        Task Operate() { operations++; return Task.CompletedTask; }
        var command = new DailySwitchPageAction(() => false, () => Away(), Operate, Operate, () => reviews++);
        await command.ExecuteAsync(Home());
        Assert.Equal(0, operations);
        Assert.Equal(1, reviews);
    }

    [Fact]
    public async Task ReplacedRecordWithSamePathsRequiresReview()
    {
        int operations = 0, reviews = 0;
        Task Operate() { operations++; return Task.CompletedTask; }
        var command = new DailySwitchPageAction(() => false,
            () => Away(Record with { SwappedUtc = Record.SwappedUtc.AddDays(1) }), Operate, Operate, () => reviews++);
        await command.ExecuteAsync(Away());
        Assert.Equal(0, operations);
        Assert.Equal(1, reviews);
    }

    [Fact]
    public async Task BusyPageCommandDoesNotReadStateOrStartWork()
    {
        var command = new DailySwitchPageAction(() => true,
            () => throw new InvalidOperationException("Busy must not refresh"),
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException(),
            () => throw new InvalidOperationException());
        await command.ExecuteAsync(Home());
    }

    [Fact]
    public async Task ReentrantClickCannotStartASecondOperationAndNextClickCanReturn()
    {
        int prepares = 0, restores = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = Home();
        var command = new DailySwitchPageAction(() => false, () => current,
            async () => { prepares++; await completion.Task; current = Away(); },
            () => { restores++; return Task.CompletedTask; }, () => Assert.Fail("Unexpected review"));
        Task first = command.ExecuteAsync(Home());
        await command.ExecuteAsync(Home());
        completion.SetResult();
        await first;
        await command.ExecuteAsync(Away());
        Assert.Equal(1, prepares);
        Assert.Equal(1, restores);
    }

    [Fact]
    public async Task WorkflowFailureDoesNotLatchThePageCommand()
    {
        int attempts = 0;
        var command = new DailySwitchPageAction(() => false, Home,
            () => { if (++attempts == 1) throw new InvalidOperationException("Simulated failure"); return Task.CompletedTask; },
            () => throw new InvalidOperationException("Wrong direction"), () => Assert.Fail("Unexpected review"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync(Home()));
        await command.ExecuteAsync(Home());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ReturnPreviewUsesRecordedWallpaperAndBackupInsteadOfAlternateSettings()
    {
        var recorded = Record with
        {
            WallpaperAtSwap = new WallpaperState(@"C:\DailySwitchTests\original.jpg", "10", "0"),
            WallpaperBackupPath = @"C:\DailySwitchTests\backup.jpg", WallpaperChanged = true
        };
        Assert.Equal(recorded.WallpaperBackupPath,
            PrepareViewModel.ResolveTargetWallpaper(Away(recorded), "alternate.jpg", _ => true));
        Assert.Equal(recorded.WallpaperAtSwap.Path,
            PrepareViewModel.ResolveTargetWallpaper(Away(recorded), "alternate.jpg", _ => false));
        Assert.Equal("alternate.jpg", PrepareViewModel.ResolveTargetWallpaper(Home(), "alternate.jpg", _ => true));
    }

    [Fact]
    public void UnknownReturnWallpaperDoesNotShowTheAlternateWallpaper()
    {
        Assert.Null(PrepareViewModel.ResolveTargetWallpaper(Away(), "alternate.jpg", _ => false));
    }

    [Fact]
    public async Task ChangedAlternateTargetRequiresAnotherReviewBeforePrepare()
    {
        int reviews = 0;
        var updated = DailySwitchPlan.Resolve(SessionState.Normal, At(Original), false, null, Other);
        var command = new DailySwitchPageAction(() => false, () => updated,
            () => throw new InvalidOperationException("Target changed"),
            () => throw new InvalidOperationException("Wrong direction"), () => reviews++);
        await command.ExecuteAsync(Home());
        Assert.Equal(1, reviews);
    }

    [Fact]
    public async Task MissingRecordAtExecutionRoutesToDiagnosticsWithoutReturning()
    {
        int reviews = 0;
        var updated = DailySwitchPlan.Resolve(SessionState.Swapped, At(Alternate), false, null, Alternate);
        var command = new DailySwitchPageAction(() => false, () => updated,
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => reviews++);
        await command.ExecuteAsync(Away());
        Assert.Equal("Diagnostics", updated.StartupPage);
        Assert.Equal(1, reviews);
    }

    [Fact]
    public async Task ReloadingTheSameSessionFromJsonStillAllowsReturn()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Record);
        var firstRead = System.Text.Json.JsonSerializer.Deserialize<SessionInfo>(json)!;
        var secondRead = System.Text.Json.JsonSerializer.Deserialize<SessionInfo>(json)!;
        int returns = 0, reviews = 0;
        var command = new DailySwitchPageAction(() => false, () => Away(secondRead),
            () => throw new InvalidOperationException("Wrong direction"),
            () => { returns++; return Task.CompletedTask; }, () => reviews++);
        await command.ExecuteAsync(Away(firstRead));
        Assert.Equal(1, returns);
        Assert.Equal(0, reviews);
    }
}
