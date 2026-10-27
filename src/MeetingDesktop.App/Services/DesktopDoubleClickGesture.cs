using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Services;

internal readonly record struct DesktopPointer(int X, int Y, uint Time, nint View);
internal readonly record struct DesktopDoubleClick(DesktopPointer First, DesktopPointer Second)
{
    public DesktopPointer FirstDown { get; init; }
    public DesktopPointer SecondDown { get; init; }
}

internal sealed class DesktopDoubleClickGesture(uint interval, int width, int height, int dragWidth, int dragHeight)
{
    private DesktopPointer? _down;
    private DesktopPointer? _firstDown;
    private DesktopPointer? _firstUp;

    public DesktopDoubleClick? Observe(DesktopMouseGesture mode, uint message, DesktopPointer point, bool blocked)
    {
        if (blocked || mode == DesktopMouseGesture.Disabled) { Reset(); return null; }
        if (message == 0x0200) { Move(point.X, point.Y); return null; }
        uint down = mode == DesktopMouseGesture.MiddleDoubleClick ? 0x0207u : 0x0201u;
        uint up = mode == DesktopMouseGesture.MiddleDoubleClick ? 0x0208u : 0x0202u;
        if (message == down) return Down(point);
        if (message == up) return Up(point);
        if (message is 0x0201 or 0x0204 or 0x0207 or 0x020A or 0x020B or 0x020E) Reset();
        return null;
    }

    public DesktopDoubleClick? Down(DesktopPointer point)
    {
        _down = point;
        return null;
    }

    public DesktopDoubleClick? Up(DesktopPointer point)
    {
        var down = _down;
        _down = null;
        if (down is not { } start || point.View == 0 || point.View != start.View ||
            unchecked(point.Time - start.Time) > interval ||
            Math.Abs((long)point.X - start.X) >= dragWidth || Math.Abs((long)point.Y - start.Y) >= dragHeight)
        { Reset(); return null; }

        if (_firstDown is { } first && _firstUp is { } firstUp && first.View == start.View &&
            unchecked(start.Time - first.Time) <= interval &&
            Math.Abs((long)start.X - first.X) <= width / 2 && Math.Abs((long)start.Y - first.Y) <= height / 2)
        {
            Reset();
            return new(firstUp, point) { FirstDown = first, SecondDown = start };
        }
        _firstDown = start;
        _firstUp = point;
        return null;
    }

    public void Reset() { _down = null; _firstDown = null; _firstUp = null; }

    public void Move(int x, int y)
    {
        if (_down is { } down && (Math.Abs((long)x - down.X) >= dragWidth || Math.Abs((long)y - down.Y) >= dragHeight)) Reset();
    }
}
