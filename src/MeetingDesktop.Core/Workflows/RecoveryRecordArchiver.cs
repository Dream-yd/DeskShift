using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Workflows;

public sealed record RecoveryRecordArchiveResult(string RecordPath, string CurrentLayoutPath, int IconCount);

/// <summary>
/// Retires an old record by explicit user choice, preserving the current layout
/// and the complete old record. Never writes desktop settings or positions.
/// </summary>
public sealed class RecoveryRecordArchiver
{
    private readonly AppPaths _paths;
    private readonly SessionStore _sessions;
    private readonly LayoutStore _layouts;
    private readonly DesktopLocationService _locations;
    private readonly Func<string, IReadOnlyList<IconPosition>> _readVerifiedLayout;
    private readonly Func<bool> _hasParkedFiles;
    private readonly Func<MonitorTopology> _captureTopology;
    private readonly Action? _beforeArchive;

    public RecoveryRecordArchiver(AppPaths paths, SessionStore sessions, LayoutStore layouts, DesktopLocationService locations)
        : this(paths, sessions, layouts, locations, ReadVerifiedLayout, HasParkedFiles, MonitorTopologyReader.Capture)
    {
    }

    internal RecoveryRecordArchiver(AppPaths paths, SessionStore sessions, LayoutStore layouts, DesktopLocationService locations,
        Func<string, IReadOnlyList<IconPosition>> readVerifiedLayout, Func<bool> hasParkedFiles,
        Func<MonitorTopology> captureTopology, Action? beforeArchive = null)
    {
        _paths = paths;
        _sessions = sessions;
        _layouts = layouts;
        _locations = locations;
        _readVerifiedLayout = readVerifiedLayout;
        _hasParkedFiles = hasParkedFiles;
        _captureTopology = captureTopology;
        _beforeArchive = beforeArchive;
    }

    public RecoveryRecordArchiveResult Archive(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] originalRecord = File.ReadAllBytes(_sessions.FilePath);
        SessionInfo session = SessionStore.ReadRecord(originalRecord) ?? throw new InvalidOperationException("The recovery record is unreadable.");
        VerifyOriginal(session);
        var icons = _readVerifiedLayout(session.OriginalDesktopPath);
        cancellationToken.ThrowIfCancellationRequested();

        // Recheck after the potentially slow Shell read. A new/changed record
        // or pending public files must never be retired by an earlier decision.
        VerifyOriginal(session);
        if (!File.ReadAllBytes(_sessions.FilePath).AsSpan().SequenceEqual(originalRecord))
            throw new InvalidOperationException("The recovery record changed. Review it again.");

        string layoutPath = _layouts.SaveAutomatic(new LayoutSnapshot
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            IsAutomatic = true,
            Topology = _captureTopology(),
            Icons = icons,
        }, DateTimeOffset.Now);
        cancellationToken.ThrowIfCancellationRequested();
        VerifyOriginal(session);
        string directory = Path.Combine(_paths.RecoveryDirectory, "ArchivedSessions");
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        _sessions.ArchiveVerified(originalRecord, archive, () =>
        {
            _beforeArchive?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        });
        return new(archive, layoutPath, icons.Count);
    }

    private void VerifyOriginal(SessionInfo session)
    {
        if (RecoveryRecordReview.Inspect(_locations.Detect(session.MeetingDesktopPathAtSwap), true, session)
                != RecoveryRecordState.OriginalDesktopActive || !Directory.Exists(session.OriginalDesktopPath))
            throw new InvalidOperationException("The desktop is not verified at the recorded original location.");
        if (session.WallpaperChanged || _hasParkedFiles())
            throw new InvalidOperationException("Wallpaper or public shortcuts still require recovery.");
    }

    private static IReadOnlyList<IconPosition> ReadVerifiedLayout(string originalPath)
    {
        var icons = DesktopShellWorker.Capture();
        // Two empty desktops cannot be distinguished without a marker. This
        // operation is read-only, so refuse that case instead of creating one.
        bool hasOriginalItem = icons.Any(icon => icon.IsFileSystem == true &&
            !string.IsNullOrWhiteSpace(icon.Identity) && Path.IsPathFullyQualified(icon.Identity) &&
            ShellViewReadiness.SamePath(Path.GetDirectoryName(icon.Identity), originalPath));
        if (!hasOriginalItem || !ShellDesktopService.SnapshotMatchesTarget(originalPath, icons))
            throw new DesktopViewVerificationException("The current desktop cannot be verified as the original folder.");
        return icons;
    }

    private static bool HasParkedFiles()
    {
        try { return Directory.EnumerateFileSystemEntries(PublicDesktopService.BackupDirectory).Any(); }
        catch (DirectoryNotFoundException) { return false; }
    }
}
