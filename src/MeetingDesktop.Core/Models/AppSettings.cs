namespace MeetingDesktop.Core.Models;

/// <summary>Which language the UI uses.</summary>
public enum LanguagePreference
{
    /// <summary>Follow the operating system.</summary>
    Auto,

    English,

    SimplifiedChinese,
}

/// <summary>User settings, persisted as JSON.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// Where the empty meeting desktop lives.
    /// </summary>
    /// <remarks>
    /// Null means "use the default under the app data directory". Stored as an
    /// absolute path because it is handed straight to the registry, and a
    /// relative path there would be resolved against explorer's working
    /// directory rather than ours.
    /// </remarks>
    public string? MeetingDesktopPath { get; init; }

    public IReadOnlyList<DesktopProfile> Profiles { get; init; } = Array.Empty<DesktopProfile>();

    public LanguagePreference Language { get; init; } = LanguagePreference.Auto;

    public ThemePreference Theme { get; init; } = ThemePreference.Dark;

    public SkinPreference Skin { get; init; } = SkinPreference.Classic;

    public bool StartWithWindows { get; init; }

    public TrayDoubleClickAction TrayDoubleClickAction { get; init; } = TrayDoubleClickAction.OpenWindow;

    /// <summary>
    /// Capture a screenshot of the desktop just before swapping, so the
    /// Restore page can preview what the user is going back to.
    /// </summary>
    public bool CaptureReturnPreview { get; init; } = true;

    /// <summary>Opt-in until no-restart switching has passed wider Windows compatibility checks.</summary>
    public bool UseLiveDesktopSwitching { get; init; }

    public SwitchingExperience SwitchingExperience { get; init; } = SwitchingExperience.Responsive;

    /// <summary>Keep an in-memory screen image visible during the switch, then fade it away.</summary>
    public bool SmoothDesktopTransition { get; init; } = true;

    public bool ShowSwitchNotification { get; init; } = true;

    /// <summary>Zero keeps history; 7, 15 or 30 cleans obsolete automatic layouts.</summary>
    public int AutomaticSnapshotRetentionDays { get; init; }

    public QuickSwitchHotkey QuickSwitchHotkey { get; init; } = QuickSwitchHotkey.ControlAltM;

    public bool ConfirmQuickSwitch { get; init; }

    public bool CloseToTray { get; init; }

    public DesktopMouseGesture DesktopMouseGesture { get; init; } = DesktopMouseGesture.MiddleDoubleClick;

    /// <summary>
    /// Wallpaper to show while the meeting desktop is active.
    /// </summary>
    /// <remarks>
    /// Null means "leave the wallpaper alone", which is the default and the
    /// right one: a user who has not asked for a different background should
    /// get the one they already had, not a surprise. Set it to the conference
    /// poster and the clean desktop also looks like it belongs to the event.
    ///
    /// This is a path into the app's own data folder - see
    /// <c>WallpaperService.Import</c> - so that deleting the original file does
    /// not leave the projector showing a black screen.
    /// </remarks>
    public string? MeetingWallpaperPath { get; init; }

    /// <summary>
    /// Dismissed notice about a legacy PowerShell installation being detected.
    /// </summary>
    public bool LegacyInstallNoticeDismissed { get; init; }
}

/// <summary>A named alternate desktop configuration; runtime paths stay local.</summary>
public sealed record DesktopProfile(string Name, string Folder, string? Wallpaper);
