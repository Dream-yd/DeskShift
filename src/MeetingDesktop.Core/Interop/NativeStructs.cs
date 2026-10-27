using System.Runtime.InteropServices;

namespace MeetingDesktop.Core.Interop;

/// <summary>
/// Plain-old-data structures shared with user32/kernel32.
/// </summary>
/// <remarks>
/// Layout matters more than usual here: these are written into another
/// process's address space with <c>WriteProcessMemory</c> and read back with
/// <c>ReadProcessMemory</c>, so a field missing or misaligned means reading
/// somebody else's bytes and interpreting them as an icon name.
///
/// Sizes are never hardcoded - use <see cref="Marshal.SizeOf{T}()"/> so the
/// same code is correct on x86 and x64. A hardcoded 88 for <c>LVITEM</c> is
/// x64-only and silently wrong on 32-bit PowerShell.
/// </remarks>
internal static class NativeStructs
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// LVITEMW. Field order must match commctrl.h exactly - the listview in
    /// explorer.exe reads this structure from its own memory.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LVITEM
    {
        public uint Mask;
        public int ItemIndex;
        public int SubItemIndex;
        public uint State;
        public uint StateMask;
        public IntPtr Text;
        public int TextMaxLength;
        public int ImageIndex;
        public IntPtr LParam;
        public int Indent;
        public int GroupId;
        public uint ColumnCount;
        public IntPtr Columns;
        public IntPtr ColumnFormats;
        public int Group;
    }
}
