using System.Runtime.InteropServices;
using MeetingDesktop.Core.Interop;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

/// <summary>STA-only Shell operations. The GUI runs these in a bounded helper process.</summary>
public static class ShellDesktopService
{
    public static string ReadDesktopPath() => ReadKnownFolder(ShellNative.DesktopId);

    internal static bool SnapshotMatchesTarget(string target, IReadOnlyList<IconPosition> icons)
    {
        var files = LiveDesktopSwitcher.DesktopFiles(target);
        return ShellViewReadiness.Matches(target, ReadKnownFolder(ShellNative.PublicDesktopId),
            files.Required, icons, files.Optional);
    }

    public static IReadOnlyList<IconPosition> Capture()
    {
        using var view = new ShellView();
        return view.ReadItems().Select(item => item.Position).ToArray();
    }

    public static void Switch(string desktopPath) => SwitchAndCapture(desktopPath);

    internal static IReadOnlyList<IconPosition> SwitchAndCapture(string desktopPath) =>
        new LiveDesktopSwitcher(new NativeLiveDesktopShell()).Run(desktopPath);

    private sealed class NativeLiveDesktopShell : ILiveDesktopShell
    {
        public IReadOnlyList<IconPosition> Capture() => ShellDesktopService.Capture();
        public string ReadDesktopPath() => ShellDesktopService.ReadDesktopPath();
        public string ReadPublicDesktopPath() => ReadKnownFolder(ShellNative.PublicDesktopId);
        public void WriteDesktopPath(string path) => new DesktopPathService().Write(path);
        public void NotifySettings() => ShellSettingsNotification.Send(ShellNative.GetShellWindow,
            window => ShellNative.SendMessageTimeout(window, 0x001a, 0, "User Shell Folders", 0x0002, 200, out _));
        public void NotifyDirectory(string path) => ShellNative.SHChangeNotify(0x1000, 0x2005, path, 0);
        public void Refresh() { using var view = new ShellView(); view.Refresh(); }
    }

    public static PlaceResult Place(IReadOnlyList<IconPosition> wanted)
    {
        if (wanted.Count == 0) return new(0, 0, [], [], false, false);
        using var view = new ShellView();
        var entries = view.ReadItems();
        var matches = ShellIconMatcher.Match(wanted, entries.Select(e => e.Position).ToArray());
        var matched = matches.Select(m => m.Wanted).ToHashSet();
        string[] notFound = wanted.Where(icon => !matched.Contains(icon)).Select(icon => icon.Name).ToArray();
        if (matches.Count == 0) return new(wanted.Count, 0, [], notFound, false, false);
        if (view.FolderView.GetAutoArrange() == 0)
            throw new InvalidOperationException("Turn off 'Auto arrange icons' before restoring a saved layout.");

        List<IconMismatch> mismatches = [];
        ShellIconPlacement.Run(matches,
            match => view.IsAt(entries[match.CurrentIndex].Pidl, match.Wanted),
            pending => Check(view.FolderView.SelectAndPositionItems((uint)pending.Length,
                pending.Select(match => entries[match.CurrentIndex].Pidl).ToArray(),
                pending.Select(match => new ShellNative.Point(match.Wanted.X, match.Wanted.Y)).ToArray(),
                0x80 | 0x40000000 /* POSITIONITEM, NOTAKEFOCUS */)));
        foreach (var match in matches)
        {
            Check(view.FolderView.GetItemPosition(entries[match.CurrentIndex].Pidl, out var point));
            if (point.X != match.Wanted.X || point.Y != match.Wanted.Y)
                mismatches.Add(new(match.Wanted.Name, match.Wanted.X, match.Wanted.Y, point.X, point.Y));
        }
        return new(wanted.Count, matches.Count, mismatches, notFound, false, false);
    }

    private static string ReadKnownFolder(Guid id)
    {
        nint pointer = 0;
        try
        {
            Check(ShellNative.SHGetKnownFolderPath(ref id, 0, 0, out pointer));
            return Marshal.PtrToStringUni(pointer) ?? throw new InvalidOperationException("Desktop path unavailable.");
        }
        finally { if (pointer != 0) Marshal.FreeCoTaskMem(pointer); }
    }

    private static string ReadName(ShellNative.IShellItem item, uint format)
    {
        nint pointer = 0;
        try
        {
            Check(item.GetDisplayName(format, out pointer));
            return Marshal.PtrToStringUni(pointer) ?? string.Empty;
        }
        finally { if (pointer != 0) Marshal.FreeCoTaskMem(pointer); }
    }

    private static void Check(int hresult)
    {
        if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private sealed record Entry(nint Pidl, IconPosition Position);

    private sealed class ShellView : IDisposable
    {
        private object? _windows, _dispatch, _browser, _view, _folder;
        private readonly List<nint> _pidls = [];
        internal ShellNative.IFolderView FolderView { get; }

        internal ShellView()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Shell access requires an STA thread.");
            nint browserPointer = 0;
            try
            {
                _windows = Activator.CreateInstance(Type.GetTypeFromCLSID(
                    new("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!)!;
                object location = 0;
                object locationRoot = null!;
                int hwnd = 0;
                _dispatch = ((dynamic)_windows).FindWindowSW(ref location, ref locationRoot, 8, ref hwnd, 1);
                if (_dispatch is null) throw new DesktopViewUnavailableException("The desktop Shell view is unavailable.");
                var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                var browserId = typeof(ShellNative.IShellBrowser).GUID;
                Check(((ShellNative.IServiceProvider)_dispatch).QueryService(ref service, ref browserId, out browserPointer));
                _browser = Marshal.GetObjectForIUnknown(browserPointer);
                Check(((ShellNative.IShellBrowser)_browser).QueryActiveShellView(out _view));
                FolderView = (ShellNative.IFolderView)_view;
                var folderId = typeof(ShellNative.IShellFolder).GUID;
                Check(FolderView.GetFolder(ref folderId, out _folder));
            }
            catch { Dispose(); throw; }
            finally { if (browserPointer != 0) Marshal.Release(browserPointer); }
        }

        internal IReadOnlyList<Entry> ReadItems()
        {
            var result = new List<Entry>();
            Check(FolderView.ItemCount(2, out int count));
            for (int index = 0; index < count; index++)
            {
                Check(FolderView.Item(index, out nint pidl));
                _pidls.Add(pidl);
                Check(FolderView.GetItemPosition(pidl, out var point));
                var itemId = typeof(ShellNative.IShellItem).GUID;
                ShellNative.IShellItem? item = null;
                try
                {
                    Check(ShellNative.SHCreateItemWithParent(0, (ShellNative.IShellFolder)_folder!, pidl, ref itemId, out item));
                    Check(item.GetAttributes(0x40000000 /* SFGAO_FILESYSTEM */, out uint attributes));
                    bool filesystem = (attributes & 0x40000000) != 0;
                    result.Add(new(pidl, new(ReadName(item, 0), point.X, point.Y,
                        ReadName(item, filesystem ? 0x80058000u /* FILESYSPATH */ : 0x80028000u), filesystem)));
                }
                finally { Release(item); }
            }
            Check(FolderView.ItemCount(2, out int after));
            if (after != count) throw new DesktopViewUnavailableException("The desktop icon listing changed during enumeration.");
            return result;
        }

        internal bool IsAt(nint pidl, IconPosition wanted)
        {
            Check(FolderView.GetItemPosition(pidl, out var point));
            return point.X == wanted.X && point.Y == wanted.Y;
        }

        internal void Refresh() => Check(((ShellNative.IShellView)_view!).Refresh());

        public void Dispose()
        {
            foreach (nint pidl in _pidls) Marshal.FreeCoTaskMem(pidl);
            _pidls.Clear();
            Release(_folder); _folder = null;
            Release(_view); _view = null;
            Release(_browser); _browser = null;
            Release(_dispatch); _dispatch = null;
            Release(_windows); _windows = null;
        }
    }
}
