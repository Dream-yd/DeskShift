using MeetingDesktop.App.Services;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Tests;

public sealed class DesktopDoubleClickGestureTests
{
    private static DesktopDoubleClickGesture Create() => new(500, 8, 8, 4, 4);
    private static DesktopPointer Point(uint time, int x = 100, int y = 100, int view = 42) => new(x, y, time, view);

    [Fact]
    public void RequiresTwoCompletedClicksAndDoesNotFireAThirdTime()
    {
        var gesture = Create();
        Assert.Null(gesture.Down(Point(100)));
        Assert.Null(gesture.Up(Point(120)));
        Assert.Null(gesture.Down(Point(200)));
        Assert.NotNull(gesture.Up(Point(220)));
        gesture.Down(Point(300));
        Assert.Null(gesture.Up(Point(320)));
    }

    [Theory]
    [InlineData(700u, 100, 100, 42)]
    [InlineData(200u, 110, 100, 42)]
    [InlineData(200u, 100, 110, 42)]
    [InlineData(200u, 100, 100, 43)]
    [InlineData(200u, 100, 100, 0)]
    public void RejectsSlowDistantOrDifferentSurfaceClicks(uint time, int x, int y, int view)
    {
        var gesture = Create();
        gesture.Down(Point(100)); gesture.Up(Point(120));
        gesture.Down(Point(time, x, y, view));
        Assert.Null(gesture.Up(Point(time + 20, x, y, view)));
    }

    [Fact]
    public void DragOrInterruptedSequenceIsNotADoubleClick()
    {
        var gesture = Create();
        gesture.Down(Point(100)); gesture.Up(Point(120, x: 120));
        gesture.Down(Point(200)); Assert.Null(gesture.Up(Point(220)));
        gesture.Reset();
        gesture.Down(Point(300)); gesture.Up(Point(320));
        gesture.Reset();
        gesture.Down(Point(400)); Assert.Null(gesture.Up(Point(420)));
    }

    [Fact]
    public void SupportsNegativeMonitorCoordinatesAndTickWrap()
    {
        var gesture = Create();
        gesture.Down(Point(uint.MaxValue - 200, -800, -100));
        gesture.Up(Point(uint.MaxValue - 180, -800, -100));
        gesture.Down(Point(100, -799, -100));
        Assert.NotNull(gesture.Up(Point(120, -799, -100)));
    }

    [Fact]
    public void DraggingOutAndBackDoesNotTurnIntoAClick()
    {
        var gesture = Create();
        gesture.Down(Point(100));
        gesture.Move(120, 100);
        gesture.Move(100, 100);
        gesture.Up(Point(120));
        gesture.Down(Point(200));
        Assert.Null(gesture.Up(Point(220)));
    }

    [Fact]
    public void KeepsButtonDownPositionsForTheBlankAreaCheck()
    {
        var gesture = Create();
        var firstDown = Point(100, x: 101);
        var secondDown = Point(200, x: 102);
        gesture.Down(firstDown); gesture.Up(Point(120, x: 103));
        gesture.Down(secondDown);
        var click = gesture.Up(Point(220, x: 104));
        Assert.NotNull(click);
        Assert.Equal(firstDown, click.Value.FirstDown);
        Assert.Equal(secondDown, click.Value.SecondDown);
    }

    [Theory]
    [InlineData(DesktopMouseGesture.MiddleDoubleClick, 0x0207u, 0x0208u, false, true)]
    [InlineData(DesktopMouseGesture.MiddleDoubleClick, 0x0201u, 0x0202u, false, false)]
    [InlineData(DesktopMouseGesture.LeftDoubleClickBlank, 0x0201u, 0x0202u, false, true)]
    [InlineData(DesktopMouseGesture.LeftDoubleClickBlank, 0x0207u, 0x0208u, false, false)]
    [InlineData(DesktopMouseGesture.Disabled, 0x0207u, 0x0208u, false, false)]
    [InlineData(DesktopMouseGesture.MiddleDoubleClick, 0x0207u, 0x0208u, true, false)]
    public void RecognizesOnlyTheConfiguredButtonAndRejectsBlockedInput(DesktopMouseGesture mode, uint down, uint up, bool blocked, bool expected)
    {
        var gesture = Create();
        gesture.Observe(mode, down, Point(100), blocked);
        gesture.Observe(mode, up, Point(120), blocked);
        gesture.Observe(mode, down, Point(200), blocked);
        Assert.Equal(expected, gesture.Observe(mode, up, Point(220), blocked) is not null);
    }

    [Fact]
    public void OtherButtonsAndMouseWheelInterruptAMiddleDoubleClick()
    {
        foreach (uint interrupt in new[] { 0x0201u, 0x0204u, 0x020Au, 0x020Bu, 0x020Eu })
        {
            var gesture = Create();
            gesture.Observe(DesktopMouseGesture.MiddleDoubleClick, 0x0207, Point(100), false);
            gesture.Observe(DesktopMouseGesture.MiddleDoubleClick, 0x0208, Point(120), false);
            gesture.Observe(DesktopMouseGesture.MiddleDoubleClick, interrupt, Point(150), false);
            gesture.Observe(DesktopMouseGesture.MiddleDoubleClick, 0x0207, Point(200), false);
            Assert.Null(gesture.Observe(DesktopMouseGesture.MiddleDoubleClick, 0x0208, Point(220), false));
        }
    }
}
