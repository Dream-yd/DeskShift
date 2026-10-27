using System.Runtime.InteropServices;

namespace MeetingDesktop.Core.Interop;

/// <summary>
/// The GDI entry points used to snapshot the desktop, and nothing else.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="NativeMethods"/> because these are device-context
/// calls rather than shell API calls, and because <see cref="NativeMethods"/> is
/// the file a reviewer reads to audit what the app does to the *shell*. Nothing
/// here touches another process; it allocates surfaces in this one.
///
/// The sequence these declarations exist for is
/// <see cref="CreateDC"/> -> <see cref="CreateCompatibleDC"/> ->
/// <see cref="CreateCompatibleBitmap"/> -> <see cref="SelectObject"/> ->
/// <c>PrintWindow</c> -> <see cref="GetDIBits"/> -> delete everything.
///
/// The screen DC comes from <c>CreateDC("DISPLAY", ...)</c> rather than
/// <c>GetDC(NULL)</c>: both work, but <c>GetDC</c> lives in user32 and this way
/// the whole capture needs exactly one DLL.
/// </remarks>
internal static class Gdi32Methods
{
    /// <summary>Uncompressed. With a 32-bit bit count this is a plain BGRA copy.</summary>
    public const uint BI_RGB = 0;

    /// <summary>The colour table holds literal RGB values, which is what BI_RGB means.</summary>
    public const uint DIB_RGB_COLORS = 0;

    /// <summary>The GDI device name for the display, used to obtain a screen DC.</summary>
    public const string DisplayDevice = "DISPLAY";

    /// <summary>
    /// BITMAPINFOHEADER. Field order must match wingdi.h: GDI reads this
    /// structure and trusts the offsets, so a missing field is not a compile
    /// error, it is a silently misparsed width.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    /// <summary>
    /// BITMAPINFO. The colour entry is never filled in for a 32-bit BI_RGB
    /// bitmap, but it still has to occupy its slot - the callee sizes the
    /// structure as header plus one table entry, and a struct declared without
    /// it is one DWORD short of what is expected.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        public uint Color;
    }

    /// <summary>Creates a device context for the named device.</summary>
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDC(string? driver, string? device, string? port, IntPtr initialData);

    /// <summary>Creates a memory DC compatible with the supplied DC.</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    /// <summary>
    /// Creates a device-dependent bitmap compatible with the supplied DC.
    /// </summary>
    /// <remarks>
    /// The DC passed here must describe the screen. Passing the memory DC that
    /// the bitmap will be selected into produces a 1bpp monochrome bitmap,
    /// because that DC starts life holding a 1x1 monochrome bitmap - and the
    /// failure is not an error code, it is a capture that comes back as noise.
    /// </remarks>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    /// <summary>Selects an object into a DC and returns what it displaced.</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    /// <summary>
    /// Copies bitmap pixels into a caller-supplied buffer, in the layout the
    /// supplied <see cref="BITMAPINFO"/> asks for.
    /// </summary>
    /// <remarks>
    /// Fails on a bitmap that is still selected into a DC - deselect it first.
    /// A negative header height requests top-down rows; a positive one gives
    /// GDI's native bottom-up order.
    /// </remarks>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetDIBits(
        IntPtr hdc,
        IntPtr bitmap,
        uint startLine,
        uint lineCount,
        [Out] byte[] bits,
        ref BITMAPINFO info,
        uint usage);
}
