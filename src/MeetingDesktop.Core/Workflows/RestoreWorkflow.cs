using System.Text.Json;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Workflows;

/// <summary>What happened during a restore.</summary>
/// <param name="ShortcutsRestored">The broker's reported count; zero when no report is available.</param>
public sealed record RestoreResult(
    string RestoredDesktopPath,
    int ShortcutsRestored,
    PlaceResult? Layout,
    bool TopologyChanged,
    IReadOnlyList<string> FilesLeftOnMeetingDesktop)
{
    /// <summary>Captured icon names omitted because their identities cannot be proven.</summary>
    public IReadOnlyList<string> UnmappedIcons { get; init; } = [];
    public bool WallpaperRestored { get; init; } = true;
    public bool LeftoverScanComplete { get; init; } = true;
}

/// <summary>The desktop returned, but layout or wallpaper recovery needs attention; the session is retained.</summary>
public sealed class RestoreIncompleteException : Exception
{
    public RestoreResult Result { get; }

    public RestoreIncompleteException(RestoreResult result, Exception? innerException = null)
        : base(result.WallpaperRestored
            ? "Some icons could not be restored exactly. The recovery record was kept; retry restore after checking the layout."
            : "The wallpaper could not be restored. The recovery record was kept; retry restore after checking the wallpaper.", innerException)
    {
        Result = result;
    }
}

/// <summary>Raised when there is nothing to restore, or the record is unusable.</summary>
public sealed class RestoreNotPossibleException : Exception
{
    public RestoreNotPossibleException(string message) : base(message)
    {
    }
}

/// <summary>
/// Puts the desktop back, and puts every icon back with it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The elevated step comes before any state change.</b>
/// </para>
/// <para>
/// The shared legacy native blob has no session ownership metadata. It is
/// deliberately ignored, even when a coordinate snapshot exists: that snapshot
/// does not prove that the blob was captured for the same session.
/// </para>
/// <para>
/// As in prepare, a declined UAC prompt must leave the machine exactly as it was.
/// </para>
/// </remarks>
public sealed class RestoreWorkflow
{
    private readonly AppPaths _paths;
    private readonly LayoutStore _layouts;
    private readonly SessionStore _sessions;
    private readonly IElevationBroker _elevation;
    private readonly IDesktopOperations _desktop;
    private readonly Func<string, string[]> _readLeftovers;
    private readonly string _publicShortcutDirectory;
    private readonly string _shortcutBackupDirectory;

    public RestoreWorkflow(
        AppPaths paths,
        LayoutStore layouts,
        SessionStore sessions,
        IElevationBroker elevation)
        : this(paths, layouts, sessions, elevation, new DesktopOperations())
    {
    }

    /// <summary>Test seam. See <see cref="IDesktopOperations"/>.</summary>
    internal RestoreWorkflow(
        AppPaths paths,
        LayoutStore layouts,
        SessionStore sessions,
        IElevationBroker elevation,
        IDesktopOperations desktop,
        Func<string, string[]>? readLeftovers = null,
        string? publicShortcutDirectory = null,
        string? shortcutBackupDirectory = null)
    {
        _paths = paths;
        _layouts = layouts;
        _sessions = sessions;
        _elevation = elevation;
        _desktop = desktop;
        _readLeftovers = readLeftovers ?? ReadLeftovers;
        _publicShortcutDirectory = publicShortcutDirectory ?? PublicDesktopService.PublicDesktopDirectory;
        _shortcutBackupDirectory = shortcutBackupDirectory ?? PublicDesktopService.BackupDirectory;
    }

    public async Task<RestoreResult> RunAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? beforeDesktopChange = null,
        Action? desktopReady = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // ---- 1. What are we undoing? -------------------------------------
        SessionInfo? session = _sessions.Load();

        if (session is null)
        {
            throw new RestoreNotPossibleException(
                "There is no record of a desktop swap, so there is nothing to restore. " +
                "If the desktop was swapped by an older version of this tool, adopt it first.");
        }

        if (!Directory.Exists(session.OriginalDesktopPath))
        {
            // Pointing the desktop at a path that does not exist makes the
            // user's files appear to have vanished. Refuse rather than guess.
            throw new RestoreNotPossibleException(
                $"The recorded desktop folder no longer exists: {session.OriginalDesktopPath}. " +
                "Restoring would point the desktop at a missing folder.");
        }

        // Validate the recovery asset before any elevated operation or path change.
        LayoutSnapshot? savedLayout = await Task.Run(() => LoadSavedLayout(session), cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();

        // Reject malformed persisted mappings before another elevated operation.
        _ = PublicShortcutRecovery.Merge(session.RestoredShortcutConflicts, [], _shortcutBackupDirectory, _publicShortcutDirectory);
        if (session.ShortcutRestoreUncertain)
            throw new RestoreNotPossibleException("A shortcut restore has no authenticated report. Review the recovery record before retrying.");

        // ---- 2. Bring the public shortcuts back (the elevated step) ------
        progress?.Report(new ProgressReport(ProgressKey.RestoringPublicShortcuts));

        // Persist uncertainty before the helper can move anything. A crash or
        // transport failure must not erase the possibility of an unseen collision.
        bool wasUncertain = session.ShortcutRestoreUncertain;
        var previousReport = _elevation.LastReport;
        // Older releases do not understand the pending-operation or conflict
        // fields and must not silently clear this recovery record after a downgrade.
        session = session with { SchemaVersion = SessionInfo.CurrentSchemaVersion, ShortcutRestoreUncertain = true };
        _sessions.Save(session);
        bool delegateStarted = false;
        ElevationRunResult run;
        try
        {
            run = await Task.Run(() =>
            {
                delegateStarted = true;
                return _elevation.RunWithEvidence(ElevatedOperations.RestoreShortcuts);
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!delegateStarted)
        {
            _sessions.Save(session with { ShortcutRestoreUncertain = wasUncertain });
            throw;
        }
        ElevationOutcome outcome = run.Outcome;
        if (run.DefinitelyNotDispatched)
        {
            _sessions.Save(session with { ShortcutRestoreUncertain = wasUncertain });
            throw new RestoreNotPossibleException("The elevated command was not dispatched. Authorize and retry restore.");
        }
        var report = _elevation.LastReport;
        // An unchanged report belongs to an earlier broker operation.
        if (ReferenceEquals(report, previousReport)) report = null;
        if (outcome == ElevationOutcome.Declined)
        {
            session = session with { ShortcutRestoreUncertain = wasUncertain };
            _sessions.Save(session);
            throw new RestoreNotPossibleException("The administrator prompt was dismissed, so nothing was changed.");
        }
        if (report is not null && PublicShortcutRecovery.ValidReport(report, outcome, _shortcutBackupDirectory))
        {
            var conflicts = PublicShortcutRecovery.Merge(session.RestoredShortcutConflicts, report.Result.Conflicts,
                _shortcutBackupDirectory, _publicShortcutDirectory);
            session = session with { RestoredShortcutConflicts = conflicts.Count == 0 ? null : conflicts,
                ShortcutRestoreUncertain = wasUncertain };
            _sessions.Save(session);
        }
        if (outcome != ElevationOutcome.Succeeded)
            throw new RestoreNotPossibleException("The elevated step failed and may have restored some shortcuts. The recovery record was kept.");
        if (session.ShortcutRestoreUncertain)
            throw new RestoreNotPossibleException("A shortcut restore has no authenticated report. Review the recovery record before retrying.");
        var mapped = PublicShortcutRecovery.Map(savedLayout,
            PublicShortcutRecovery.Merge(session.RestoredShortcutConflicts, [], _shortcutBackupDirectory, _publicShortcutDirectory));
        savedLayout = mapped.Layout;

        // ---- 3. Point the desktop back -----------------------------------
        if (beforeDesktopChange is not null)
            await beforeDesktopChange(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new ProgressReport(ProgressKey.RedirectingDesktop));

        if (session.SwitchMode == DesktopSwitchMode.Live)
        {
            progress?.Report(new ProgressReport(ProgressKey.RefreshingDesktop));
            await Task.Run(() => _desktop.SwitchLiveDesktop(session.OriginalDesktopPath, cancellationToken),
                cancellationToken).ConfigureAwait(true);
        }
        else
        {
            _desktop.WriteDesktopPath(session.OriginalDesktopPath);

            // ---- 4. Reload the desktop binding ------------------------------
            progress?.Report(new ProgressReport(ProgressKey.RestartingExplorer));

            try
            {
                await Task.Run(_desktop.StopExplorer, cancellationToken).ConfigureAwait(true);
            }
            finally
            {
                // Even a stop failure or cancellation must bring the shell back.
                await Task.Run(_desktop.StartExplorer, CancellationToken.None).ConfigureAwait(true);
            }
            await Task.Run(() => _desktop.WaitForCompatibleDesktop(
                session.OriginalDesktopPath, savedLayout?.Icons, cancellationToken), cancellationToken).ConfigureAwait(true);
        }

        // ---- 5. Put the icons back by name -------------------------------
        // The session's saved coordinate snapshot is the recovery mechanism.
        progress?.Report(new ProgressReport(ProgressKey.ApplyingLayout));

        PlaceResult? placement = null;
        bool topologyChanged = false;

        if (savedLayout is { } snapshot)
        {
            if (snapshot.Icons.Count > 0)
            {
                topologyChanged = !snapshot.Topology.Matches(MonitorTopologyReader.Capture());

                placement = await Task.Run(
                    () => session.SwitchMode == DesktopSwitchMode.Live
                        ? _desktop.PlaceShellIcons(snapshot.Icons, cancellationToken)
                        : _desktop.PlaceIcons(snapshot.Icons, cancellationToken),
                    cancellationToken).ConfigureAwait(true);

                progress?.Report(new ProgressReport(ProgressKey.VerifyingLayout, Fraction: 1));
            }
        }

        if (mapped.Unmapped.Count > 0)
        {
            placement ??= new PlaceResult(0, 0, [], [], false, true);
            placement = placement with { Requested = placement.Requested + mapped.Unmapped.Count,
                NotFound = placement.NotFound.Concat(mapped.Unmapped).ToArray() };
        }

        // ---- 6b. Put the wallpaper back -----------------------------------
        // Only when the swap actually replaced it. Re-applying an identical
        // wallpaper would flash the desktop at the exact moment the user is
        // getting ready to present, and they would have no idea why.
        bool wallpaperRestored = !session.WallpaperChanged;
        Exception? wallpaperError = null;
        if (session.WallpaperChanged && session.WallpaperAtSwap is { } previous)
        {
            progress?.Report(new ProgressReport(ProgressKey.RestoringWallpaper));
            try
            {
                // Complete recovery even if cancellation arrived after the path
                // changed, while leaving the caller free to dispatch input.
                wallpaperRestored = await Task.Run(() => _desktop.ApplyWallpaper(
                    WallpaperRecoverySnapshot.Resolve(_paths, previous, session.WallpaperBackupPath))).ConfigureAwait(true);
            }
            catch (Exception ex) { wallpaperError = ex; }
        }

        if (placement is not { IsExact: false } && wallpaperRestored)
            DesktopReadyNotification.Send(desktopReady);

        // Report leftovers after revealing the verified desktop. Keep this work
        // awaited so a following switch cannot race the current recovery record.
        string[] leftovers = [];
        bool leftoverScanComplete = true;
        try { leftovers = await Task.Run(() => _readLeftovers(session.MeetingDesktopPathAtSwap)).ConfigureAwait(true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { leftoverScanComplete = false; }

        var result = new RestoreResult(
            session.OriginalDesktopPath,
            report?.Result.MovedCount ?? 0,
            placement,
            topologyChanged,
            leftovers)
        {
            WallpaperRestored = wallpaperRestored,
            UnmappedIcons = mapped.Unmapped,
            LeftoverScanComplete = leftoverScanComplete,
        };

        // Preserve useful partial results and finish wallpaper recovery before
        // reporting an incomplete restore. Keep the session available for retry.
        if (placement is { IsExact: false } || !wallpaperRestored)
            throw new RestoreIncompleteException(result, wallpaperError);

        cancellationToken.ThrowIfCancellationRequested();

        // ---- 7. Only now is the swap fully undone ------------------------
        _sessions.Clear();

        progress?.Report(new ProgressReport(ProgressKey.Done, Fraction: 1));

        return result;
    }

    private static string[] ReadLeftovers(string directory) => Directory.EnumerateFileSystemEntries(directory)
        .Select(Path.GetFileName)
        .Where(name => name is not null && !name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
        .Select(name => name!).ToArray();

    private LayoutSnapshot? LoadSavedLayout(SessionInfo session)
    {
        const string error = "The saved layout is missing, corrupt or incomplete. The recovery record was kept.";
        if (string.IsNullOrWhiteSpace(session.AutoLayoutSnapshotPath))
        {
            // Adopted legacy sessions have no coordinate capture to validate.
            if (session.CapturedIconCount == 0) return null;
            throw new RestoreNotPossibleException(error);
        }

        try
        {
            string path = Path.GetFullPath(session.AutoLayoutSnapshotPath);
            string stem = Path.GetFileNameWithoutExtension(path);
            string storedPath = Path.Combine(_paths.LayoutsDirectory, LayoutSnapshot.SanitizeName(stem) + ".json");
            if (!string.Equals(path, Path.GetFullPath(storedPath), StringComparison.OrdinalIgnoreCase))
                throw new RestoreNotPossibleException(error);

            // Missing constructor arguments deserialize to zero. Require actual
            // coordinates so an incomplete capture cannot manufacture positions.
            // Keep the file read-only until the store finishes loading the same
            // capture, so it cannot be replaced between validation and loading.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Topology", out var topology) || topology.ValueKind != JsonValueKind.Object ||
                !topology.TryGetProperty("Monitors", out var monitors) || monitors.ValueKind != JsonValueKind.Array ||
                !document.RootElement.TryGetProperty("Icons", out var icons) || icons.ValueKind != JsonValueKind.Array ||
                icons.EnumerateArray().Any(icon => icon.ValueKind != JsonValueKind.Object ||
                    !icon.TryGetProperty("X", out var x) || !x.TryGetInt32(out _) ||
                    !icon.TryGetProperty("Y", out var y) || !y.TryGetInt32(out _)))
                throw new RestoreNotPossibleException(error);

            LayoutSnapshot? snapshot = _layouts.Load(stem);
            if (snapshot is null || snapshot.SchemaVersion < 1 || snapshot.Icons is null ||
                snapshot.Icons.Count != session.CapturedIconCount ||
                snapshot.Icons.Any(icon => icon is null || string.IsNullOrWhiteSpace(icon.Name)) ||
                snapshot.Topology?.Monitors is null || snapshot.Topology.Monitors.Any(monitor => monitor is null))
                throw new RestoreNotPossibleException(error);

            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw new RestoreNotPossibleException(error);
        }
    }
}
