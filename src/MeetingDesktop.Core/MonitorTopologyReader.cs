using System.Runtime.InteropServices;
using MeetingDesktop.Core.Interop;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

/// <summary>
/// Captures the current display arrangement.
/// </summary>
/// <remarks>
/// Worth recording alongside every layout snapshot, because desktop icon
/// coordinates are virtual-desktop coordinates and their origin moves with the
/// arrangement. Attaching a monitor to the left of the primary shifts every
/// icon's X by that monitor's width, so positions saved before the change no
/// longer mean what they meant. Comparing topologies lets restore say "the
/// display setup changed" instead of silently placing icons somewhere wrong.
/// </remarks>
public static class MonitorTopologyReader
{
    /// <summary>
    /// The current display arrangement, or <see cref="MonitorTopology.Empty"/>
    /// when it cannot be read.
    /// </summary>
    public static MonitorTopology Capture()
    {
        var monitors = new List<MonitorInfo>();

        try
        {
            NativeMethods.EnumDisplayMonitors(nint.Zero, nint.Zero, (nint handle, nint _, ref NativeStructs.RECT _, nint _) =>
            {
                var info = new NativeMethods.MONITORINFOEX
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                    DeviceName = string.Empty,
                };

                if (NativeMethods.GetMonitorInfo(handle, ref info))
                {
                    monitors.Add(new MonitorInfo(
                        info.DeviceName ?? string.Empty,
                        info.Monitor.Left,
                        info.Monitor.Top,
                        info.Monitor.Right - info.Monitor.Left,
                        info.Monitor.Bottom - info.Monitor.Top,
                        (info.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
                }

                return true;
            }, nint.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not fatal: a missing topology only means the app cannot warn
            // about a display change, which degrades the report, not the
            // restore.
            return MonitorTopology.Empty;
        }

        return monitors.Count == 0
            ? MonitorTopology.Empty
            : new MonitorTopology(monitors);
    }
}
