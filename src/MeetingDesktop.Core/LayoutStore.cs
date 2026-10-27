using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core;

/// <summary>
/// Reads and writes <see cref="LayoutSnapshot"/> files: one JSON file per
/// snapshot, in a caller-supplied folder.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot is only meaningful together with the monitor topology it was
/// taken under. Icon coordinates are <em>virtual desktop</em> coordinates, and
/// their origin moves when a display is added or the arrangement changes - so
/// positions saved under one arrangement describe somewhere else under
/// another. That is why <see cref="LayoutSnapshot.Topology"/> travels in the
/// same file as <see cref="LayoutSnapshot.Icons"/>: restore can compare the two
/// topologies and tell the user the layout was captured on a different display
/// setup, instead of silently placing icons at coordinates that no longer mean
/// what they meant when they were saved.
/// </para>
/// <para>
/// Files are written to a temporary sibling and then moved into place, so an
/// interrupted write leaves the previous snapshot intact. The automatic
/// pre-swap snapshot is frequently the only record of a layout the user never
/// saved by hand; truncating it during a write is the exact failure this
/// application exists to prevent.
/// </para>
/// </remarks>
public sealed class LayoutStore
{
    /// <summary>Marks the snapshots taken automatically before a desktop swap.</summary>
    private const string AutomaticPrefix = "auto-";

    private const string Extension = ".json";

    /// <summary>Temporary files carry this extension so <see cref="List"/> never reports one as a snapshot.</summary>
    private const string TemporaryExtension = ".tmp";

    /// <summary>
    /// Windows rejects these as file names even with an extension attached
    /// (<c>CON.json</c> resolves to the console device, not a file), so a
    /// layout the user names after one would fail to save.
    /// </summary>
    private static readonly HashSet<string> ReservedFileStems =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private readonly object _automaticSync = new();

    /// <summary>
    /// Points the store at a folder, creating it if it does not exist.
    /// </summary>
    /// <param name="layoutsDirectory">Folder holding the snapshot files.</param>
    /// <exception cref="ArgumentException"><paramref name="layoutsDirectory"/> is null, empty, or whitespace.</exception>
    /// <exception cref="IOException">The folder could not be created.</exception>
    /// <remarks>
    /// The folder is created here rather than lazily on the first write so a
    /// broken path surfaces while the user is still on a settings screen. The
    /// alternative is discovering it mid-swap, after the desktop has already
    /// been redirected and the layout that needed saving is the live one.
    /// </remarks>
    public LayoutStore(string layoutsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutsDirectory);

        _directory = Path.GetFullPath(layoutsDirectory);
        Directory.CreateDirectory(_directory);
    }

    /// <summary>
    /// The names of the snapshots currently on disk, sorted.
    /// </summary>
    /// <returns>
    /// File names without the <c>.json</c> extension, which is what
    /// <see cref="Load"/> and <see cref="Delete"/> accept. A name being listed
    /// is not a promise that it loads; a damaged file still appears here and
    /// <see cref="Load"/> returns null for it.
    /// </returns>
    /// <remarks>
    /// Sort order is ordinal, which places the automatic snapshots in
    /// chronological order for free: their timestamps are fixed-width and
    /// zero-padded, so string order and time order agree.
    /// </remarks>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_directory))
        {
            // The folder can be removed by a cleanup tool or a sync client
            // after construction. An empty list is the honest answer.
            return Array.Empty<string>();
        }

        var names = new List<string>();

        foreach (string file in Directory.EnumerateFiles(_directory))
        {
            if (string.Equals(Path.GetExtension(file), Extension, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// Loads one snapshot.
    /// </summary>
    /// <param name="name">A name from <see cref="List"/>, with or without the extension.</param>
    /// <returns>The snapshot, or null when the file is missing, unreadable, or damaged.</returns>
    /// <remarks>
    /// An exact logical name takes precedence; the extension form is tried
    /// only when that file is absent.
    ///
    /// Nothing here throws for bad input on disk. Callers always have a
    /// fallback - read the live desktop - and refusing to start because one
    /// stale file is truncated helps nobody. A file written by a newer build is
    /// treated the same way: the fields this build does not know would be
    /// dropped on the way back out, so restoring a lossy version of it is worse
    /// than admitting it cannot be read.
    /// </remarks>
    public LayoutSnapshot? Load(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string path = Resolve(name);

        try
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (FileNotFoundException) when (name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                json = File.ReadAllText(Resolve(name[..^Extension.Length]));
            }
            LayoutSnapshot? snapshot = JsonSerializer.Deserialize<LayoutSnapshot>(json, SerializerOptions);

            if (snapshot is null || snapshot.SchemaVersion < 1 || snapshot.SchemaVersion > LayoutSnapshot.CurrentSchemaVersion ||
                snapshot.Icons is null || snapshot.Icons.Any(icon => icon is null || string.IsNullOrWhiteSpace(icon.Name)) ||
                snapshot.Topology?.Monitors is null || snapshot.Topology.Monitors.Any(monitor => monitor is null))
            {
                return null;
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a snapshot under a user-chosen name, replacing any snapshot that
    /// already has it.
    /// </summary>
    /// <param name="snapshot">The snapshot to persist.</param>
    /// <param name="name">The name to save under; null falls back to the snapshot's own name.</param>
    /// <returns>The full path of the file that was written.</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <remarks>
    /// Replacing is the point of an explicit name: "save over my usual layout"
    /// is what the user asked for. The automatic snapshots deliberately do not
    /// behave this way - see <see cref="SaveAutomatic"/>.
    /// </remarks>
    public string Save(LayoutSnapshot snapshot, string? name)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string stem = LayoutSnapshot.SanitizeName(name ?? snapshot.Name);
        return Write(snapshot, stem, allowOverwrite: true);
    }

    /// <summary>
    /// Writes the pre-swap snapshot under a timestamped, collision-proof name.
    /// </summary>
    /// <param name="snapshot">The snapshot to persist.</param>
    /// <param name="stamp">The moment the snapshot was taken; also names the file.</param>
    /// <returns>The full path of the file that was written.</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <remarks>
    /// An existing file is never overwritten, not even one this method wrote
    /// itself. If the caller restores and swaps again within the same second,
    /// the second capture gets a numeric suffix rather than replacing the
    /// first: the first is the layout the user had before any of this started,
    /// and losing it is the failure the whole application is built around.
    /// </remarks>
    public string SaveAutomatic(LayoutSnapshot snapshot, DateTimeOffset stamp)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Local time, because these names are read by a person browsing the
        // layouts folder. Invariant culture because a non-Gregorian system
        // calendar would otherwise produce names that do not sort by date.
        string stampText = stamp.LocalDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Write(snapshot, AutomaticPrefix + stampText, allowOverwrite: false);
    }

    /// <summary>
    /// Compares the newest valid automatic capture for this source, reusing it
    /// only when icons and full topology match. Names, times and enumeration
    /// order do not affect equality. Reuse leaves the original file untouched.
    /// </summary>
    public string SaveAutomaticIfChanged(LayoutSnapshot snapshot, string desktopPath, DateTimeOffset stamp)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!AutomaticSnapshotPolicy.TryNormalizePath(desktopPath, out string source))
            throw new ArgumentException("An absolute source desktop path is required.", nameof(desktopPath));

        var capture = snapshot with { SourceDesktopPath = source, CreatedUtc = stamp, IsAutomatic = true };
        if (!AutomaticSnapshotPolicy.IsValid(capture))
            throw new ArgumentException("The capture must have a supported schema, timestamp, icons and complete monitor topology.", nameof(snapshot));

        lock (_automaticSync)
        {
            if (!AutomaticSnapshotPolicy.HasNoReparseAncestors(_directory))
                throw new IOException("The snapshot directory must exist and have no reparse-point ancestors.");
            string topology = AutomaticSnapshotPolicy.TopologyKey(capture);
            string icons = AutomaticSnapshotPolicy.IconKey(capture);
            var previous = NewestFirst(ReadAutomaticCaptures()
                .Where(c => StringComparer.OrdinalIgnoreCase.Equals(c.Source, source))).FirstOrDefault();
            if (previous is not null && previous.Topology == topology &&
                AutomaticSnapshotPolicy.IconKey(previous.Snapshot) == icons) return previous.Path;
            return SaveAutomatic(capture, stamp);
        }
    }

    /// <summary>
    /// Removes captures strictly older than 7, 15 or 30 days. Zero and unsupported
    /// values disable deletion. Keeps protected full paths and the newest valid
    /// capture per source/full topology, even when that capture is expired.
    /// Unproven, unreadable and reparse-point files are always left alone.
    /// </summary>
    public int PruneAutomatic(int retentionDays, DateTimeOffset now, IReadOnlyCollection<string> protectedPaths)
    {
        if (retentionDays is not (7 or 15 or 30)) return 0;
        ArgumentNullException.ThrowIfNull(protectedPaths);
        if (now.UtcTicks < TimeSpan.FromDays(retentionDays).Ticks) return 0;
        DateTimeOffset cutoff = now.ToUniversalTime().AddDays(-retentionDays);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in protectedPaths)
        {
            // An invalid protection request must never silently unprotect a file.
            if (!AutomaticSnapshotPolicy.TryNormalizePath(path, out string fullPath)) return 0;
            keep.Add(fullPath);
        }

        lock (_automaticSync)
        {
            var captures = ReadAutomaticCaptures();
            foreach (var group in captures.GroupBy(c => (c.Source.ToUpperInvariant(), c.Topology)))
                keep.Add(NewestFirst(group).First().Path);

            int removed = 0;
            foreach (var capture in captures)
            {
                if (capture.Snapshot.CreatedUtc >= cutoff || keep.Contains(capture.Path)) continue;
                // Recheck containment, reparse points and exact contents immediately
                // before removal; a changed/unreadable file is no longer our candidate.
                var current = ReadAutomaticCapture(capture.Path);
                if (current is null || current.Json != capture.Json) continue;
                try
                {
                    if (DeletePath(capture.Path) == LayoutDeleteResult.Deleted) removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Retention is best effort; failed deletion preserves the capture.
                }
            }
            return removed;
        }
    }

    private sealed record AutomaticCapture(string Path, LayoutSnapshot Snapshot, string Source, string Topology, string Json);

    private static IOrderedEnumerable<AutomaticCapture> NewestFirst(IEnumerable<AutomaticCapture> captures) =>
        captures.OrderByDescending(c => c.Snapshot.CreatedUtc)
            .ThenByDescending(c => CollisionSequence(c.Path)).ThenBy(c => c.Path, StringComparer.Ordinal);

    private static int CollisionSequence(string path)
    {
        // SaveAutomatic appends numeric suffixes for captures with identical
        // timestamps. Numeric order (including -10 and beyond) preserves recency.
        string stem = Path.GetFileNameWithoutExtension(path);
        if (stem.Length > 21 && stem[20] == '-' &&
            DateTime.TryParseExact(stem.Substring(5, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
            int.TryParse(stem.AsSpan(21), NumberStyles.None, CultureInfo.InvariantCulture, out int sequence) && sequence >= 2)
            return sequence;
        return 1;
    }

    private IReadOnlyList<AutomaticCapture> ReadAutomaticCaptures()
    {
        if (!AutomaticSnapshotPolicy.HasNoReparseAncestors(_directory)) return Array.Empty<AutomaticCapture>();
        try
        {
            // Materialize first: partial enumeration must not imply a complete history.
            string[] paths = Directory.GetFiles(_directory);
            var captures = new List<AutomaticCapture>();
            foreach (string path in paths)
            {
                var capture = ReadAutomaticCapture(path);
                if (capture is not null) captures.Add(capture);
            }
            return captures;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<AutomaticCapture>();
        }
    }

    private AutomaticCapture? ReadAutomaticCapture(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(_directory), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith(AutomaticPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase) ||
            !AutomaticSnapshotPolicy.HasNoReparseAncestors(_directory)) return null;
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
            string json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (!AutomaticSnapshotPolicy.HasCaptureFields(document.RootElement)) return null;
            var snapshot = JsonSerializer.Deserialize<LayoutSnapshot>(json, SerializerOptions);
            if (snapshot is null || !AutomaticSnapshotPolicy.IsValid(snapshot)) return null;
            AutomaticSnapshotPolicy.TryNormalizePath(snapshot.SourceDesktopPath, out string source);
            return new(path, snapshot, source, AutomaticSnapshotPolicy.TopologyKey(snapshot), json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes one snapshot.
    /// </summary>
    /// <param name="name">A name from <see cref="List"/>.</param>
    /// <returns>True when a file was removed; false when there was nothing to remove
    /// or the file could not be deleted.</returns>
    public bool Delete(string name) => DeleteWithReason(name) == LayoutDeleteResult.Deleted;

    public LayoutDeleteResult DeleteWithReason(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return DeletePath(Resolve(name));
    }

    private LayoutDeleteResult DeletePath(string path)
    {
        string sessionPath = Path.Combine(Directory.GetParent(_directory)!.FullName, "State", "session.json");
        lock (SessionStore.SnapshotSync)
        {
            try
            {
                // Keep an existing record open without write/delete sharing until deletion completes.
                // Save uses the same lock, including when no record exists yet.
                FileStream? record = null;
                try { record = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                using (record)
                {
                    if (record is not null)
                    {
                        using var bytes = new MemoryStream();
                        record.CopyTo(bytes);
                        var session = SessionStore.ReadRecord(bytes.ToArray());
                        if (session is null || session.SchemaVersion < 1 ||
                            string.IsNullOrWhiteSpace(session.OriginalDesktopPath) ||
                            string.IsNullOrWhiteSpace(session.MeetingDesktopPathAtSwap) ||
                            session.CapturedIconCount < 0)
                            return LayoutDeleteResult.SessionUnreadable;
                        string? reference = session.AutoLayoutSnapshotPath;
                        if (string.IsNullOrWhiteSpace(reference))
                        {
                            if (session.CapturedIconCount != 0) return LayoutDeleteResult.SessionUnreadable;
                        }
                        else
                        {
                            if (!Path.IsPathFullyQualified(reference)) return LayoutDeleteResult.SessionUnreadable;
                            if (string.Equals(Path.GetFullPath(reference), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                                return LayoutDeleteResult.SessionReferenced;
                        }
                    }
                    if (!File.Exists(path)) return LayoutDeleteResult.Failed;
                    File.Delete(path);
                    return LayoutDeleteResult.Deleted;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return LayoutDeleteResult.SessionUnreadable;
            }
        }
    }

    /// <summary>
    /// Turns a snapshot name into a path inside the layouts folder.
    /// </summary>
    /// <remarks>
    /// Everything that becomes a file name goes through
    /// <see cref="LayoutSnapshot.SanitizeName"/>, which strips separators and
    /// characters Windows rejects, so a name can never resolve outside the
    /// folder. Applying it in one place keeps save, load and delete agreeing on
    /// what a given name refers to.
    /// </remarks>
    private string Resolve(string name)
    {
        string stem = LayoutSnapshot.SanitizeName(name);

        if (ReservedFileStems.Contains(stem))
        {
            // Escaped rather than rejected, and identically on the way back in,
            // so a layout called "NUL" saves and loads like any other.
            stem = "_" + stem;
        }

        return Path.Combine(_directory, stem + Extension);
    }

    private string Write(LayoutSnapshot snapshot, string stem, bool allowOverwrite)
    {
        string path = Resolve(stem);

        if (!allowOverwrite)
        {
            path = NextFreePath(path);
        }

        WriteAtomically(path, JsonSerializer.Serialize(snapshot, SerializerOptions));
        return path;
    }

    /// <summary>
    /// Appends a numeric suffix until an unused path is found.
    /// </summary>
    /// <remarks>
    /// The suffix goes after the timestamp, so the colliding names still sort
    /// in capture order.
    /// </remarks>
    private static string NextFreePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(path);

        for (int suffix = 2; suffix < int.MaxValue; suffix++)
        {
            string candidate = Path.Combine(
                directory,
                stem + "-" + suffix.ToString(CultureInfo.InvariantCulture) + Extension);

            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"No free snapshot file name could be derived from '{path}'.");
    }

    private static void WriteAtomically(string path, string json)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + TemporaryExtension;

        try
        {
            File.WriteAllText(temporary, json);

            if (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Discard(temporary);
            throw;
        }
    }

    private static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. A leftover temporary file is invisible to List and
            // is overwritten by nothing, so it cannot be mistaken for a
            // snapshot; failing the write over it would be worse.
        }
    }
}

public enum LayoutDeleteResult { Deleted, SessionReferenced, SessionUnreadable, Failed }
