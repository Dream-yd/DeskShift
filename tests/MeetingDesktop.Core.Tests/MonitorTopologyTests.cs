using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class MonitorTopologyTests
{
    [Fact]
    public void ChangingPrimaryDisplayIsNotTheSameTopology()
    {
        var left = new MonitorInfo("left", -1920, 0, 1920, 1080, false);
        var right = new MonitorInfo("right", 0, 0, 1920, 1080, true);
        Assert.False(new MonitorTopology([left, right]).Matches(
            new([left with { IsPrimary = true }, right with { IsPrimary = false }])));
    }

    [Fact]
    public void EnumerationOrderAndRenamedDevicesDoNotChangeGeometry()
    {
        var left = new MonitorInfo("left", -1920, 0, 1920, 1080, false);
        var right = new MonitorInfo("right", 0, 0, 2560, 1440, true);
        Assert.True(new MonitorTopology([left, right]).Matches(
            new([right with { DeviceName = "new-right" }, left with { DeviceName = "new-left" }])));
    }

    [Fact]
    public void MissingMonitorDataCannotConfirmAnUnchangedDisplay()
    {
        Assert.False(MonitorTopology.Empty.Matches(MonitorTopology.Empty));
        Assert.False(MonitorTopology.Empty.Matches(null));
    }

    [Fact]
    public void DisconnectAndResolutionChangesAreDetected()
    {
        var screen = new MonitorInfo("screen", 0, 0, 2560, 1440, true);
        var previous = new MonitorTopology([screen, new("other", -1920, 0, 1920, 1080, false)]);
        Assert.False(previous.Matches(new([screen])));
        Assert.False(new MonitorTopology([screen]).Matches(new([screen with { Width = 1920 }])));
    }
}
