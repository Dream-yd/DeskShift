using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Workflows;

/// <summary>Validates broker data before using it solely as icon identity information.</summary>
internal static class PublicShortcutRecovery
{
    internal static IReadOnlyList<ShortcutConflict> Merge(IReadOnlyList<ShortcutConflict>? prior, IReadOnlyList<ShortcutConflict> added,
        string backupDirectory, string publicDirectory)
    {
        var all = (prior ?? []).Concat(added).ToArray();
        var byRequested = new Dictionary<string, ShortcutConflict>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in all)
        {
            if (!Valid(conflict, backupDirectory, publicDirectory)) throw new RestoreNotPossibleException("The public shortcut conflict report is invalid; the recovery record was kept.");
            if (byRequested.TryGetValue(conflict.RequestedPath, out var existing))
            {
                if (!string.Equals(existing.PreservedPath, conflict.PreservedPath, StringComparison.OrdinalIgnoreCase))
                    throw new RestoreNotPossibleException("Conflicting shortcut recovery records require review.");
                continue;
            }
            if (!destinations.Add(conflict.PreservedPath)) throw new RestoreNotPossibleException("Ambiguous shortcut recovery records require review.");
            byRequested.Add(conflict.RequestedPath, conflict);
        }
        return byRequested.Values.ToArray();
    }

    internal static bool ValidReport(ElevatedOperationReport report, ElevationOutcome outcome, string backupDirectory)
    {
        if (report.Result is null || report.Result.Conflicts is null || report.Result.MovedCount < 0 ||
            report.RemainingCount < 0 || report.Result.Conflicts.Count > report.Result.MovedCount) return false;
        if (outcome == ElevationOutcome.Succeeded)
            return report.Succeeded && report.RemainingCount == 0 && report.FailedPath is null;
        if (outcome != ElevationOutcome.Failed || report.Succeeded || report.RemainingCount == 0 ||
            string.IsNullOrWhiteSpace(report.FailedPath)) return false;
        try { return DirectChild(report.FailedPath, backupDirectory) &&
            string.Equals(Path.GetExtension(report.FailedPath), ".lnk", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static bool Valid(ShortcutConflict? c, string backupDirectory, string publicDirectory)
    {
        try
        {
            if (c is null || !DirectChild(c.SourcePath, backupDirectory) ||
                !DirectChild(c.RequestedPath, publicDirectory) ||
                !DirectChild(c.PreservedPath, publicDirectory) ||
                !string.Equals(Path.GetFileName(c.SourcePath), Path.GetFileName(c.RequestedPath), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(c.RequestedPath), ".lnk", StringComparison.OrdinalIgnoreCase)) return false;
            string prefix = Path.GetFileNameWithoutExtension(c.RequestedPath) + " (restored ";
            string name = Path.GetFileName(c.PreservedPath);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(").lnk", StringComparison.OrdinalIgnoreCase)) return false;
            return int.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - 5), out int suffix) && suffix > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static bool DirectChild(string path, string directory) => Path.IsPathFullyQualified(path) &&
        string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase);

    internal static (LayoutSnapshot? Layout, IReadOnlyList<string> Unmapped) Map(LayoutSnapshot? snapshot, IReadOnlyList<ShortcutConflict> conflicts)
    {
        if (snapshot is null || conflicts.Count == 0) return (snapshot, []);
        var map = conflicts.ToDictionary(c => c.RequestedPath, c => c.PreservedPath, StringComparer.OrdinalIgnoreCase);
        var candidates = snapshot.Icons.Select(icon =>
        {
            if (string.IsNullOrWhiteSpace(icon.Identity)) return (Icon: icon, Safe: false);
            if (!map.TryGetValue(icon.Identity, out string? path)) return (Icon: icon, Safe: true);
            return icon.IsFileSystem == true ? (Icon: icon with { Identity = path }, Safe: true) : (Icon: icon, Safe: false);
        }).ToArray();
        var duplicates = candidates.Where(c => c.Safe && !string.IsNullOrWhiteSpace(c.Icon.Identity))
            .GroupBy(c => c.Icon.Identity!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Safe((IconPosition Icon, bool Safe) candidate) => candidate.Safe &&
            (candidate.Icon.Identity is null || !duplicates.Contains(candidate.Icon.Identity));
        return (snapshot with { Icons = candidates.Where(Safe).Select(c => c.Icon).ToArray() },
            candidates.Where(c => !Safe(c)).Select(c => c.Icon.Name).ToArray());
    }
}
