namespace MeetingDesktop.Core;

/// <summary>
/// Moves the public desktop's shortcuts out of the way and puts them back.
/// </summary>
/// <remarks>
/// <para>
/// Swapping the <em>user's</em> desktop for an empty meeting folder leaves the
/// <em>public</em> desktop on screen untouched, and that folder is where
/// machine-wide installers put their shortcuts - so a presentation that is
/// supposed to show a clean screen still shows them. Removing them needs
/// administrator rights, which makes this the only part of the application
/// that must run elevated.
/// </para>
/// <para>
/// The same code runs in both processes. The normal-privilege app calls
/// <see cref="MoveShortcutsAside"/> and <see cref="RestoreShortcuts"/> when it
/// already has the rights it needs, and the elevated one-shot helper calls
/// exactly these methods when it does not. Nothing here may therefore depend
/// on application state, settings, or a path passed in from outside: both
/// folders are computed from the shell's own answer. If the two sides
/// disagreed about where the shortcuts live, one of them would be rearranging
/// a directory the other one knows nothing about, and the desktop would come
/// back with shortcuts missing.
/// </para>
/// <para>
/// The backup folder is a sibling of the public desktop, which has two
/// consequences worth knowing. Both are on the same volume, so each move is a
/// rename rather than a copy and cannot leave a half-written file behind. And
/// the folder is visible in Explorer, so an interrupted run can be finished by
/// hand instead of only by re-running the app.
/// </para>
/// <para>
/// Neither method throws when there is nothing to do, and both are safe to run
/// twice: a restore that follows a crash may be completing work that a
/// previous run had already finished.
/// </para>
/// </remarks>
public static class PublicDesktopService
{
    /// <summary>Read-only inventory of public entries left visible after parking .lnk files.
    /// Hidden items other than desktop.ini are included conservatively. Access failures propagate.</summary>
    public static PublicDesktopPrecheck Precheck() => Precheck(PublicDesktopDirectory);

    internal static PublicDesktopPrecheck Precheck(string directory)
    {
        if (!Directory.Exists(directory)) return new(0, []);
        int shortcuts = 0;
        var residual = new List<PublicDesktopResidualItem>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(entry);
            bool folder = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
            if (!folder && Path.GetExtension(name).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) shortcuts++;
            else if (folder || !name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                residual.Add(new(name, entry, folder));
        }
        return new(shortcuts, residual);
    }

    /// <summary>Detailed fixed-path operations for callers that display collision reports.</summary>
    public static ShortcutMoveResult MoveShortcutsAsideDetailed() => MoveShortcuts(PublicDesktopDirectory, BackupDirectory);
    public static ShortcutMoveResult RestoreShortcutsDetailed()
    {
        var result = MoveShortcuts(BackupDirectory, PublicDesktopDirectory);
        DeleteBackupIfEmpty();
        return result;
    }

    // Internal only: the elevated command line must never accept caller-supplied paths.
    internal static ShortcutMoveResult MoveShortcuts(string source, string destination)
    {
        var shortcuts = EnumerateShortcuts(source).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (shortcuts.Length == 0) return new(0, []);
        Directory.CreateDirectory(destination);
        return MoveAll(shortcuts, source, destination);
    }
    /// <summary>
    /// Name of the backup folder. A <c>const</c> rather than a literal so the
    /// elevated helper and the normal-privilege code cannot be compiled
    /// against different names.
    /// </summary>
    private const string BackupFolderName = "Desktop_MeetingBackup";

    /// <summary>
    /// Pattern for the only thing this class is allowed to touch. Everything
    /// else on the public desktop, <c>desktop.ini</c> above all, stays where
    /// it is - moving the shell's own folder configuration would change how
    /// the folder is displayed rather than what is in it.
    /// </summary>
    private const string ShortcutPattern = "*.lnk";

    // Resolved once, in declaration order, so the backup path is derived from
    // the same string the public desktop path was.
    private static readonly string PublicDesktopPath = ResolvePublicDesktopDirectory();
    private static readonly string BackupPath = ResolveBackupDirectory(PublicDesktopPath);

    /// <summary>
    /// The public desktop folder, <c>%PUBLIC%\Desktop</c> unless it has been
    /// redirected.
    /// </summary>
    public static string PublicDesktopDirectory => PublicDesktopPath;

    /// <summary>Where the shortcuts are parked while the desktop is swapped.</summary>
    public static string BackupDirectory => BackupPath;

    /// <summary>
    /// Moves every shortcut off the public desktop into <see cref="BackupDirectory"/>.
    /// </summary>
    /// <returns>
    /// How many shortcuts were moved. Zero when the public desktop held none,
    /// which includes being called a second time after a successful move.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">
    /// The caller is not elevated. This is the expected failure when the
    /// normal-privilege process calls in without going through the elevated
    /// helper first.
    /// </exception>
    /// <exception cref="PublicDesktopMoveException">
    /// The move stopped partway, so some shortcuts are on the public desktop
    /// and some are in the backup.
    /// </exception>
    public static int MoveShortcutsAside() => MoveShortcutsAsideDetailed().MovedCount;

    /// <summary>
    /// Moves the parked shortcuts back onto the public desktop.
    /// </summary>
    /// <returns>
    /// How many shortcuts were restored. Zero when the backup held none, which
    /// includes being called a second time after a successful restore.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">
    /// The caller is not elevated.
    /// </exception>
    /// <exception cref="PublicDesktopMoveException">
    /// The move stopped partway, so some shortcuts are back on the public
    /// desktop and some are still in the backup.
    /// </exception>
    public static int RestoreShortcuts() => RestoreShortcutsDetailed().MovedCount;

    /// <summary>
    /// Lists the shortcuts in a folder, treating a missing folder as an empty
    /// one.
    /// </summary>
    /// <remarks>
    /// Top-level only. A folder on the public desktop is not a shortcut, and
    /// recursing would drag its contents along with it.
    /// </remarks>
    private static string[] EnumerateShortcuts(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(directory, ShortcutPattern, SearchOption.TopDirectoryOnly);
    }

    /// <summary>
    /// Moves each shortcut, and reports a mixed result as a single exception
    /// rather than leaving the caller to work out what actually happened.
    /// </summary>
    private static ShortcutMoveResult MoveAll(
        IReadOnlyList<string> shortcuts,
        string sourceDirectory,
        string destinationDirectory)
    {
        int moved = 0;
        var conflicts = new List<ShortcutConflict>();

        foreach (string shortcut in shortcuts)
        {
            string destination = Path.Combine(destinationDirectory, Path.GetFileName(shortcut));

            try
            {
                string requested = destination;
                int suffix = 0;
                while (true)
                {
                    try
                    {
                        // No overwrite, including if another writer wins the name after our check.
                        File.Move(shortcut, destination, overwrite: false);
                        break;
                    }
                    catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
                    {
                        destination = Path.Combine(destinationDirectory,
                            $"{Path.GetFileNameWithoutExtension(shortcut)} (restored {++suffix}){Path.GetExtension(shortcut)}");
                    }
                }
                if (suffix > 0) conflicts.Add(new(shortcut, requested, destination));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The counts are the point of this exception: "it failed" is
                // not enough to recover from, because the two folders are now
                // holding different halves of the desktop and both need to be
                // looked at.
                throw new PublicDesktopMoveException(
                    $"Moving shortcuts from '{sourceDirectory}' to '{destinationDirectory}' failed after " +
                    $"{moved} of {shortcuts.Count}: '{Path.GetFileName(shortcut)}' could not be moved ({ex.Message}). " +
                    $"{shortcuts.Count - moved} shortcut(s) are still in '{sourceDirectory}' and {moved} are now in " +
                    $"'{destinationDirectory}', so the public desktop is split across the two folders. " +
                    "Both folders are intact, so the remaining files can be moved by hand.",
                    moved,
                    shortcuts.Count - moved,
                    shortcut,
                    ex) { CompletedConflicts = conflicts.ToArray() };
            }

            moved++;
        }

        return new(moved, conflicts.ToArray());
    }

    /// <summary>
    /// Removes the backup folder, but only when it is genuinely empty.
    /// </summary>
    /// <remarks>
    /// The emptiness check is what guarantees a folder still holding
    /// shortcuts is never deleted; the non-recursive delete after it is a
    /// second line of defence, because it fails rather than sweeping up
    /// anything that arrived between the check and the call - a shortcut the
    /// user recreated by hand, or a file an installer wrote.
    ///
    /// A failure to delete is swallowed on purpose. By the time this runs the
    /// shortcuts themselves are already back where they belong, so the restore
    /// has succeeded; turning a success into an exception over a leftover
    /// folder would be the worse outcome. The folder is visible, so the user
    /// can remove it with no special knowledge if it ever needs removing.
    /// </remarks>
    private static void DeleteBackupIfEmpty()
    {
        if (!Directory.Exists(BackupDirectory) ||
            Directory.EnumerateFileSystemEntries(BackupDirectory).Any())
        {
            return;
        }

        try
        {
            Directory.Delete(BackupDirectory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something took a handle on the folder between the check and the
            // delete. Nothing left to do about it here.
        }
    }

    /// <summary>
    /// Asks the shell for the public desktop rather than hardcoding
    /// <c>%PUBLIC%\Desktop</c>, which is only the default.
    /// </summary>
    private static string ResolvePublicDesktopDirectory()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "Environment.GetFolderPath(CommonDesktopDirectory) returned an empty path, so there is no public " +
                "desktop to move shortcuts out of.");
        }

        // The shell is free to return a trailing separator, and
        // Path.GetDirectoryName would then return the folder itself instead of
        // its parent - which would put the backup folder inside the public
        // desktop and make MoveShortcutsAside back up the backup.
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static string ResolveBackupDirectory(string publicDesktopDirectory)
    {
        string? parent = Path.GetDirectoryName(publicDesktopDirectory);

        if (string.IsNullOrEmpty(parent))
        {
            throw new InvalidOperationException(
                $"'{publicDesktopDirectory}' has no parent folder, so the '{BackupFolderName}' backup folder cannot " +
                "be placed beside it.");
        }

        return Path.Combine(parent, BackupFolderName);
    }
}

/// <summary>
/// A move of the public desktop's shortcuts stopped partway, leaving some of
/// them in the source folder and some in the destination.
/// </summary>
/// <remarks>
/// Derives from <see cref="IOException"/> so callers that already treat a
/// failed file move as an I/O problem keep working, while the counts describe
/// the state of both folders. A half-moved public desktop is otherwise very
/// hard to diagnose: it presents as some shortcuts reappearing and others
/// staying gone, which reads as a random failure rather than a stopped move.
/// </remarks>
public sealed class PublicDesktopMoveException : IOException
{
    public PublicDesktopMoveException(
        string message,
        int movedCount,
        int remainingCount,
        string failedPath,
        Exception? innerException = null)
        : base(message, innerException)
    {
        MovedCount = movedCount;
        RemainingCount = remainingCount;
        FailedPath = failedPath;
    }

    /// <summary>How many shortcuts were moved before the failure.</summary>
    public int MovedCount { get; }

    public IReadOnlyList<ShortcutConflict> CompletedConflicts { get; init; } = [];

    /// <summary>How many are still in the folder they started in.</summary>
    public int RemainingCount { get; }

    /// <summary>Full path of the shortcut that could not be moved.</summary>
    public string FailedPath { get; }
}
