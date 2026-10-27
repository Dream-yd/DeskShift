using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

/// <summary>Resolves the desktop folder and detects an outstanding desktop switch.</summary>
/// <remarks>
/// User Shell Folders and Shell Folders must agree when both are readable.
/// Known Folder and the profile directory are fallbacks, not additional votes.
/// Path resolution does not prove the active Explorer view; workflows verify it separately.
/// </remarks>
public class DesktopLocationService
{
    /// <summary>Folder the older PowerShell scripts parked the real desktop in.</summary>
    private const string LegacyBackupFolderName = "Desktop_MeetingBackup";

    /// <summary>
    /// Name of the folder this app keeps its own data under, e.g.
    /// <c>%LOCALAPPDATA%\MeetingDesktop</c>.
    /// </summary>
    private const string AppDataFolderName = "MeetingDesktop";

    /// <summary>The desktop subfolder Windows creates inside every user profile.</summary>
    private const string DesktopFolderName = "Desktop";

    /// <summary>
    /// How the computed <see cref="DesktopPathSource.UserProfile"/> candidate
    /// is written down before expansion. Nothing is read for that source, so
    /// this template is what the Diagnostics page shows as its raw value.
    /// </summary>
    private const string UserProfileTemplate = @"%USERPROFILE%\Desktop";

    /// <summary>Note attached to any candidate that already points at the meeting folder.</summary>
    private const string MeetingFolderNote = "Points at the configured meeting folder.";

    /// <summary>Environment variables OneDrive publishes, one per SKU.</summary>
    private static readonly string[] OneDriveVariables = { "OneDrive", "OneDriveCommercial" };

    private readonly DesktopPathService _paths;

    /// <summary>Creates the service on top of the platform-specific path reader.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public DesktopLocationService(DesktopPathService paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>
    /// Reads every known source for the desktop folder and decides whether
    /// they can be trusted.
    /// </summary>
    /// <param name="meetingDesktopDirectory">
    /// The meeting folder the app is configured to use, when one is known.
    /// Candidates that already point at it are annotated rather than removed,
    /// so a caller looking at a swapped machine can see why the sources read
    /// the way they do. Passing null simply skips that annotation.
    /// </param>
    /// <returns>
    /// One candidate per source, plus a status saying whether they agreed.
    /// <see cref="DesktopLocation.ResolvedPath"/> is non-null only when the
    /// status is <see cref="DesktopLocationStatus.Agreed"/> or
    /// <see cref="DesktopLocationStatus.SingleSource"/>.
    /// </returns>
    /// <remarks>
    /// Virtual so a test can hand the workflows a synthetic desktop instead of
    /// the machine's own. The alternative is a test that reads the developer's
    /// registry and only passes on a machine whose desktop happens to be
    /// resolvable, which is not a test.
    /// </remarks>
    public virtual DesktopLocation Detect(string? meetingDesktopDirectory = null)
    {
        IReadOnlyList<DesktopPathCandidate> candidates = new List<DesktopPathCandidate>(4)
        {
            SingleValueCandidate(DesktopPathSource.KnownFolder, _paths.ReadKnownFolder),
            UserShellFoldersCandidate(),
            SingleValueCandidate(DesktopPathSource.ShellFolders, _paths.ReadShellFoldersLegacy),
            ProfileCandidate(),
        };

        if (NormalizeOrNull(meetingDesktopDirectory) is { } meetingFolder)
        {
            candidates = candidates
                .Select(c => PathsEqual(c.ExpandedPath, meetingFolder)
                    ? c with { Note = MeetingFolderNote }
                    : c)
                .ToList();
        }

        return Decide(candidates);
    }

    /// <summary>Resolves readable candidates without guessing between conflicting registry values.</summary>
    /// <param name="candidates">Path sources collected by <see cref="Detect"/>.</param>
    internal static DesktopLocation Decide(IReadOnlyList<DesktopPathCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Sources are in priority order, so the first readable one is also the
        // one whose exact string is reported as the resolved path. Reporting it
        // verbatim - rather than a re-joined copy - keeps it identical to one
        // of the candidates, which is what DesktopLocation.Chosen matches on.
        DesktopPathCandidate? representative = null;

        foreach (DesktopPathCandidate candidate in Voting(candidates))
        {
            if (string.IsNullOrWhiteSpace(candidate.ExpandedPath))
            {
                continue;
            }

            if (representative is null)
            {
                representative = candidate;
                continue;
            }

            if (!PathsEqual(representative.ExpandedPath, candidate.ExpandedPath))
            {
                // Guessing a winner here is what repoints a desktop at the
                // wrong folder. The user picks instead.
                return new DesktopLocation(DesktopLocationStatus.Conflicting, null, candidates, false);
            }
        }

        // Only when no voting source could be read do the non-voting ones
        // become the answer. Neither is a vote, and the order between them is
        // by how directly each reflects the real desktop: the shell's cached
        // answer still beats a folder that merely exists by convention.
        representative ??= Voting(candidates).All(c => string.IsNullOrWhiteSpace(c.ExpandedPath))
            ? KnownFolder(candidates) ?? Profile(candidates)
            : null;

        if (representative?.ExpandedPath is not { } resolved)
        {
            return new DesktopLocation(DesktopLocationStatus.Unresolved, null, candidates, false);
        }

        // A path that is not on disk is not a desktop we can hand to explorer,
        // however many registry values agree about it.
        if (!Directory.Exists(resolved))
        {
            return new DesktopLocation(DesktopLocationStatus.Unresolved, null, candidates, false);
        }

        int readableCount = Voting(candidates)
            .Count(c => !string.IsNullOrWhiteSpace(c.ExpandedPath));

        DesktopLocationStatus status = readableCount > 1
            ? DesktopLocationStatus.Agreed
            : DesktopLocationStatus.SingleSource;

        return new DesktopLocation(status, resolved, candidates, IsUnderOneDrive(resolved));
    }

    /// <summary>The two stored registry paths used to resolve the desktop.</summary>
    /// <remarks>
    /// Known Folder may retain a process-local binding. The profile directory is only a
    /// convention and can legitimately differ after relocation or OneDrive redirection.
    /// Both remain in diagnostics and are used only when the registry has no readable path.
    /// </remarks>
    private static IEnumerable<DesktopPathCandidate> Voting(
        IReadOnlyList<DesktopPathCandidate> candidates) =>
        candidates.Where(c => c.Source is
            DesktopPathSource.UserShellFolders or
            DesktopPathSource.ShellFolders);

    private static DesktopPathCandidate? KnownFolder(IReadOnlyList<DesktopPathCandidate> candidates) =>
        candidates.FirstOrDefault(c =>
            c.Source == DesktopPathSource.KnownFolder &&
            !string.IsNullOrWhiteSpace(c.ExpandedPath));

    private static DesktopPathCandidate? Profile(IReadOnlyList<DesktopPathCandidate> candidates) =>
        candidates.FirstOrDefault(c =>
            c.Source == DesktopPathSource.UserProfile &&
            !string.IsNullOrWhiteSpace(c.ExpandedPath));

    /// <summary>Classifies the resolved desktop as normal, switched, legacy, or unresolved.</summary>
    /// <remarks>
    /// An unresolved path blocks another switch. Legacy-looking folders require review
    /// because their original desktop may not be recorded by this application.
    /// </remarks>
    public SessionState DetectSessionState(string? meetingDesktopDirectory)
    {
        DesktopLocation location = Detect(meetingDesktopDirectory);
        return ClassifySessionState(location, meetingDesktopDirectory);
    }

    public static SessionState ClassifySessionState(DesktopLocation location, string? meetingDesktopDirectory)
    {
        if (location.ResolvedPath is not { } current)
        {
            return SessionState.Inconsistent;
        }

        if (NormalizeOrNull(meetingDesktopDirectory) is { } meetingFolder &&
            PathsEqual(current, meetingFolder))
        {
            return SessionState.Swapped;
        }

        return LooksLikeLegacyMeetingFolder(current)
            ? SessionState.Adopted
            : SessionState.Normal;
    }

    /// <summary>Recognizes the older scripts' backup naming and this app's default meeting folder.</summary>
    /// <remarks>A matching name requests review; it does not authorize restoration.</remarks>
    private static bool LooksLikeLegacyMeetingFolder(string path)
    {
        string normalized = Normalize(path);
        string name = Path.GetFileName(normalized);

        if (name.EndsWith(DesktopFolderName, StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(normalized);
            if (parent is not null && Directory.Exists(Path.Combine(parent, LegacyBackupFolderName)))
            {
                return true;
            }
        }

        string parentName = Path.GetFileName(Path.GetDirectoryName(normalized) ?? string.Empty);
        return parentName.StartsWith(AppDataFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a path sits inside either of OneDrive's roots.</summary>
    /// <remarks>
    /// Read from the environment on every call rather than cached: OneDrive
    /// sets these per session, so a process that started before sign-in would
    /// otherwise remember the wrong answer for its whole life.
    /// </remarks>
    private static bool IsUnderOneDrive(string path)
    {
        string normalized = Normalize(path);

        foreach (string variable in OneDriveVariables)
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string normalizedRoot = Normalize(root);

            // The separator is appended before the prefix test so that
            // "C:\Users\me\OneDriveOld" cannot pass as a child of
            // "C:\Users\me\OneDrive".
            if (normalized.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the candidate for a source that hands back one already-expanded
    /// path.
    /// </summary>
    /// <remarks>
    /// Used for <see cref="DesktopPathSource.ShellFolders"/> as well as the
    /// known folder: the legacy key is read with its variables already
    /// expanded, so for that source the raw and expanded values are the same
    /// string and both fields carry it.
    /// </remarks>
    private DesktopPathCandidate SingleValueCandidate(DesktopPathSource source, Func<string?> read)
    {
        string? value = TryRead(read, out string? failure);

        return value is null
            ? Unreadable(source, failure)
            : PathCandidate(source, value, value);
    }

    /// <summary>Builds the candidate for the live <c>User Shell Folders</c> value.</summary>
    /// <remarks>
    /// Both readers are used so the row carries what was stored next to what it
    /// expands to. An install storing <c>%USERPROFILE%\Desktop</c> is behaving
    /// correctly, and only the pair makes that visible on the Diagnostics page.
    /// </remarks>
    private DesktopPathCandidate UserShellFoldersCandidate()
    {
        string? raw = TryRead(_paths.ReadUserShellFoldersRaw, out string? rawFailure);
        string? expanded = TryRead(_paths.ReadUserShellFolders, out string? expandedFailure);

        if (expanded is null)
        {
            return raw is null
                ? Unreadable(DesktopPathSource.UserShellFolders, rawFailure ?? expandedFailure)
                : new DesktopPathCandidate(
                    DesktopPathSource.UserShellFolders,
                    raw.Trim(),
                    null,
                    false,
                    expandedFailure ?? "The stored value could not be expanded into a path.");
        }

        return PathCandidate(DesktopPathSource.UserShellFolders, raw ?? expanded, expanded);
    }

    /// <summary>Builds the candidate for the desktop inside the user profile.</summary>
    /// <remarks>
    /// This source is recorded even when the folder is missing, because it is
    /// the one that says "this user has no desktop folder here", and the
    /// Diagnostics page needs to be able to show that.
    /// </remarks>
    private static DesktopPathCandidate ProfileCandidate()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrWhiteSpace(profile))
        {
            return new DesktopPathCandidate(
                DesktopPathSource.UserProfile,
                UserProfileTemplate,
                null,
                false,
                "The user profile folder could not be located.");
        }

        string path = Path.Combine(profile, DesktopFolderName);

        return new DesktopPathCandidate(
            DesktopPathSource.UserProfile,
            UserProfileTemplate,
            path,
            Directory.Exists(path));
    }

    /// <summary>Turns an expanded path into a candidate, or into a readable failure.</summary>
    private static DesktopPathCandidate PathCandidate(DesktopPathSource source, string raw, string expanded)
    {
        string trimmed = expanded.Trim();

        // A surviving % means an environment variable was not expanded. The
        // registry readers deliberately leave such text untouched so the typo
        // stays visible rather than turning into a blank value that looks
        // "unset". It is not a usable path, so this source counts as having
        // nothing instead of counting as disagreeing with the other three.
        if (trimmed.Contains('%'))
        {
            return new DesktopPathCandidate(
                source,
                raw.Trim(),
                null,
                false,
                "The stored value still contains an environment variable after expansion.");
        }

        return new DesktopPathCandidate(source, raw.Trim(), trimmed, Directory.Exists(trimmed));
    }

    /// <summary>Builds the candidate for a source that had nothing to say.</summary>
    private static DesktopPathCandidate Unreadable(DesktopPathSource source, string? failure) =>
        new(source, string.Empty, null, false, failure ?? "No value is stored here.");

    /// <summary>
    /// Reads one source, turning a refusal into a null result.
    /// </summary>
    /// <remarks>
    /// The readers already swallow a missing value or a denied key, so this is
    /// a second line of defence rather than the main one. It is here because
    /// losing one source must not lose the other three: detection runs on
    /// machines the app has never seen, and a throw would take down the whole
    /// Diagnostics page instead of graying out one row of it.
    /// </remarks>
    private static string? TryRead(Func<string?> read, out string? failure)
    {
        try
        {
            failure = null;
            return read();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            failure = $"Could not be read ({ex.GetType().Name}).";
            return null;
        }
    }

    /// <summary>Trims a path for comparison, or null when there is nothing to compare.</summary>
    private static string? NormalizeOrNull(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Normalize(path);

    /// <summary>
    /// Trims surrounding whitespace and any trailing directory separators.
    /// </summary>
    /// <remarks>
    /// Only trailing separators are removed. <see cref="Path.GetFullPath"/>
    /// would also fold in the current directory and resolve relative segments,
    /// which would hide a source that produced a relative path - a bug worth
    /// seeing, not repairing silently. The length check keeps <c>C:\</c>
    /// intact.
    /// </remarks>
    private static string Normalize(string path)
    {
        string trimmed = path.Trim();

        while (trimmed.Length > 3 && (trimmed[^1] == '\\' || trimmed[^1] == '/'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }

    /// <summary>Compares two paths the way Windows does: case-insensitively, ignoring trailing separators.</summary>
    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }
}
