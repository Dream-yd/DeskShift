namespace MeetingDesktop.Core.Models;

/// <summary>Where a candidate desktop path came from.</summary>
public enum DesktopPathSource
{
    /// <summary>
    /// <c>SHGetKnownFolderPath(FOLDERID_Desktop)</c>.
    /// </summary>
    /// <remarks>
    /// Reported for diagnostics but not counted as a vote - the shell answers
    /// from a per-process cache, so this is a stale copy of
    /// <see cref="UserShellFolders"/> rather than an independent witness. See
    /// the remarks on <c>DesktopLocationService</c>.
    /// </remarks>
    KnownFolder,

    /// <summary><c>HKCU\...\Explorer\User Shell Folders\Desktop</c> (REG_EXPAND_SZ).</summary>
    UserShellFolders,

    /// <summary><c>HKCU\...\Explorer\Shell Folders\Desktop</c> - a legacy cache.</summary>
    ShellFolders,

    /// <summary><c>%USERPROFILE%\Desktop</c>.</summary>
    UserProfile,

    /// <summary>The value this app itself last wrote.</summary>
    AppRecorded,
}

/// <summary>One place a desktop path was read from.</summary>
/// <param name="Source">Which source produced it.</param>
/// <param name="RawValue">Exactly what was read, before any expansion.</param>
/// <param name="ExpandedPath">After environment-variable expansion, or null if expansion failed.</param>
/// <param name="Exists">Whether the folder is present on disk.</param>
/// <param name="Note">Anything unusual about this source, for the Diagnostics page.</param>
public sealed record DesktopPathCandidate(
    DesktopPathSource Source,
    string RawValue,
    string? ExpandedPath,
    bool Exists,
    string? Note = null);

/// <summary>How confident the resolution is.</summary>
public enum DesktopLocationStatus
{
    /// <summary>Every source that could be read agreed. Safe to use.</summary>
    Agreed,

    /// <summary>Sources disagreed. The user must pick; the app will not guess.</summary>
    Conflicting,

    /// <summary>Only one source could be read and it looks usable.</summary>
    SingleSource,

    /// <summary>No usable path was found, or the one found does not exist.</summary>
    Unresolved,
}

/// <summary>
/// The real desktop folder, plus how it was determined.
/// </summary>
/// <remarks>
/// The prototype read one registry value and trusted it. That is wrong on any
/// machine where the desktop has been redirected - OneDrive takes it over by
/// default on many installs - and it silently produced the wrong answer rather
/// than failing.
///
/// When sources disagree the app deliberately refuses to pick. Guessing here
/// means repointing a user's desktop at the wrong folder, which looks to them
/// like their files have vanished.
/// </remarks>
public sealed record DesktopLocation(
    DesktopLocationStatus Status,
    string? ResolvedPath,
    IReadOnlyList<DesktopPathCandidate> Candidates,
    bool IsOneDriveRedirected)
{
    /// <summary>True when <see cref="ResolvedPath"/> may be used without asking the user.</summary>
    public bool IsUsable =>
        Status is DesktopLocationStatus.Agreed or DesktopLocationStatus.SingleSource &&
        !string.IsNullOrWhiteSpace(ResolvedPath);

    /// <summary>The candidate the app would use, when it is confident.</summary>
    public DesktopPathCandidate? Chosen =>
        ResolvedPath is null
            ? null
            : Candidates.FirstOrDefault(c =>
                string.Equals(c.ExpandedPath, ResolvedPath, StringComparison.OrdinalIgnoreCase));
}
