using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Workflows;

/// <summary>What the caller wants prepared.</summary>
/// <param name="MeetingDesktopDirectory">The empty folder to point the desktop at.</param>
/// <param name="CapturePreview">Whether to snapshot the desktop for the Restore page.</param>
public sealed record PrepareRequest(
    string MeetingDesktopDirectory,
    bool CapturePreview = true,
    string? MeetingWallpaperPath = null,
    DesktopSwitchMode SwitchMode = DesktopSwitchMode.RestartExplorer,
    int AutomaticSnapshotRetentionDays = 0);

/// <summary>What happened.</summary>
/// <param name="ShortcutsMoved">The broker's reported count; zero when no report is available.</param>
public sealed record PrepareResult(
    string OriginalDesktopPath,
    string MeetingDesktopPath,
    int IconCount,
    string? AutomaticSnapshotPath,
    string? PreviewPath,
    int ShortcutsMoved)
{
    /// <summary>The optional image safety copy failed; restoration still uses the original path if it survives.</summary>
    public bool WallpaperBackupUnavailable { get; init; }
}

/// <summary>Saves recovery state, parks public shortcuts, and switches the desktop folder.</summary>
/// <remarks>
/// Save the icon layout and session before requesting elevation. Desktop-path and
/// wallpaper changes start only after elevation succeeds. Later failures attempt
/// rollback; an incomplete rollback keeps the recovery record for retry.
/// </remarks>
public sealed class PrepareWorkflow
{
    private readonly AppPaths _paths;
    private readonly LayoutStore _layouts;
    private readonly SessionStore _sessions;
    private readonly DesktopLocationService _locations;
    private readonly IElevationBroker _elevation;
    private readonly IDesktopOperations _desktop;

    public PrepareWorkflow(
        AppPaths paths,
        LayoutStore layouts,
        SessionStore sessions,
        DesktopLocationService locations,
        IElevationBroker elevation)
        : this(paths, layouts, sessions, locations, elevation, new DesktopOperations())
    {
    }

    /// <summary>Test seam. See <see cref="IDesktopOperations"/>.</summary>
    internal PrepareWorkflow(
        AppPaths paths,
        LayoutStore layouts,
        SessionStore sessions,
        DesktopLocationService locations,
        IElevationBroker elevation,
        IDesktopOperations desktop)
    {
        _paths = paths;
        _layouts = layouts;
        _sessions = sessions;
        _locations = locations;
        _elevation = elevation;
        _desktop = desktop;
    }

    public async Task<PrepareResult> RunAsync(
        PrepareRequest request,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? beforeDesktopChange = null,
        Action? desktopReady = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_sessions.Exists)
            throw new InvalidOperationException("A recovery record already exists. Restore it before preparing another desktop.");

        // ---- 1. Where is the real desktop? (read-only) -------------------
        progress?.Report(new ProgressReport(ProgressKey.DetectingDesktopPath));

        DesktopLocation location = _locations.Detect(request.MeetingDesktopDirectory);

        if (!location.IsUsable || string.IsNullOrWhiteSpace(location.ResolvedPath))
        {
            throw new InvalidOperationException(
                $"The desktop folder could not be determined (status {location.Status}). " +
                "See the Diagnostics page; the sources disagree and this app will not guess.");
        }

        string originalDesktop = location.ResolvedPath;

        // ---- 2. Read the current icon layout (read-only) -----------------
        // Abort before touching anything if this fails. A swap without a
        // layout snapshot is not reversible.
        progress?.Report(new ProgressReport(ProgressKey.ReadingCurrentLayout));

        var iconsTask = Task.Run(
            () => request.SwitchMode == DesktopSwitchMode.Live
                ? _desktop.ReadShellIcons(cancellationToken)
                : _desktop.ReadIcons(cancellationToken),
            cancellationToken);
        // Both reads belong to the same pre-change desktop. Join them even on
        // failure so a preview writer cannot outlive this attempt and race a retry.
        Task<string?> previewTask = request.CapturePreview
            ? Task.Run(() => _desktop.CapturePreview(
                Path.Combine(_paths.StateDirectory, "return-preview.png")), cancellationToken)
            : Task.FromResult<string?>(null);
        await Task.WhenAll(iconsTask, previewTask).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<IconPosition> icons = await iconsTask.ConfigureAwait(true);
        string? previewPath = await previewTask.ConfigureAwait(true);

        if (icons.Count == 0)
        {
            // An empty desktop is legal but worth recording; the restore path
            // will simply have nothing to put back.
            progress?.Report(new ProgressReport(ProgressKey.ReadingCurrentLayout, new object[] { 0 }));
        }

        // ---- 3. Persist the layout (app data only) -----------------------
        progress?.Report(new ProgressReport(ProgressKey.WritingLayoutSnapshot));

        var saved = await Task.Run(() =>
        {
            MonitorTopology topology = MonitorTopologyReader.Capture();
            var snapshot = new LayoutSnapshot
            {
                CreatedUtc = DateTimeOffset.UtcNow,
                IsAutomatic = true,
                Topology = topology,
                Icons = icons,
            };
            return (Topology: topology, Path: _layouts.SaveAutomaticIfChanged(snapshot, originalDesktop, DateTimeOffset.Now));
        }, cancellationToken).ConfigureAwait(true);
        MonitorTopology topology = saved.Topology;
        string snapshotPath = saved.Path;

        // ---- 4b. Wallpaper: read it, decide, do not touch it yet ---------
        // Read before anything is applied, so the session record can hold the
        // state to go back to. A meeting wallpaper is only used when the user
        // configured one - the default is to leave theirs alone, and a user who
        // set nothing should not have their background changed and then changed
        // back.
        //
        // Applying it here would be wrong. See the note on step 7a.
        var wallpaper = await Task.Run(() => (_desktop.ReadWallpaper(), ResolveMeetingWallpaper(request.MeetingWallpaperPath)),
            cancellationToken).ConfigureAwait(true);
        WallpaperState wallpaperBefore = wallpaper.Item1;
        WallpaperState? meetingWallpaper = wallpaper.Item2;

        // A configured wallpaper that is already in place is left alone.
        // Re-applying it repaints the desktop for no change, and restore makes
        // the same judgement on the way back for the same reason.
        bool willChangeWallpaper =
            meetingWallpaper is not null && !wallpaperBefore.Matches(meetingWallpaper);
        string? wallpaperBackupPath = willChangeWallpaper
            ? await WallpaperRecoverySnapshot.CaptureAsync(_paths, wallpaperBefore, cancellationToken).ConfigureAwait(true)
            : null;

        // ---- 5. Record the session BEFORE changing anything --------------
        // The wallpaper flag is a prediction, written here rather than after
        // the apply so the record cannot end up describing the opposite of
        // what happened. Predicting "replaced" and being wrong costs one
        // redundant repaint on restore; predicting "untouched" and being wrong
        // costs the user their wallpaper with nothing left that knows how to
        // put it back.
        await Task.Run(() => _sessions.Save(new SessionInfo
        {
            OriginalDesktopPath = originalDesktop,
            SwitchMode = request.SwitchMode,
            MeetingDesktopPathAtSwap = request.MeetingDesktopDirectory,
            SwappedUtc = DateTimeOffset.UtcNow,
            AutoLayoutSnapshotPath = snapshotPath,
            CapturedIconCount = icons.Count,
            HasReturnPreview = previewPath is not null,
            TopologyAtSwap = topology,
            WallpaperAtSwap = wallpaperBefore,
            WallpaperBackupPath = wallpaperBackupPath,
            WallpaperChanged = willChangeWallpaper,
        }), cancellationToken).ConfigureAwait(true);

        // ---- 6. Move the public shortcuts aside (the only elevated step) --
        // Deliberately before the registry write: the elevated step is the one
        // thing Windows will not let this process do for itself, so it is the
        // last thing that can fail without leaving the machine half-swapped.
        bool elevationStarted = false;
        ElevationOutcome outcome;
        try
        {
            progress?.Report(new ProgressReport(ProgressKey.MovingPublicShortcuts));
            outcome = await Task.Run(() =>
            {
                elevationStarted = true;
                return _elevation.Run(ElevatedOperations.MoveShortcutsAside);
            }, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            // A queued operation cancelled before execution cannot have moved
            // shortcuts. Once the broker starts, an uncertain result needs recovery.
            if (!elevationStarted) _sessions.Clear();
            throw;
        }

        if (outcome != ElevationOutcome.Succeeded)
        {
            // A declined prompt is a no-op. A failed elevated move may already
            // have parked some shortcuts, so its recovery record must survive.
            if (outcome == ElevationOutcome.Declined) _sessions.Clear();

            throw new ElevationDeclinedException(
                outcome == ElevationOutcome.Declined
                    ? "The administrator prompt was dismissed, so nothing was changed."
                    : "The elevated step failed and may have moved some shortcuts. The recovery record was kept.",
                wasDeclined: outcome == ElevationOutcome.Declined);
        }

        // ---- 7. The point of no return ----------------------------------
        // The public shortcuts are parked from here, so a failure in any of
        // the steps below leaves a desktop missing them with no route back.
        // The catch unwinds what it can and rethrows; without it the user is
        // left to recover by hand, which means a second UAC prompt at the
        // worst possible moment - standing in front of a projector.
        try
        {
            if (beforeDesktopChange is not null)
                await beforeDesktopChange(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            // 7a. Apply the wallpaper.
            //
            // Below the elevation guard, and that placement is the whole point.
            // Applying it earlier - where it reads more naturally, next to the
            // read - broke the invariant this workflow is built on: everything
            // up to and including the UAC prompt has to leave the machine
            // untouched, so that a dismissed prompt is a genuine no-op. A
            // wallpaper applied before the prompt survived the dismissal, and
            // the message said "nothing was changed".
            if (willChangeWallpaper)
            {
                progress?.Report(new ProgressReport(ProgressKey.ApplyingWallpaper));
                // A false result can follow a partial style or tile write.
                // Keep the prewritten recovery flag even when apply fails.
                _desktop.ApplyWallpaper(meetingWallpaper!);
            }

            // 7b. Redirect the desktop.
            progress?.Report(new ProgressReport(ProgressKey.RedirectingDesktop));

            Directory.CreateDirectory(request.MeetingDesktopDirectory);
            if (request.SwitchMode == DesktopSwitchMode.Live)
            {
                progress?.Report(new ProgressReport(ProgressKey.RefreshingDesktop));
                await Task.Run(() => _desktop.SwitchLiveDesktop(
                    request.MeetingDesktopDirectory, cancellationToken), cancellationToken).ConfigureAwait(true);
            }
            else
            {
                _desktop.WriteDesktopPath(request.MeetingDesktopDirectory);

                // Capture the native blob while the layout is still the user's own.
                // It is a secondary record - coordinates only mean something
                // relative to the monitor arrangement - but writing it back lets
                // explorer do most of the work before the name-based pass
                // fine-tunes.
                byte[]? blob = _desktop.ReadIconLayoutBlob();
                if (blob is not null)
                {
                    _desktop.ExportIconLayoutBlob(
                        blob, Path.Combine(_paths.RecoveryDirectory, "IconLayouts.bin"));
                }

                // 7c. Restart explorer so it picks the change up.
                progress?.Report(new ProgressReport(ProgressKey.RestartingExplorer));

                await Task.Run(_desktop.RestartExplorer, cancellationToken).ConfigureAwait(true);
                await Task.Run(() => _desktop.WaitForCompatibleDesktop(
                    request.MeetingDesktopDirectory, null, cancellationToken), cancellationToken).ConfigureAwait(true);
            }

            // Task.Run cannot cancel a delegate that has already started. If
            // cancellation arrived during the switch, unwind it before returning.
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            await RollBackAsync(
                originalDesktop,
                willChangeWallpaper ? wallpaperBefore : null,
                wallpaperBackupPath,
                request.SwitchMode,
                icons).ConfigureAwait(true);

            throw;
        }

        DesktopReadyNotification.Send(desktopReady);

        // Cleanup is housekeeping, never a reason to roll back a verified switch.
        try
        {
            await Task.Run(() => _layouts.PruneAutomatic(request.AutomaticSnapshotRetentionDays,
                DateTimeOffset.UtcNow, [snapshotPath])).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }

        progress?.Report(new ProgressReport(ProgressKey.Done, Fraction: 1));

        return new PrepareResult(
            originalDesktop,
            request.MeetingDesktopDirectory,
            icons.Count,
            snapshotPath,
            previewPath,
            _elevation.LastReport?.Result.MovedCount ?? 0)
        {
            WallpaperBackupUnavailable = willChangeWallpaper && !wallpaperBefore.IsEmpty && wallpaperBackupPath is null,
        };
    }

    /// <summary>Uses the configured image with fill enabled, or leaves the wallpaper unchanged.</summary>
    private static WallpaperState? ResolveMeetingWallpaper(string? configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath) || !File.Exists(configuredPath)
            ? null
            : new WallpaperState(configuredPath, "10", "0");

    /// <summary>Attempts every recovery step after a failed switch without masking its original error.</summary>
    /// <remarks>
    /// Cleanup ignores the original cancellation. Shortcut, layout, and wallpaper
    /// recovery are independent; clear the record only when every step succeeds.
    /// </remarks>
    private async Task RollBackAsync(
        string originalDesktop,
        WallpaperState? wallpaperToRestore,
        string? wallpaperBackupPath,
        DesktopSwitchMode mode,
        IReadOnlyList<IconPosition> icons)
    {
        // Cleanup must still run when the original operation was cancelled.
        var cancellationToken = CancellationToken.None;
        bool complete = true;
        try
        {
            var shortcuts = await Task.Run(() => _elevation.Run(ElevatedOperations.RestoreShortcuts))
                .ConfigureAwait(true);
            complete = shortcuts == ElevationOutcome.Succeeded;
        }
        catch (Exception) { complete = false; }

        try
        {
            if (mode == DesktopSwitchMode.Live)
            {
                await Task.Run(() => _desktop.SwitchLiveDesktop(originalDesktop, cancellationToken))
                    .ConfigureAwait(true);
                var placement = await Task.Run(() => _desktop.PlaceShellIcons(icons, cancellationToken))
                    .ConfigureAwait(true);
                complete &= placement.IsExact;
            }
            else
            {
                _desktop.WriteDesktopPath(originalDesktop);
                // A surviving shell may already be bound to the meeting folder.
                // Rewriting the registry alone cannot prove that it switched back.
                try
                {
                    if (_desktop.IsExplorerRunning())
                        await Task.Run(_desktop.StopExplorer, cancellationToken).ConfigureAwait(true);
                }
                finally
                {
                    await Task.Run(_desktop.StartExplorer, cancellationToken).ConfigureAwait(true);
                }
                await Task.Run(() => _desktop.WaitForCompatibleDesktop(originalDesktop, icons, cancellationToken))
                    .ConfigureAwait(true);
                var placement = await Task.Run(() => _desktop.PlaceIcons(icons, cancellationToken)).ConfigureAwait(true);
                complete &= placement.IsExact;
            }
        }
        catch (Exception)
        {
            // Leave the session file behind as the record of an unfinished
            // unwind.
            complete = false;
        }

        // Wallpaper recovery is independent of shortcut and layout recovery.
        // Any failure must retain the record, including an independent cancellation.
        if (wallpaperToRestore is not null)
        {
            try { complete &= _desktop.ApplyWallpaper(WallpaperRecoverySnapshot.Resolve(_paths, wallpaperToRestore, wallpaperBackupPath)); }
            catch (Exception) { complete = false; }
        }

        if (complete) _sessions.Clear();
    }
}

/// <summary>The user dismissed the administrator prompt, or the elevated step failed.</summary>
public sealed class ElevationDeclinedException : Exception
{
    public bool WasDeclined { get; }
    public ElevationDeclinedException(string message, bool wasDeclined = true) : base(message)
    {
        WasDeclined = wasDeclined;
    }
}
