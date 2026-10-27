namespace MeetingDesktop.Core.Models;

/// <summary>Whether the desktop is currently swapped.</summary>
public enum SessionState
{
    /// <summary>The desktop points at the user's real desktop folder.</summary>
    Normal,

    /// <summary>The desktop points at a meeting folder this app created.</summary>
    Swapped,

    /// <summary>
    /// The desktop appears swapped but the app has no record of doing it -
    /// typically a swap performed by the older PowerShell scripts, or a state
    /// file that was deleted. Never overwrite silently in this case.
    /// </summary>
    Adopted,

    /// <summary>
    /// The state file and the registry disagree, or a required path is gone.
    /// The user has to look before anything else is attempted.
    /// </summary>
    Inconsistent,
}

/// <summary>
/// What the app needs to know to undo a swap.
/// </summary>
/// <remarks>
/// <see cref="MeetingDesktopPathAtSwap"/> is recorded because the user can
/// move the meeting folder between runs. Without it, restore would write the
/// icons back and repoint the registry at a folder that no longer exists,
/// which is how a desktop "disappears".
/// </remarks>
public sealed record SessionInfo
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DesktopSwitchMode SwitchMode { get; init; } = DesktopSwitchMode.RestartExplorer;

    /// <summary>The real desktop path, as it was before the swap.</summary>
    public required string OriginalDesktopPath { get; init; }

    /// <summary>The meeting folder the desktop was pointed at.</summary>
    public required string MeetingDesktopPathAtSwap { get; init; }

    public DateTimeOffset SwappedUtc { get; init; }

    /// <summary>Absolute path of the automatic layout snapshot taken before the swap.</summary>
    public string? AutoLayoutSnapshotPath { get; init; }

    /// <summary>Number of icons captured in the automatic snapshot.</summary>
    public int CapturedIconCount { get; init; }

    /// <summary>True only when this session captured return-preview.png successfully.</summary>
    public bool HasReturnPreview { get; init; }

    public MonitorTopology TopologyAtSwap { get; init; } = MonitorTopology.Empty;

    /// <summary>
    /// The wallpaper as it was before the swap, recorded so it can be put back.
    /// </summary>
    /// <remarks>
    /// Only meaningful when <see cref="WallpaperChanged"/> is true. A user who
    /// has not configured a meeting wallpaper keeps their own, and restore must
    /// not touch it - re-applying an identical wallpaper would be a pointless
    /// flicker on the projector at the exact moment they are getting ready to
    /// present.
    /// </remarks>
    public WallpaperState? WallpaperAtSwap { get; init; }

    /// <summary>Optional private image copy used if the original generated wallpaper disappears.</summary>
    /// <remarks>This restores a static background image, not a dynamic wallpaper engine's project or scene.</remarks>
    public string? WallpaperBackupPath { get; init; }

    /// <summary>Whether the swap replaced the wallpaper and restore must undo that.</summary>
    public bool WallpaperChanged { get; init; }
    /// <summary>Authenticated shortcut collision mappings retained across restore retries.</summary>
    public IReadOnlyList<ShortcutConflict>? RestoredShortcutConflicts { get; init; }

    /// <summary>A restore operation may have moved shortcuts without a complete report.</summary>
    public bool ShortcutRestoreUncertain { get; init; }
}
