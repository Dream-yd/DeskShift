using System.Runtime.InteropServices;
using MeetingDesktop.Core.Interop;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Workflows;

/// <summary>Reads the active view and its persisted folder on an STA thread; never redirects or refreshes it.</summary>
internal static class CompatibilityShellView
{
    internal static CompatibilityDesktopSnapshot Capture(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object? windows = null, dispatch = null, browser = null, view = null, folder = null;
        nint browserPointer = 0;
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!);
            object location = 0, root = null!;
            int hwnd = 0;
            dispatch = ((dynamic)windows!).FindWindowSW(ref location, ref root, 8, ref hwnd, 1);
            if (dispatch is null) throw new InvalidOperationException("The active desktop Shell view is unavailable.");
            var serviceId = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var browserId = typeof(ShellNative.IShellBrowser).GUID;
            Check(((ShellNative.IServiceProvider)dispatch).QueryService(ref serviceId, ref browserId, out browserPointer));
            browser = Marshal.GetObjectForIUnknown(browserPointer);
            Check(((ShellNative.IShellBrowser)browser).QueryActiveShellView(out view));
            var folderView = (ShellNative.IFolderView)view;
            string? actualFolder = ReadFolderPath(folderView);
            string knownFolder = ShellDesktopService.ReadDesktopPath();
            var folderId = typeof(ShellNative.IShellFolder).GUID;
            Check(folderView.GetFolder(ref folderId, out folder));
            Check(((ShellNative.IShellView)view).GetWindow(out nint viewWindow));

            using var target = DesktopGridView.Open(cancellationToken);
            if (target.View != viewWindow && NativeMethods.GetParent(target.View) != viewWindow)
                throw new InvalidOperationException("The compatibility listview belongs to a different Shell view.");

            var shellIcons = ReadIcons(folderView, (ShellNative.IShellFolder)folder, cancellationToken);
            var listIcons = CompatibilityIconIdentity.Enrich(DesktopIconReader.Read(target, cancellationToken), shellIcons);
            cancellationToken.ThrowIfCancellationRequested();
            string? folderAfterRead = ReadFolderPath(folderView);
            if (actualFolder is null || folderAfterRead is null || !ShellViewReadiness.SamePath(folderAfterRead, actualFolder))
                actualFolder = null;
            return new(actualFolder, shellIcons, listIcons)
            {
                KnownFolderPath = ShellViewReadiness.SamePath(ShellDesktopService.ReadDesktopPath(), knownFolder) ? knownFolder : null,
                ViewWindow = viewWindow,
                ProcessId = target.ProcessId,
            };
        }
        finally
        {
            if (browserPointer != 0) Marshal.Release(browserPointer);
            Release(folder);
            Release(view);
            Release(browser);
            Release(dispatch);
            Release(windows);
        }
    }

    private static string? ReadFolderPath(ShellNative.IFolderView view)
    {
        object? folder = null;
        try
        {
            // A folder shortcut may expose its physical target separately from
            // its namespace location. Query the object owned by the active view.
            var targetId = typeof(IPersistFolder3).GUID;
            if (view.GetFolder(ref targetId, out folder) == 0)
            {
                FolderTargetInfo info = default;
                try
                {
                    if (((IPersistFolder3)folder).GetFolderTargetInfo(out info) == 0)
                    {
                        if (!string.IsNullOrWhiteSpace(info.ParsingName) && Path.IsPathFullyQualified(info.ParsingName))
                            return Path.GetFullPath(info.ParsingName);
                        string? target = ReadPhysicalPath(info.TargetPidl);
                        if (target is not null) return target;
                    }
                }
                finally { if (info.TargetPidl != 0) Marshal.FreeCoTaskMem(info.TargetPidl); }
            }
        }
        finally { Release(folder); }

        folder = null;
        nint pidl = 0;
        try
        {
            var persistId = typeof(IPersistFolder2).GUID;
            Check(view.GetFolder(ref persistId, out folder));
            Check(((IPersistFolder2)folder).GetCurFolder(out pidl));
            return ReadPhysicalPath(pidl);
        }
        finally
        {
            if (pidl != 0) Marshal.FreeCoTaskMem(pidl);
            Release(folder);
        }
    }

    private static string? ReadPhysicalPath(nint pidl)
    {
        // An empty PIDL denotes the virtual Desktop root. Resolving it through
        // Known Folders would merely repeat the cached path we need to distrust.
        if (pidl == 0 || Marshal.ReadInt16(pidl) == 0) return null;
        nint name = 0;
        try
        {
            Check(SHGetNameFromIDList(pidl, 0x80028000 /* DESKTOPABSOLUTEPARSING */, out name));
            string? path = Marshal.PtrToStringUni(name);
            return path is not null && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        finally { if (name != 0) Marshal.FreeCoTaskMem(name); }
    }

    private static IReadOnlyList<IconPosition> ReadIcons(ShellNative.IFolderView view, ShellNative.IShellFolder folder,
        CancellationToken cancellationToken)
    {
        Check(view.ItemCount(2, out int count));
        var icons = new List<IconPosition>(count);
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint pidl = 0;
            ShellNative.IShellItem? item = null;
            try
            {
                Check(view.Item(index, out pidl));
                Check(view.GetItemPosition(pidl, out var point));
                var itemId = typeof(ShellNative.IShellItem).GUID;
                Check(ShellNative.SHCreateItemWithParent(0, folder, pidl, ref itemId, out item));
                Check(item.GetAttributes(0x40000000, out uint attributes));
                bool filesystem = (attributes & 0x40000000) != 0;
                icons.Add(new(ReadName(item, 0), point.X, point.Y,
                    ReadName(item, filesystem ? 0x80058000u : 0x80028000u), filesystem));
            }
            finally
            {
                if (pidl != 0) Marshal.FreeCoTaskMem(pidl);
                Release(item);
            }
        }
        Check(view.ItemCount(2, out int after));
        if (count != after) throw new InvalidOperationException("The Shell icon listing changed during enumeration.");
        return icons;
    }

    private static string ReadName(ShellNative.IShellItem item, uint format)
    {
        nint name = 0;
        try
        {
            Check(item.GetDisplayName(format, out name));
            return Marshal.PtrToStringUni(name) ?? string.Empty;
        }
        finally { if (name != 0) Marshal.FreeCoTaskMem(name); }
    }

    private static void Check(int hresult) { if (hresult < 0) Marshal.ThrowExceptionForHR(hresult); }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetNameFromIDList(nint pidl, uint format, out nint name);

    [ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFolder2
    {
        [PreserveSig] int GetClassID(out Guid classId);
        [PreserveSig] int Initialize(nint pidl);
        [PreserveSig] int GetCurFolder(out nint pidl);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FolderTargetInfo
    {
        public nint TargetPidl;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ParsingName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? NetworkProvider;
        public uint Attributes;
        public int Csidl;
    }

    [ComImport, Guid("CEF04FDF-FE72-11D2-87A5-00C04F6837CF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFolder3
    {
        [PreserveSig] int GetClassID(out Guid classId);
        [PreserveSig] int Initialize(nint pidl);
        [PreserveSig] int GetCurFolder(out nint pidl);
        [PreserveSig] int InitializeEx(nint bindContext, nint root, nint targetInfo);
        [PreserveSig] int GetFolderTargetInfo(out FolderTargetInfo info);
    }
}
