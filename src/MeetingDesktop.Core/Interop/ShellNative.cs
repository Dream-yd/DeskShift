using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MeetingDesktop.Core.Interop;

internal static class ShellNative
{
    internal static readonly Guid DesktopId = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    internal static readonly Guid PublicDesktopId = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint GetShellWindow();

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int SHSetKnownFolderPath(ref Guid id, uint flags, nint token, string path);
    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern void SHChangeNotify(int eventId, int flags, string path, nint other);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint window, uint message, nuint wparam,
        string lparam, uint flags, uint timeout, out nuint result);
    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHCreateItemWithParent(nint parentPidl,
        [MarshalAs(UnmanagedType.Interface)] IShellFolder parent, nint childPidl, ref Guid id,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
        public Point(int x, int y) { X = x; Y = y; }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid id, out nint result);
    }

    // Preserve all preceding native slots, including methods this app does not call.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out nint window);
        [PreserveSig] int ContextSensitiveHelp(int enterMode);
        [PreserveSig] int InsertMenusSB(nint menu, nint menuWidths);
        [PreserveSig] int SetMenuSB(nint menu, nint reserved, nint activeObject);
        [PreserveSig] int RemoveMenusSB(nint menu);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int EnableModelessSB(int enable);
        [PreserveSig] int TranslateAcceleratorSB(nint message, ushort id);
        [PreserveSig] int BrowseObject(nint pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IStream stream);
        [PreserveSig] int GetControlWindow(uint id, out nint window);
        [PreserveSig] int SendControlMsg(uint id, uint message, nuint wparam, nint lparam, out nint result);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid id, [MarshalAs(UnmanagedType.Interface)] out object folder);
        [PreserveSig] int Item(int index, out nint pidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out object items);
        [PreserveSig] int GetSelectionMarkedItem(out int index);
        [PreserveSig] int GetFocusedItem(out int index);
        [PreserveSig] int GetItemPosition(nint pidl, out Point point);
        [PreserveSig] int GetSpacing(out Point point);
        [PreserveSig] int GetDefaultSpacing(out Point point);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int index, uint flags);
        [PreserveSig] int SelectAndPositionItems(uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] nint[] items,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Point[] points, uint flags);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellFolder { }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellView
    {
        [PreserveSig] int GetWindow(out nint window);
        [PreserveSig] int ContextSensitiveHelp(int enterMode);
        [PreserveSig] int TranslateAccelerator(nint message);
        [PreserveSig] int EnableModeless(int enable);
        [PreserveSig] int UIActivate(uint state);
        [PreserveSig] int Refresh();
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bindContext, ref Guid handler, ref Guid id, out nint result);
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint format, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    }
}
