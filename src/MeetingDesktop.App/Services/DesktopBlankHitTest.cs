using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace MeetingDesktop.App.Services;

/// <summary>Accepts only Explorer's desktop list and a complete, readable icon hit test.</summary>
internal static class DesktopBlankHitTest
{
    public static nint DesktopSurfaceAt(int x, int y)
    {
        nint window = WindowFromPoint(new NativePoint { X = x, Y = y });
        if (ClassName(window) is "Progman" or "WorkerW") return window;
        return DesktopViewAt(x, y);
    }

    public static bool IsDesktopSurface(DesktopDoubleClick click) =>
        AllPoints(click).All(point => point.View != 0 && point.View == click.First.View &&
            DesktopSurfaceAt(point.X, point.Y) == point.View);

    public static nint DesktopViewAt(int x, int y)
    {
        nint view = WindowFromPoint(new NativePoint { X = x, Y = y });
        if (ClassName(view) != "SysListView32" || ClassName(GetParent(view)) != "SHELLDLL_DefView") return 0;
        return ClassName(GetAncestor(view, 2)) is "Progman" or "WorkerW" ? view : 0;
    }

    public static bool IsBlank(DesktopDoubleClick click)
    {
        nint view = click.First.View;
        var samples = AllPoints(click).ToArray();
        if (samples.Any(point => point.View == 0 || point.View != view || DesktopViewAt(point.X, point.Y) != view)) return false;

        Point[] points = samples.Select(sample => new Point(sample.X, sample.Y)).ToArray();
        // UI Automation runs on a separate STA, never inside the global mouse hook.
        if (points.Any(point => !IsListAt(point, view))) return false;
        var list = AutomationElement.FromHandle(view);
        var items = list.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        if (SendMessageTimeout(view, 0x1004 /* LVM_GETITEMCOUNT */, 0, 0, 2, 100, out nuint count) == 0 ||
            count != (nuint)items.Count) return false;

        foreach (AutomationElement item in items)
        {
            Rect bounds = item.Current.BoundingRectangle;
            if (points.Any(bounds.Contains)) return false;
        }
        return DesktopViewAt(click.Second.X, click.Second.Y) == view;
    }

    private static IEnumerable<DesktopPointer> AllPoints(DesktopDoubleClick click) =>
        [click.FirstDown, click.First, click.SecondDown, click.Second];

    private static bool IsListAt(Point point, nint view)
    {
        var element = AutomationElement.FromPoint(point).Current;
        return element.ControlType == ControlType.List && (nint)element.NativeWindowHandle == view;
    }

    private static string ClassName(nint window)
    {
        var value = new StringBuilder(64);
        return window != 0 && GetClassName(window, value, value.Capacity) != 0 ? value.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder value, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint window,
        uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
}
