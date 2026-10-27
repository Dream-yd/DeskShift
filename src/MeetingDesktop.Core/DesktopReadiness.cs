using System.Diagnostics;

namespace MeetingDesktop.Core;

/// <summary>Read-only facts shown before switching, without creating the target directory.</summary>
public sealed record DesktopReadiness(int FileCount, int FolderCount, long TopLevelBytes, long? FreeBytes, bool DynamicWallpaper)
{
    public static DesktopReadiness Inspect(string folder)
    {
        int files = 0, directories = 0;
        long bytes = 0;
        if (Directory.Exists(folder))
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                if (Directory.Exists(path)) directories++;
                else
                {
                    files++;
                    bytes += new FileInfo(path).Length;
                }
            }
        long? free = null;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (root is not null)
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady) free = drive.AvailableFreeSpace;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        bool dynamic = false;
        foreach (string name in new[] { "wallpaper32", "wallpaper64", "Lively" })
        {
            var processes = Process.GetProcessesByName(name);
            dynamic |= processes.Length > 0;
            foreach (var process in processes) process.Dispose();
        }
        return new(files, directories, bytes, free, dynamic);
    }
}
