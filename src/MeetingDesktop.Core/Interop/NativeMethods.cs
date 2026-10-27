using System.Runtime.InteropServices;
using System.Text;
using static MeetingDesktop.Core.Interop.NativeStructs;

namespace MeetingDesktop.Core.Interop;

/// <summary>
/// Every P/Invoke the app performs, and nothing else. No logic lives here.
/// </summary>
/// <remarks>
/// This is the file a reviewer reads to audit the native surface, so the
/// declarations are kept together deliberately. Three things differ from the
/// original PowerShell prototype and each one is load-bearing:
///
/// 1. <c>SetLastError = true</c> wherever the API sets it, so a failure can be
///    reported as a real Win32 error instead of a guess.
/// 2. <see cref="ProcessAccess.DesktopListView"/> instead of
///    <c>PROCESS_ALL_ACCESS</c>. The prototype asked for the right to
///    <c>CreateRemoteThread</c> and <c>DupHandle</c> in explorer.exe, which it
///    never uses and which antivirus heuristics treat as an injection
///    signature.
/// 3. <c>SendMessageTimeout</c> instead of <c>SendMessage</c>. See the note on
///    that method - it is a hang-prevention measure, not a style preference.
/// </remarks>
internal static class NativeMethods
{
    /// <summary>Always true for the desktop listview; used by the analyzer.</summary>
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    // -----------------------------------------------------------------
    // listview messages
    // -----------------------------------------------------------------
    public const uint LVM_GETITEMCOUNT = 0x1004;
    public const uint LVM_GETITEMPOSITION = 0x1010;
    public const uint LVM_SETITEMPOSITION32 = 0x1031;
    public const uint LVM_GETITEMTEXTW = 0x1073;
    public const uint LVM_REDRAWITEMS = 0x1015;

    /// <summary>
    /// Returns the icon spacing packed as cx in the low word and cy in the
    /// high word. No remote buffer needed - the result is the return value.
    /// wParam is fSmall: <c>TRUE</c> selects the SMALL icon view, <c>FALSE</c>
    /// the icon (large) view. The naming is the opposite way round from what
    /// it reads like, and getting it backwards silently yields the wrong grid
    /// pitch.
    /// </summary>
    public const uint LVM_GETITEMSPACING = 0x1033;

    public const uint LVIF_TEXT = 0x0001;

    /// <summary>
    /// Send the message unless the target window has not pumped its message
    /// queue for this long, in which case give up rather than block.
    /// </summary>
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public const uint SMTO_NORMAL = 0x0000;

    /// <summary>How long to wait for explorer's UI thread before giving up.</summary>
    public const uint ListViewMessageTimeoutMs = 5000;

    // -----------------------------------------------------------------
    // process access
    // -----------------------------------------------------------------
    public static class ProcessAccess
    {
        public const uint VmOperation = 0x0008;
        public const uint VmRead = 0x0010;
        public const uint VmWrite = 0x0020;
        public const uint QueryInformation = 0x0400;

        /// <summary>
        /// The minimum needed to allocate a buffer in explorer.exe and read
        /// and write icon data through it. Anything requesting
        /// PROCESS_ALL_ACCESS here is asking for rights it does not use.
        /// </summary>
        public const uint DesktopListView = VmOperation | VmRead | VmWrite | QueryInformation; // 0x0438
    }

    // -----------------------------------------------------------------
    // memory
    // -----------------------------------------------------------------
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

    // -----------------------------------------------------------------
    // PrintWindow
    // -----------------------------------------------------------------
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    // -----------------------------------------------------------------
    // monitors
    // -----------------------------------------------------------------
    /// <summary>
    /// Returned by <see cref="GetMonitorInfo"/> when the monitor is the primary.
    /// </summary>
    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    /// <summary>MONITORINFOEXW. The size field must be set before the call.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    public delegate bool MonitorEnumProc(nint monitor, nint hdc, ref RECT rect, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);

    // -----------------------------------------------------------------
    // wallpaper
    // -----------------------------------------------------------------
    /// <summary>Sets the desktop wallpaper to the image at <c>pvParam</c>.</summary>
    public const uint SPI_SETDESKWALLPAPER = 0x0014;

    /// <summary>Persist the change to the user profile.</summary>
    public const uint SPIF_UPDATEINIFILE = 0x01;

    /// <summary>Broadcast the change so running windows repaint.</summary>
    public const uint SPIF_SENDCHANGE = 0x02;

    /// <summary>
    /// Applies a wallpaper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-monitor wallpaper API (<c>IDesktopWallpaper</c>) arrived in
    /// Windows 8 and is what the Settings app uses. This call is the older one
    /// and applies a single image to every monitor. That is the right scope
    /// here: the meeting wallpaper is one conference image for the whole
    /// screen, and the older call works identically on every supported
    /// version.
    /// </para>
    /// <para>
    /// It also writes <c>HKCU\Control Panel\Desktop\WallPaper</c> when
    /// <see cref="SPIF_UPDATEINIFILE"/> is passed, but the fit style is a
    /// separate registry value this call does not touch - see
    /// <c>WallpaperService</c>, which sets it.
    /// </para>
    /// </remarks>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    // -----------------------------------------------------------------
    // user32
    // -----------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    /// <summary>
    /// Enumerates every descendant, not just direct children. Do not add a
    /// manual recursion on top of this - it double-counts and, in the case of
    /// the SHELLDLL_DefView search, can make a single window look like several.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    /// <summary>
    /// DPI of the monitor a window is on. Needed to express the fallback grid
    /// pitch in physical pixels - the positions from LVM_* are always physical,
    /// so a 96-DPI assumption is wrong on any scaled display.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Like SendMessage, but returns 0 instead of blocking forever when the
    /// target window has stopped pumping its message queue.
    /// </summary>
    /// <remarks>
    /// This matters more than it looks. The app kills and restarts explorer
    /// around these calls, and explorer's UI thread can be busy for seconds
    /// at a time. The prototype used SendMessage, which would freeze the WPF
    /// window indefinitely with no way to cancel.
    ///
    /// Callers MUST check the return value before reading the remote buffer.
    /// A timeout leaves the buffer holding whatever was there before, which
    /// parses as a plausible-looking but wrong icon name.
    /// </remarks>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hwnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeoutMs,
        out IntPtr result);

    /// <summary>Prints a window's content into a device context.</summary>
    /// <remarks>
    /// Used to snapshot the desktop. Unlike a screen grab, this renders the
    /// window's own content, so whatever windows happen to be on top of the
    /// desktop do not appear in the capture.
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    // -----------------------------------------------------------------
    // kernel32
    // -----------------------------------------------------------------
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, IntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------
    public static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public static string GetWindowText(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
