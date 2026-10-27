namespace MeetingDesktop.Core.Models;

/// <summary>Why a candidate meeting-desktop folder is or is not acceptable.</summary>
public enum PathIssue
{
    /// <summary>Nothing wrong.</summary>
    None,

    /// <summary>The path is empty, relative, or otherwise not a full path.</summary>
    NotAbsolute,

    /// <summary>The folder could not be created.</summary>
    CannotCreate,

    /// <summary>A write-and-read-back probe failed. The folder is not usable even if it exists.</summary>
    NotWritable,

    /// <summary>
    /// The volume is removable or on the network.
    /// </summary>
    /// <remarks>
    /// Worth warning about loudly rather than refusing: if the drive is not
    /// present at sign-in, the desktop points at a path that does not exist
    /// and the user's desktop appears to have vanished.
    /// </remarks>
    NotFixedDisk,

    /// <summary>The path is the user's real desktop, or inside it.</summary>
    OverlapsRealDesktop,

    /// <summary>The path is inside a protected system location.</summary>
    ProtectedLocation,
}

/// <summary>Result of validating a candidate meeting-desktop folder.</summary>
/// <param name="IsAcceptable">No blocking issue. Warnings may still be present.</param>
/// <param name="Issues">Everything found, including non-blocking warnings.</param>
/// <param name="NormalizedPath">The expanded, absolute path that was checked.</param>
public sealed record PathValidation(
    bool IsAcceptable,
    IReadOnlyList<PathIssue> Issues,
    string? NormalizedPath)
{
    public bool Has(PathIssue issue) => Issues.Contains(issue);
}
