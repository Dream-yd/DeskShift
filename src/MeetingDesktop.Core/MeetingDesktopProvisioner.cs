using System.Security;
using System.Text;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

/// <summary>
/// Checks that a candidate meeting-desktop folder is usable, and creates it.
/// </summary>
/// <remarks>
/// <para>
/// The meeting desktop is the folder the shell is redirected to while
/// presenting. It is created with four subfolders - <c>state</c>,
/// <c>layouts</c>, <c>recovery</c> and <c>logs</c> - so that everything the
/// app writes lives under one directory the user can back up or delete
/// wholesale.
/// </para>
/// <para>
/// Validation collects every finding rather than stopping at the first one.
/// The settings page shows the whole list at once, and a path that breaks
/// three rules should not take three round trips to fix.
/// </para>
/// <para>
/// Nothing here reads or writes the registry, so validation never has a side
/// effect on the running desktop and is safe to call on the UI thread. The
/// only thing <see cref="ValidateAndCreate"/> touches is the folder itself.
/// </para>
/// </remarks>
public sealed class MeetingDesktopProvisioner
{
    /// <summary>Subfolders every meeting desktop carries. Created but not required to be non-empty.</summary>
    private static readonly string[] Subfolders = { "state", "layouts", "recovery", "logs" };

    /// <summary>
    /// Prefix for the writability probe file.
    /// </summary>
    /// <remarks>
    /// Dotted so it does not look like user content if a pass is interrupted
    /// between write and delete, and unlikely to collide with anything the
    /// user keeps there.
    /// </remarks>
    private const string ProbePrefix = ".write-probe-";

    private readonly Func<string?> _realDesktopPath;

    /// <summary>
    /// Creates a provisioner.
    /// </summary>
    /// <param name="realDesktopPath">
    /// Resolves the current user's real desktop folder, or returns null when
    /// it cannot be determined. A delegate rather than a string because
    /// resolving it reads the registry and can fail; the caller owns what a
    /// failure means. It is expected not to throw - report failure as null.
    /// </param>
    public MeetingDesktopProvisioner(Func<string?> realDesktopPath)
    {
        ArgumentNullException.ThrowIfNull(realDesktopPath);
        _realDesktopPath = realDesktopPath;
    }

    /// <summary>
    /// Checks a candidate path and reports everything wrong with it, without
    /// creating anything.
    /// </summary>
    /// <param name="candidatePath">The path to check. Environment variables and surrounding quotes are expanded.</param>
    /// <returns>
    /// Every finding, including non-blocking warnings. <see cref="PathIssue.None"/>
    /// is never listed - an empty list already says nothing was found.
    /// </returns>
    public PathValidation Validate(string candidatePath)
    {
        Candidate candidate = Inspect(candidatePath, _realDesktopPath);
        return new PathValidation(!candidate.Issues.Any(IsBlocking), candidate.Issues, candidate.Normalized);
    }

    /// <summary>
    /// Checks a candidate path, creates the folder and its subfolders if they
    /// are missing, and proves it is writable.
    /// </summary>
    /// <param name="candidatePath">The path to check and create.</param>
    /// <returns>
    /// Every finding. The folder is left in place on success; on failure it may
    /// exist but be unusable.
    /// </returns>
    public PathValidation ValidateAndCreate(string candidatePath)
    {
        Candidate candidate = Inspect(candidatePath, _realDesktopPath);

        // Creation is skipped when a blocking issue already rules the path out:
        // a path inside the real desktop or under Program Files is rejected, and
        // rejecting it after creating it would leave a folder behind in exactly
        // the place we just called unsafe.
        if (candidate.FullPath is not null && !candidate.Issues.Any(IsBlocking))
        {
            if (TryCreate(candidate.FullPath, candidate.Issues) && !ProbeWritable(candidate.FullPath))
            {
                candidate.Issues.Add(PathIssue.NotWritable);
            }
        }

        return new PathValidation(!candidate.Issues.Any(IsBlocking), candidate.Issues, candidate.Normalized);
    }

    /// <summary>
    /// Runs every check that does not touch the file system.
    /// </summary>
    /// <remarks>
    /// The path is expanded before it is judged, so <c>%USERPROFILE%\Meeting</c>
    /// is compared against the real desktop as the folder it actually names.
    /// A path that cannot be parsed at all is reported as
    /// <see cref="PathIssue.NotAbsolute"/>: the enum already covers "otherwise
    /// not a full path", and inventing a second kind for it would make callers
    /// handle the same situation twice.
    /// </remarks>
    private static Candidate Inspect(string candidatePath, Func<string?> realDesktopPath)
    {
        var issues = new List<PathIssue>();

        string? expanded = Expand(candidatePath);

        string? fullPath = null;
        if (expanded is not null && Path.IsPathFullyQualified(expanded))
        {
            try
            {
                fullPath = Path.GetFullPath(expanded);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
            {
                fullPath = null;
            }
        }

        if (fullPath is null)
        {
            issues.Add(PathIssue.NotAbsolute);
            return new Candidate(expanded, null, issues);
        }

        string? realDesktop = TryResolveRealDesktop(realDesktopPath);
        if (realDesktop is not null && IsSameOrInside(fullPath, realDesktop))
        {
            issues.Add(PathIssue.OverlapsRealDesktop);
        }

        if (ProtectedRoots().Any(root => IsSameOrInside(fullPath, root)))
        {
            issues.Add(PathIssue.ProtectedLocation);
        }

        if (!IsOnFixedDisk(fullPath))
        {
            issues.Add(PathIssue.NotFixedDisk);
        }

        return new Candidate(fullPath, fullPath, issues);
    }

    /// <summary>Trims and expands a candidate, returning null when nothing usable was supplied.</summary>
    private static string? Expand(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return null;
        }

        // Quotes come from a user pasting a path out of Explorer's address bar.
        // A NUL cannot survive the checks below and would make the expansion
        // itself throw, so it is treated as "no usable input".
        string trimmed = candidatePath.Trim().Trim('"').Trim();
        if (trimmed.Length == 0 || trimmed.Contains('\0'))
        {
            return null;
        }

        return Environment.ExpandEnvironmentVariables(trimmed);
    }

    /// <summary>
    /// Resolves the real desktop for comparison, or null when it cannot be
    /// compared.
    /// </summary>
    /// <remarks>
    /// A null or unparseable value means "unknown", and the overlap check is
    /// skipped rather than failed: refusing every path because the desktop
    /// could not be located would make the app unusable on a machine where the
    /// shell is slow to answer, and the overlap check is a guard against a
    /// mistake, not a security boundary.
    /// </remarks>
    private static string? TryResolveRealDesktop(Func<string?> realDesktopPath)
    {
        string? value = realDesktopPath();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (!Path.IsPathFullyQualified(expanded))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>Locations a meeting desktop must never live in, as named by the environment.</summary>
    private static IEnumerable<string> ProtectedRoots()
    {
        string? programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        string? programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return programFiles;

            // WindowsApps holds packaged-app payloads and is re-signed on
            // repair; anything written into it is lost or breaks signature
            // checks. It usually sits under ProgramFiles already, but it is
            // listed separately because that is not guaranteed forever.
            yield return Path.Combine(programFiles, "WindowsApps");
        }

        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return programFilesX86;
        }

        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            yield return systemRoot;
        }
    }

    /// <summary>Whether a path is a fixed local disk.</summary>
    /// <remarks>
    /// Removable and network volumes only warn. They are legitimate - a user
    /// may keep the meeting desktop on a second drive - but if the volume is
    /// absent at sign-in the desktop is redirected to a path that does not
    /// exist, and the user's desktop appears to have vanished until they plug
    /// the drive back in. That is worth a sentence in the UI, not a refusal.
    /// </remarks>
    private static bool IsOnFixedDisk(string fullPath)
    {
        // Canonical form first: GetPathRoot on a \\?\ path yields the device
        // root, which DriveInfo does not recognise as a fixed disk, and the
        // caller would get a network warning about their own C: drive.
        string? root = Path.GetPathRoot(Canonical(fullPath));
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
        {
            // An unreadable volume is not a fixed disk we can vouch for.
            return false;
        }
    }

    /// <summary>Creates the meeting desktop and its subfolders.</summary>
    private static bool TryCreate(string fullPath, List<PathIssue> issues)
    {
        try
        {
            Directory.CreateDirectory(fullPath);

            foreach (string subfolder in Subfolders)
            {
                Directory.CreateDirectory(Path.Combine(fullPath, subfolder));
            }

            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            // Includes "the path is an existing file", which CreateDirectory
            // reports as IOException. The folder is unusable either way.
            issues.Add(PathIssue.CannotCreate);
            return false;
        }
    }

    /// <summary>
    /// Proves the folder is writable by writing a file, reading it back, and
    /// deleting it.
    /// </summary>
    /// <remarks>
    /// The probe is a real write on purpose. ACLs are the obvious thing to
    /// check instead and they are routinely wrong: inherited denies, virtualized
    /// redirects under the app data folder, and a drive that has gone read-only
    /// after a failure all read as permitted on paper while the write fails.
    /// The only honest test is to write the file.
    /// </remarks>
    private static bool ProbeWritable(string folder)
    {
        string probePath = Path.Combine(folder, ProbePrefix + Guid.NewGuid().ToString("N") + ".tmp");

        // Unique content, so a pre-existing file at the probe path cannot make
        // the read-back pass.
        byte[] payload = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"));

        try
        {
            File.WriteAllBytes(probePath, payload);

            if (!File.ReadAllBytes(probePath).AsSpan().SequenceEqual(payload))
            {
                return false;
            }

            // Deleting is part of the test: a folder that accepts writes but
            // never releases them would collect one probe per validation pass.
            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            TryDelete(probePath);
            return false;
        }
    }

    /// <summary>Best-effort cleanup of a probe file; failure to delete is already reported elsewhere.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            // Nothing left to do. The probe is inert and a later pass will
            // overwrite a folder that accepts writes anyway.
        }
    }

    private static bool IsFileSystemFailure(Exception ex) => ex is
        IOException or
        UnauthorizedAccessException or
        SecurityException or
        ArgumentException or
        NotSupportedException;

    /// <summary>
    /// Whether an issue makes a path unusable, as opposed to merely worth
    /// mentioning.
    /// </summary>
    private static bool IsBlocking(PathIssue issue) => issue is
        PathIssue.NotAbsolute or
        PathIssue.CannotCreate or
        PathIssue.NotWritable or
        PathIssue.OverlapsRealDesktop or
        PathIssue.ProtectedLocation;

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="ancestor"/> or
    /// sits inside it.
    /// </summary>
    private static bool IsSameOrInside(string candidate, string ancestor)
    {
        string inside = Canonical(candidate);
        string container = Canonical(ancestor);

        if (inside.Length == 0 || container.Length == 0)
        {
            return false;
        }

        if (string.Equals(inside, container, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The trailing separator is what stops C:\Users\me\Desktop2 from
        // counting as being inside C:\Users\me\Desktop.
        string prefix = container.EndsWith(Path.DirectorySeparatorChar)
            ? container
            : container + Path.DirectorySeparatorChar;

        return inside.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reduces a path to a form two spellings of the same folder share, so
    /// string comparison can be trusted.
    /// </summary>
    /// <remarks>
    /// A <c>\\?\</c> path names the same folder as its plain form but compares
    /// as a different string, which would let an overlap slip past.
    /// Comparison stays ordinal-ignore-case because Windows paths are
    /// case-insensitive; the volume manager, not the file system, decides what
    /// a path means, so there is nothing to resolve against.
    /// </remarks>
    private static string Canonical(string path)
    {
        string value = path.Trim();

        if (value.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            value = @"\\" + value[8..];
        }
        else if (value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                 value.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        return Path.TrimEndingDirectorySeparator(value);
    }

    /// <summary>
    /// What was made of a candidate path: the path to report, the path to act
    /// on, and everything found wrong with it.
    /// </summary>
    /// <param name="Normalized">
    /// The expanded path handed back in <see cref="PathValidation.NormalizedPath"/>.
    /// Fully qualified whenever the input was; otherwise the expanded input, so
    /// the caller can echo what was rejected. Null only when the input held
    /// nothing usable.
    /// </param>
    /// <param name="FullPath">The path to create, or null when the candidate never became absolute.</param>
    /// <param name="Issues">Findings so far, appended to as checks run.</param>
    private readonly record struct Candidate(string? Normalized, string? FullPath, List<PathIssue> Issues);
}
