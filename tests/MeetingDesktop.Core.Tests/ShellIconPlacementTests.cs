namespace MeetingDesktop.Core.Tests;

public sealed class ShellIconPlacementTests
{
    [Fact]
    public void AlreadyPlacedIconsDoNotWaitOrMove()
    {
        ShellIconPlacement.Run([1, 2], _ => true,
            _ => Assert.Fail("No movement needed."), _ => Assert.Fail("No delay needed."));
    }

    [Fact]
    public void ImmediatePlacementNeedsOnlyTwoShortConfirmations()
    {
        bool placed = false;
        var delays = new List<int>();
        ShellIconPlacement.Run([1, 2], _ => placed, _ => placed = true, delays.Add);
        Assert.Equal([20, 20], delays);
    }

    [Fact]
    public void BrieflyCorrectPositionsDoNotEndVerification()
    {
        int elapsed = 0, moves = 0;
        var delays = new List<int>();
        ShellIconPlacement.Run([1], _ => elapsed == 20 || elapsed >= 60,
            _ => moves++, delay => { elapsed += delay; delays.Add(delay); });
        Assert.Equal(80, elapsed);
        Assert.Equal(1, moves);
        Assert.Equal([20, 20, 20, 20], delays);
    }

    [Fact]
    public void MatchAtTheRetryBoundaryStillNeedsAnotherConfirmation()
    {
        int elapsed = 0;
        ShellIconPlacement.Run([1], _ => elapsed == 80 || elapsed >= 120,
            _ => { }, delay => elapsed += delay);
        Assert.Equal(140, elapsed);
    }

    [Fact]
    public void UnsettledIconsRetainTheRetryBudgetAndOnlyMoveUnmatchedItems()
    {
        int elapsed = 0;
        var batches = new List<int[]>();
        ShellIconPlacement.Run([1, 2], item => item == 1, batches.Add, delay => elapsed += delay);
        Assert.Equal(6, batches.Count);
        Assert.All(batches, batch => Assert.Equal([2], batch));
        Assert.Equal(480, elapsed);
    }

    [Fact]
    public void NativeFailureIsNotReportedAsSuccessfulPlacement()
    {
        Assert.Throws<IOException>(() => ShellIconPlacement.Run([1], _ => false,
            _ => throw new IOException("Explorer disconnected"), _ => Assert.Fail("Must not retry a failed call.")));
    }
}
