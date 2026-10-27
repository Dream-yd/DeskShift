using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

/// <summary>Reads a user-selected legacy record without modifying the desktop.</summary>
public static class LegacyRecoveryRecord
{
    public static SessionInfo Read(string recordPath, string currentDesktopPath)
    {
        if (!string.Equals(Path.GetFileName(recordPath), "original_desktop_path.txt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select original_desktop_path.txt.", nameof(recordPath));
        var file = new FileInfo(recordPath);
        if (file.Length > 32768) throw new ArgumentException("Legacy record is too large.", nameof(recordPath));
        string original = File.ReadAllText(recordPath).Trim().TrimStart('\uFEFF');
        if (!Path.IsPathFullyQualified(original) || !Path.IsPathFullyQualified(currentDesktopPath))
            throw new ArgumentException("Desktop folders must be absolute paths.");
        original = Path.TrimEndingDirectorySeparator(Path.GetFullPath(original));
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDesktopPath));
        if (!Directory.Exists(original) || !Directory.Exists(current) ||
            string.Equals(original, current, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Original folder must exist and differ from the current desktop.");
        return new SessionInfo
        {
            OriginalDesktopPath = original,
            MeetingDesktopPathAtSwap = current,
            SwappedUtc = file.LastWriteTimeUtc,
            AutoLayoutSnapshotPath = null,
            CapturedIconCount = 0
        };
    }
}
