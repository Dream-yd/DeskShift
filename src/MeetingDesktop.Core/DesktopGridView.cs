using System.Diagnostics;
using System.Runtime.InteropServices;
using MeetingDesktop.Core.Interop;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

/// <summary>
/// Locates the desktop's icon listview and opens a handle to the process that
/// owns it.
/// </summary>
/// <remarks>
/// This implementation walks the desktop host window tree. Earlier probes
/// observed the following on the development machine:
///
/// <list type="bullet">
/// <item><c>FindWindow("Progman")</c> finds a window that has no children.</item>
/// <item>The <c>ShellWindows</c> COM collection (CLSID 9BA05972-...) returns
/// <c>Count == 0</c>.</item>
/// </list>
///
/// A zero collection count does not rule out the separate COM desktop entry
/// point: <c>IShellWindows.FindWindowSW(SWC_DESKTOP)</c> and the resulting
/// <c>IFolderView</c> read chain have since been verified on that machine.
/// This class continues to find <c>SHELLDLL_DefView</c> and then the
/// <c>SysListView32</c> beneath it; it does not implement that COM route.
///
/// The one trap worth calling out: a machine routinely has <em>more than one</em>
/// <c>SHELLDLL_DefView</c>. A File Explorer window has its own, titled
/// <c>ShellView</c>. Taking "the first one found" therefore works until the user
/// opens File Explorer, at which point the app picks the wrong view, finds no
/// listview under it, and fails - which looks like an intermittent bug and is
/// actually fully deterministic. The search is scoped to desktop host windows
/// (<c>Progman</c> / <c>WorkerW</c>) to avoid that.
/// </remarks>
public static class DesktopGridView
{
    private const int DefaultRetries = 20;
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Finds the desktop icon listview, retrying while explorer starts up.
    /// </summary>
    /// <remarks>
    /// The retry is not defensive padding. The app kills and restarts
    /// explorer, and the listview does not exist for the first second or two
    /// afterwards.
    /// </remarks>
    public static nint FindView(
        int retries = DefaultRetries,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan wait = delay ?? DefaultDelay;

        for (int attempt = 0; attempt < retries; attempt++)
        {
            nint view = FindViewOnce();
            if (view != nint.Zero)
            {
                return view;
            }

            if (attempt < retries - 1)
            {
                if (cancellationToken.WaitHandle.WaitOne(wait))
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
        }

        return nint.Zero;
    }

    /// <summary>
    /// Lists every candidate window seen while searching, for the Diagnostics page.
    /// </summary>
    public static IReadOnlyList<DiagnosticEntry> Diagnostics()
    {
        var entries = new List<DiagnosticEntry>();

        NativeMethods.EnumWindows((top, _) =>
        {
            bool underHost = IsDesktopHost(top);

            NativeMethods.EnumChildWindows(top, (child, _) =>
            {
                string className = NativeMethods.GetClassName(child);
                if (className is "SHELLDLL_DefView" or "SysListView32")
                {
                    NativeMethods.GetWindowThreadProcessId(child, out uint pid);
                    entries.Add(new DiagnosticEntry(
                        child,
                        pid,
                        className,
                        NativeMethods.GetWindowText(child),
                        underHost));
                }

                return true;
            }, nint.Zero);

            return true;
        }, nint.Zero);

        return entries;
    }

    /// <summary>
    /// Opens the desktop listview and the process that owns it.
    /// </summary>
    /// <exception cref="DesktopInteropException">
    /// Thrown with a <see cref="InteropFailureKind"/> describing which step
    /// failed, rather than a bare message.
    /// </exception>
    internal static DesktopListViewTarget Open(CancellationToken cancellationToken = default)
    {
        nint view = FindView(cancellationToken: cancellationToken);

        if (view == nint.Zero)
        {
            IReadOnlyList<DiagnosticEntry> candidates = Diagnostics();
            bool anyExplorer = Process.GetProcessesByName("explorer").Length > 0;

            throw new DesktopInteropException(
                anyExplorer ? InteropFailureKind.DesktopViewNotFound : InteropFailureKind.ExplorerNotRunning,
                anyExplorer
                    ? "Could not locate the desktop icon listview (SHELLDLL_DefView -> SysListView32) under any Progman or WorkerW host window."
                    : "explorer.exe is not running, so there is no desktop to read icons from.",
                candidates.Select(c =>
                    $"hwnd=0x{c.Hwnd:X} pid={c.ProcessId} class={c.ClassName} title='{c.Title}' underDesktopHost={c.UnderDesktopHost}")
                    .ToList());
        }

        NativeMethods.GetWindowThreadProcessId(view, out uint processId);

        nint process = NativeMethods.OpenProcess(
            NativeMethods.ProcessAccess.DesktopListView,
            inheritHandle: false,
            processId);

        if (process == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new DesktopInteropException(
                InteropFailureKind.AccessDenied,
                $"OpenProcess(0x{NativeMethods.ProcessAccess.DesktopListView:X4}) failed for explorer pid {processId} " +
                $"(Win32 error {error}). This normally means explorer is running at a higher integrity level than this process - " +
                "usually because explorer was started by an elevated process. Sign out and back in, or restart explorer normally.",
                win32ErrorCode: error,
                processId: processId);
        }

        return new DesktopListViewTarget(view, process, processId);
    }

    private static nint FindViewOnce()
    {
        nint result = nint.Zero;

        // Preferred: only look under desktop host windows, so a File Explorer
        // window's own SHELLDLL_DefView ("ShellView") cannot shadow the real one.
        NativeMethods.EnumWindows((top, _) =>
        {
            if (!IsDesktopHost(top))
            {
                return true;
            }

            nint defView = FindDescendant(top, "SHELLDLL_DefView");
            if (defView == nint.Zero)
            {
                return true;
            }

            nint listView = FindDescendant(defView, "SysListView32");
            if (listView != nint.Zero)
            {
                result = listView;
                return false;
            }

            return true;
        }, nint.Zero);

        if (result != nint.Zero)
        {
            return result;
        }

        // Fallback: any SysListView32 titled "FolderView" whose ancestor chain
        // contains a SHELLDLL_DefView. Covers desktop hosts this build has not
        // seen, at the cost of also matching a File Explorer window in icon
        // view - hence it is the fallback, not the primary path.
        NativeMethods.EnumWindows((top, _) =>
        {
            NativeMethods.EnumChildWindows(top, (child, _) =>
            {
                if (NativeMethods.GetClassName(child) != "SysListView32")
                {
                    return true;
                }

                if (NativeMethods.GetWindowText(child) != "FolderView")
                {
                    return true;
                }

                if (HasAncestor(child, "SHELLDLL_DefView"))
                {
                    result = child;
                    return false;
                }

                return true;
            }, nint.Zero);

            return result == nint.Zero;
        }, nint.Zero);

        return result;
    }

    private static bool IsDesktopHost(nint hwnd)
    {
        string className = NativeMethods.GetClassName(hwnd);
        return className is "Progman" or "WorkerW";
    }

    private static nint FindDescendant(nint parent, string className)
    {
        nint found = nint.Zero;

        // EnumChildWindows already walks the whole subtree.
        NativeMethods.EnumChildWindows(parent, (child, _) =>
        {
            if (NativeMethods.GetClassName(child) == className)
            {
                found = child;
                return false;
            }

            return true;
        }, nint.Zero);

        return found;
    }

    private static bool HasAncestor(nint hwnd, string className)
    {
        nint current = NativeMethods.GetParent(hwnd);

        while (current != nint.Zero)
        {
            if (NativeMethods.GetClassName(current) == className)
            {
                return true;
            }

            current = NativeMethods.GetParent(current);
        }

        return false;
    }
}

/// <summary>
/// The desktop listview and the process handle used to read and write its
/// items. Disposing releases the process handle.
/// </summary>
internal sealed class DesktopListViewTarget : IDisposable
{
    private bool _disposed;

    public DesktopListViewTarget(nint view, nint process, uint processId)
    {
        View = view;
        Process = process;
        ProcessId = processId;
    }

    public nint View { get; }

    public nint Process { get; }

    public uint ProcessId { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Process != nint.Zero)
        {
            NativeMethods.CloseHandle(Process);
        }
    }
}
