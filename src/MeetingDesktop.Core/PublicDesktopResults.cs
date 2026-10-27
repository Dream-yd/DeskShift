namespace MeetingDesktop.Core;

public sealed record ShortcutConflict(string SourcePath, string RequestedPath, string PreservedPath);
public sealed record ShortcutMoveResult(int MovedCount, IReadOnlyList<ShortcutConflict> Conflicts);
public sealed record PublicDesktopResidualItem(string Name, string FullPath, bool IsDirectory);
public sealed record PublicDesktopPrecheck(int ShortcutCount, IReadOnlyList<PublicDesktopResidualItem> ResidualItems);
