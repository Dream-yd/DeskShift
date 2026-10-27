using System.Globalization;
using System.Windows.Media;
using MeetingDesktop.App.Localization;
using MeetingDesktop.App.Services;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.App.ViewModels;

/// <summary>
/// The shell: navigation, session state, and the shared status line.
/// </summary>
/// <remarks>
/// Session state is resolved once at startup and refreshed after every
/// operation, because it decides which of the two primary actions is even
/// meaningful. Prepare while already swapped would point the desktop at the
/// meeting folder a second time with the real one still unreachable; restore
/// while not swapped has nothing to undo. Both are refused rather than
/// attempted.
/// </remarks>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly SessionStore _sessions;
    private readonly LayoutStore _layouts;
    private readonly DesktopLocationService _locations;
    private readonly ISessionElevationBroker? _permission;
    private readonly WindowsStartupRegistration _startup;
    private string _permissionStatusKey = "";
    private SwitchTimingTrace? _switchTiming;
    internal SwitchTimingHistory SwitchTimings { get; } = new();
    private int _progressGeneration;
    private double? _lastSwitchSeconds;
    private bool _disposed;

    private AppSettings _currentSettings;
    private SessionState _sessionState = SessionState.Normal;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private double _progressValue;
    private object? _currentView;
    private RecoveryRecordState _recordState;
    private SessionInfo? _pendingSession;
    internal DesktopLocation? CurrentDesktopLocation { get; private set; }
    internal DailySwitchPlan DailySwitch { get; private set; } = new(DailySwitchDirection.Recovery, null, null, null);
    private readonly AsyncRelayCommand _archiveRecoveryCommand;
    private readonly QuickSwitchController _quickSwitch;
    public event Action<bool>? QuickSwitchVisibilityRequested;
    public event Action<string>? NotificationRequested;
    public System.Windows.Input.ICommand QuickSwitchCommand { get; }

    internal void SetQuickSwitchVisibility(bool visible) => QuickSwitchVisibilityRequested?.Invoke(visible);

    public MainViewModel(
        AppPaths paths,
        SettingsStore settings,
        SessionStore sessions,
        LayoutStore layouts,
        DesktopLocationService locations,
        IElevationBroker elevation,
        WindowsStartupRegistration? startup = null,
        AppSettings? initialSettings = null)
    {
        _paths = paths;
        _settings = settings;
        _sessions = sessions;
        _layouts = layouts;
        _locations = locations;
        _permission = elevation as ISessionElevationBroker;
        _startup = startup ?? new WindowsStartupRegistration(Environment.ProcessPath!, paths.DataDirectory);

        _currentSettings = initialSettings ?? settings.LoadForStartup();
        MigrateStartupCommand = new RelayCommand(MigrateStartup, () => !IsBusy);
        ThemeToggleCommand = new RelayCommand(() => ChangeTheme(
            ThemeManager.IsDark ? ThemePreference.Light : ThemePreference.Dark));
        ThemeManager.Changed += OnThemeChanged;

        Prepare = new PrepareViewModel(paths, layouts, sessions, locations, elevation, this);
        Restore = new RestoreViewModel(paths, layouts, sessions, elevation, this);
        Layouts = new LayoutsViewModel(layouts, paths, this);
        Settings = new SettingsViewModel(paths, settings, locations, this);
        Diagnostics = new DiagnosticsViewModel(paths, sessions, locations, this);
        AuthorizationCommand = new AsyncRelayCommand(ChangeAuthorizationAsync, () => !IsBusy && _permission?.IsAuthorizing != true);
        if (_permission is not null) _permission.AuthorizationChanged += OnAuthorizationChanged;

        _currentView = Prepare;

        // Navigation goes through a command rather than through the radio
        // buttons' IsChecked bindings. Those bindings are two-way, and WPF
        // pushes values through a binding while initialising the visual tree -
        // so the buttons, not the user, decided which page the app opened on.
        // A command only ever runs when something is actually clicked.
        NavigateCommand = new RelayCommand<string>(page => Select(page switch
        {
            "Restore" => Restore,
            "Layouts" => Layouts,
            "Settings" => Settings,
            "Diagnostics" => Diagnostics,
            _ => (object)Prepare,
        }));

        ReviewRecoveryCommand = new RelayCommand(() =>
        {
            RefreshSessionState();
            Select(CanRestore ? Restore : Diagnostics);
        }, () => !IsBusy);
        _archiveRecoveryCommand = new AsyncRelayCommand(ArchiveRecoveryAsync,
            () => !IsBusy && CanArchiveRecovery);

        _quickSwitch = new QuickSwitchController(() =>
        {
            if (!IsBusy) RefreshSessionState();
            return new(SessionState, _sessions.Exists, IsBusy,
                CanReturnToOriginal);
        }, Prepare.PrepareFromShortcutAsync, Restore.RestoreFromShortcutAsync, () =>
        {
            ReviewRecoveryCommand.Execute(null);
            StatusText = RecoveryNoticeMessage;
            Notify(RecoveryNoticeMessage);
        });
        QuickSwitchCommand = new AsyncRelayCommand(async () =>
        {
            try { await _quickSwitch.ExecuteAsync(); }
            catch (Exception)
            {
                StatusText = LocalizationManager.Instance["QuickSwitchFailed"];
                Notify(StatusText);
            }
        }, () => !IsBusy);

        RefreshSessionState();
        if (DailySwitch.StartupPage != "Prepare")
            Select(DailySwitch.StartupPage == "Restore" ? Restore : Diagnostics);
    }

    /// <summary>Switches the visible page. The argument is the page's name.</summary>
    public System.Windows.Input.ICommand NavigateCommand { get; }

    public PrepareViewModel Prepare { get; }

    public RestoreViewModel Restore { get; }

    public LayoutsViewModel Layouts { get; }

    public SettingsViewModel Settings { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public string DataDirectory => _paths.DataDirectory;

    public bool IsPortable => _paths.IsPortable;

    public AppSettings CurrentSettings => _currentSettings;

    public bool IsLiquidGlass
    {
        get => _currentSettings.Skin == SkinPreference.LiquidGlass;
        set
        {
            if (value == IsLiquidGlass) return;
            SavePreference(_currentSettings with { Skin = value ? SkinPreference.LiquidGlass : SkinPreference.Classic });
            OnPropertyChanged();
        }
    }
    public void ChangeTrayDoubleClickAction(TrayDoubleClickAction action)
    {
        if (!Enum.IsDefined(action) || action == _currentSettings.TrayDoubleClickAction) return;
        SavePreference(_currentSettings with { TrayDoubleClickAction = action });
    }

    internal bool UseCompatibilitySwitching() => !IsBusy && SavePreference(
        _currentSettings with { SwitchingExperience = SwitchingExperience.Compatibility, UseLiveDesktopSwitching = false },
        _settings.Load() with { SwitchingExperience = SwitchingExperience.Compatibility, UseLiveDesktopSwitching = false });
    public bool StartWithWindows
    {
        get => _currentSettings.StartWithWindows;
        set
        {
            if (value == StartWithWindows || IsBusy) { OnPropertyChanged(); return; }
            AppSettings updated = _currentSettings with { StartWithWindows = value };
            try
            {
                _startup.SetEnabled(value, () => _settings.Save(updated));
                OnSettingsChanged(updated);
                StatusText = LocalizationManager.Instance[value ? "StartupEnabled" : "StartupDisabled"];
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or AggregateException or System.Security.SecurityException)
            {
                StatusText = LocalizationManager.Instance.Format("StartupSaveFailed", ex.Message);
                Notify(StatusText);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(StartupStatusText));
        }
    }
    public System.Windows.Input.ICommand MigrateStartupCommand { get; }
    public bool CanMigrateStartup
    {
        get
        {
            try { return _startup.CanMigratePreviousVersion(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return false; }
        }
    }
    private void MigrateStartup()
    {
        try
        {
            if (StartWithWindows)
                StatusText = LocalizationManager.Instance[_startup.TryMigratePreviousVersion() ? "StartupEnabled" : "StartupRegistrationMismatch"];
            else
            {
                _startup.SetEnabled(false);
                StatusText = LocalizationManager.Instance["StartupDisabled"];
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or AggregateException or System.Security.SecurityException)
        { StatusText = LocalizationManager.Instance.Format("StartupSaveFailed", ex.Message); }
        OnPropertyChanged(nameof(CanMigrateStartup));
        OnPropertyChanged(nameof(StartupStatusText));
        Settings.RefreshStartupSelection();
    }
    public string StartupStatusText
    {
        get
        {
            try
            {
                return LocalizationManager.Instance[_startup.IsEnabled() ? "StartupEnabled" :
                    StartWithWindows || _startup.ReadActual() is not null ? "StartupRegistrationMismatch" : "StartupDisabled"];
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return LocalizationManager.Instance["StartupUnavailable"]; }
        }
    }
    private bool SavePreference(AppSettings updated, AppSettings? persisted = null)
    {
        try { _settings.Save(persisted ?? updated); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText = LocalizationManager.Instance.Format("SettingsSaveFailed", ex.Message);
            return false;
        }
        OnSettingsChanged(updated);
        return true;
    }

    public System.Windows.Input.ICommand AuthorizationCommand { get; }
    public bool HasSwitchPermission => _permission is null || _permission.IsAuthorized;
    public string PermissionActionText => LocalizationManager.Instance[HasSwitchPermission && _permission is not null ? "PermissionRevoke" : "PermissionGrant"];
    public string PermissionStatusText => LocalizationManager.Instance[!string.IsNullOrEmpty(_permissionStatusKey) ? _permissionStatusKey :
        _permission?.IsAuthorizing == true ? "PermissionGranting" : HasSwitchPermission ? "PermissionReady" : "PermissionMissing"];
    public Brush PermissionBrush => HasSwitchPermission ? Brushes.MediumAquamarine : Brushes.Goldenrod;
    public bool EnsureSwitchPermission()
    {
        if (HasSwitchPermission) return true;
        StatusText = LocalizationManager.Instance["PermissionNeededBeforeSwitch"];
        Notify(StatusText);
        return false;
    }
    private async Task ChangeAuthorizationAsync()
    {
        if (_permission is null) return;
        if (_permission.IsAuthorized)
        {
            IsBusy = true;
            try { await Task.Run(_permission.Revoke); _permissionStatusKey = ""; }
            catch (Exception) { _permissionStatusKey = "PermissionFailed"; }
            finally { IsBusy = false; RefreshPermissionText(); }
            return;
        }
        _permissionStatusKey = "";
        IsBusy = true;
        try
        {
            var outcome = await _permission.AuthorizeAsync();
            _permissionStatusKey = outcome == ElevationOutcome.Succeeded ? "" :
                outcome == ElevationOutcome.Declined ? "PermissionDeclined" : "PermissionFailed";
            StatusText = PermissionStatusText;
        }
        catch (Exception) { _permissionStatusKey = "PermissionFailed"; StatusText = PermissionStatusText; }
        finally { IsBusy = false; RefreshPermissionText(); }
    }
    private void OnAuthorizationChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(RefreshPermissionText);
        else RefreshPermissionText();
    }
    private void RefreshPermissionText()
    {
        OnPropertyChanged(nameof(HasSwitchPermission));
        OnPropertyChanged(nameof(PermissionActionText));
        OnPropertyChanged(nameof(PermissionStatusText));
        OnPropertyChanged(nameof(PermissionBrush));
        (AuthorizationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
    public void Notify(string message) => NotificationRequested?.Invoke(message);
    internal void BeginSwitchTiming(bool returning = false) { _progressGeneration++; _switchTiming = new(returning); }
    internal void MarkDesktopReady()
    {
        _switchTiming?.DesktopReady();
        StatusText = LocalizationManager.Instance["ProgressFinishing"];
    }
    internal void EndSwitchTiming(bool succeeded = false)
    {
        _progressGeneration++;
        if (_switchTiming is null) return;
        var result = _switchTiming.Finish(succeeded);
        SwitchTimings.Add(result);
        _lastSwitchSeconds = result.TotalMilliseconds / 1000;
        _switchTiming = null;
        OnPropertyChanged(nameof(LastSwitchDurationText));
    }
    public string LastSwitchDurationText => _lastSwitchSeconds is { } seconds
        ? LocalizationManager.Instance.Format("SwitchDuration", seconds.ToString("0.00", CultureInfo.CurrentCulture)) : "";

    public System.Windows.Input.ICommand ThemeToggleCommand { get; }
    public string ThemeToggleGlyph => ThemeManager.IsDark ? "☀" : "☾";
    public string ThemeToggleLabel => LocalizationManager.Instance[
        ThemeManager.IsDark ? "ThemeSwitchToLight" : "ThemeSwitchToDark"];

    public void ChangeTheme(ThemePreference preference)
    {
        if (!Enum.IsDefined(preference) || preference == _currentSettings.Theme) return;
        var updated = _currentSettings with { Theme = preference };
        try { _settings.Save(updated); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText = LocalizationManager.Instance.Format("SettingsSaveFailed", ex.Message);
            Settings.RefreshThemeSelection();
            return;
        }
        OnSettingsChanged(updated);
    }

    private void RefreshThemeText()
    {
        OnPropertyChanged(nameof(ThemeToggleGlyph));
        OnPropertyChanged(nameof(ThemeToggleLabel));
    }
    private void OnThemeChanged(object? sender, EventArgs e) => RefreshThemeText();

    // ---------------------------------------------------------------
    // Navigation
    // ---------------------------------------------------------------

    public object? CurrentView
    {
        get => _currentView;
        private set => SetProperty(ref _currentView, value);
    }

    public bool IsPrepareSelected
    {
        get => ReferenceEquals(CurrentView, Prepare);
        set { if (value) Select(Prepare); }
    }

    public bool IsRestoreSelected
    {
        get => ReferenceEquals(CurrentView, Restore);
        set { if (value) Select(Restore); }
    }

    public bool IsLayoutsSelected
    {
        get => ReferenceEquals(CurrentView, Layouts);
        set { if (value) Select(Layouts); }
    }

    public bool IsSettingsSelected
    {
        get => ReferenceEquals(CurrentView, Settings);
        set { if (value) Select(Settings); }
    }

    public bool IsDiagnosticsSelected
    {
        get => ReferenceEquals(CurrentView, Diagnostics);
        set { if (value) Select(Diagnostics); }
    }

    private void Select(object view)
    {
        if (ReferenceEquals(CurrentView, Settings) && !ReferenceEquals(view, Settings)) Settings.CommitMeetingDesktopPath();
        CurrentView = view;

        foreach (string name in new[]
                 {
                     nameof(IsPrepareSelected), nameof(IsRestoreSelected),
                     nameof(IsLayoutsSelected), nameof(IsSettingsSelected),
                     nameof(IsDiagnosticsSelected),
                 })
        {
            OnPropertyChanged(name);
        }

        // A page may show data that another page changed.
        (view as IPageViewModel)?.OnActivated();
    }

    // ---------------------------------------------------------------
    // Session state
    // ---------------------------------------------------------------

    public SessionState SessionState => _sessionState;

    public string SessionStateText => _sessionState switch
    {
        SessionState.Swapped => LocalizationManager.Instance["StateSwapped"],
        SessionState.Adopted => LocalizationManager.Instance["StateAdopted"],
        SessionState.Inconsistent => LocalizationManager.Instance[ShowArchiveRecovery ? "StateOldRecord" : "StateInconsistent"],
        _ => LocalizationManager.Instance["StateNormal"],
    };

    public string SessionStateHint => _sessionState switch
    {
        SessionState.Swapped => LocalizationManager.Instance["StateSwappedHint"],
        SessionState.Adopted => LocalizationManager.Instance["StateAdoptedHint"],
        SessionState.Inconsistent => LocalizationManager.Instance[ShowArchiveRecovery ? "StateOldRecordHint" : "StateInconsistentHint"],
        _ => LocalizationManager.Instance["StateNormalHint"],
    };

    /// <summary>
    /// Colour of the state pill. Text is dark on all of these, so they are
    /// deliberately light tints rather than the saturated status colours.
    /// </summary>
    public Brush SessionStateBrush => _sessionState switch
    {
        SessionState.Swapped => new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)),
        SessionState.Adopted => new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x4D)),
        SessionState.Inconsistent => new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
        _ => new SolidColorBrush(Color.FromRgb(0xA9, 0xC6, 0xF8)),
    };

    public bool CanPrepare => DailySwitch.Direction == DailySwitchDirection.ToAlternate;

    public bool CanReturnToOriginal => DailySwitch.Direction == DailySwitchDirection.ToOriginal;

    public bool CanRestore => _sessions.Load() is not null;

    public bool HasRecoveryNotice => DailySwitch.Direction == DailySwitchDirection.Recovery;
    public bool ShowArchiveRecovery => _recordState == RecoveryRecordState.OriginalDesktopActive;
    public bool CanArchiveRecovery => ShowArchiveRecovery && _pendingSession is { WallpaperChanged: false };
    public System.Windows.Input.ICommand ReviewRecoveryCommand { get; }
    public System.Windows.Input.ICommand ArchiveRecoveryCommand => _archiveRecoveryCommand;
    public string RecoveryActionLabel => LocalizationManager.Instance[CanRestore ? "RecoveryReviewAction" : "RecoveryDiagnosticsAction"];
    public string RecoveryNoticeTitle => LocalizationManager.Instance[ShowArchiveRecovery ? "RecoveryAlreadyHomeTitle" : "RecoveryReviewTitle"];
    public string RecoveryNoticeMessage => ShowArchiveRecovery && _pendingSession is { } session
        ? LocalizationManager.Instance.Format(session.WallpaperChanged ? "RecoveryAlreadyHomePendingAssetsMessage" : "RecoveryAlreadyHomeMessage",
            session.SwappedUtc.ToLocalTime().ToString("g", LocalizationManager.Instance.Culture), session.CapturedIconCount)
        : LocalizationManager.Instance[_recordState == RecoveryRecordState.Unreadable ? "RecoveryUnreadableMessage" :
            CanRestore ? "RecoveryPendingMessage" : "RecoveryUnknownMessage"];

    private async Task ArchiveRecoveryAsync()
    {
        if (IsBusy || !CanArchiveRecovery) return;
        var owner = System.Windows.Application.Current?.MainWindow;
        string body = LocalizationManager.Instance["RecoveryArchiveConfirm"];
        string title = LocalizationManager.Instance["RecoveryArchiveAction"];
        var answer = owner is { IsLoaded: true }
            ? System.Windows.MessageBox.Show(owner, body, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No)
            : System.Windows.MessageBox.Show(body, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            var archiver = new RecoveryRecordArchiver(_paths, _sessions, _layouts, _locations);
            var result = await Task.Run(() => archiver.Archive());
            RefreshSessionState();
            Select(Prepare);
            StatusText = LocalizationManager.Instance.Format("RecoveryArchiveSucceeded", result.IconCount, result.RecordPath);
        }
        catch (DesktopViewVerificationException)
        {
            StatusText = LocalizationManager.Instance["RecoveryArchiveViewUnverified"];
        }
        catch (Exception)
        {
            StatusText = LocalizationManager.Instance["RecoveryArchiveFailed"];
        }
        finally { IsBusy = false; RefreshSessionState(); }
    }

    /// <summary>Re-reads the machine state and republishes everything that depends on it.</summary>
    public void RefreshSessionState()
    {
        // Through AppPaths, never the raw setting: on a fresh install the
        // setting is null and the swap went to the built-in default. Handing
        // the detector a null would make it read that as "no meeting folder is
        // configured", miss the app's own swap, and report Adopted - the state
        // that means "swapped by something we have no record of", on a machine
        // holding the record.
        _pendingSession = _sessions.Load();
        string configuredTarget = _paths.ResolveMeetingDesktopDirectory(_currentSettings);
        // The record owns the active alternate desktop even if settings changed since the swap.
        string activeTarget = _pendingSession?.MeetingDesktopPathAtSwap ?? configuredTarget;
        DesktopLocation location = _locations.Detect(activeTarget);
        CurrentDesktopLocation = location;
        _sessionState = DesktopLocationService.ClassifySessionState(location, activeTarget);
        bool recordExists = _sessions.Exists;
        if (recordExists && _sessionState == SessionState.Normal)
            _sessionState = SessionState.Inconsistent;
        _recordState = RecoveryRecordReview.Inspect(location, recordExists, _pendingSession);
        DailySwitch = DailySwitchPlan.Resolve(_sessionState, location, recordExists, _pendingSession, configuredTarget);

        OnPropertyChanged(nameof(SessionState));
        OnPropertyChanged(nameof(SessionStateText));
        OnPropertyChanged(nameof(SessionStateHint));
        OnPropertyChanged(nameof(SessionStateBrush));
        OnPropertyChanged(nameof(CanPrepare));
        OnPropertyChanged(nameof(CanReturnToOriginal));
        OnPropertyChanged(nameof(CanRestore));
        RefreshRecoveryText();
        _archiveRecoveryCommand.RaiseCanExecuteChanged();

        Prepare.RaiseCanExecuteChanged();
        Restore.RaiseCanExecuteChanged();
        Settings.RaiseCanExecuteChanged();
    }

    /// <summary>Re-applies the stored language preference to the whole UI.</summary>
    public void ApplyLanguage()
    {
        LocalizationManager.Instance.ApplyPreference(_currentSettings.Language);
        RefreshThemeText();
        RefreshRecoveryText();
        RefreshPermissionText();
        OnPropertyChanged(nameof(StartupStatusText));
        Settings.RefreshStartupSelection();
        OnPropertyChanged(nameof(LastSwitchDurationText));

        // Every one of these reads through LocalizationManager, so they all
        // change; the indexer notification on the manager refreshes anything
        // bound in XAML, but view-model properties produced in C# need telling.
        OnPropertyChanged(nameof(SessionStateText));
        OnPropertyChanged(nameof(SessionStateHint));
        Prepare.RefreshLocalizedText();
        Restore.RefreshLocalizedText();
        Layouts.RefreshLocalizedText();
        Settings.RefreshLocalizedText();
        Diagnostics.RefreshLocalizedText();
    }

    /// <summary>Called by the settings page after it writes a new settings object.</summary>
    public void OnSettingsChanged(AppSettings settings)
    {
        bool desktopChanged = !string.Equals(_currentSettings.MeetingDesktopPath, settings.MeetingDesktopPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_currentSettings.MeetingWallpaperPath, settings.MeetingWallpaperPath, StringComparison.OrdinalIgnoreCase);
        _currentSettings = settings;
        OnPropertyChanged(nameof(CurrentSettings));
        OnPropertyChanged(nameof(IsLiquidGlass));
        OnPropertyChanged(nameof(StartWithWindows));
        ThemeManager.Apply(settings.Theme);
        Settings.RefreshThemeSelection();
        Settings.RefreshSwitchingExperience();
        Settings.RefreshMeetingDesktopSaveStatus();
        if (desktopChanged) Prepare.RefreshSettings();
        ApplyLanguage();
        RefreshSessionState();
    }

    // ---------------------------------------------------------------
    // Language, from the sidebar
    // ---------------------------------------------------------------

    /// <summary>
    /// True when the interface is currently Chinese.
    /// </summary>
    /// <remarks>
    /// A two-state switch lives in the sidebar because language is something a
    /// user reaches for while looking at a screen they cannot read - which is
    /// exactly when they will not go hunting through Settings. The full
    /// three-way choice, including "follow Windows", stays in Settings where
    /// it can be explained; picking a side here writes an explicit preference.
    ///
    /// The two properties are deliberately complementary rather than backed by
    /// one field: the resolved culture is the single source of truth, so the
    /// switch stays correct when the preference is "follow Windows" and the
    /// answer came from the operating system.
    /// </remarks>
    public bool IsChineseUi
    {
        get => IsChineseCulture(LocalizationManager.Instance.Culture);
        set { if (value) SetLanguage(LanguagePreference.SimplifiedChinese); }
    }

    /// <summary>True when the interface is currently English.</summary>
    public bool IsEnglishUi
    {
        get => !IsChineseCulture(LocalizationManager.Instance.Culture);
        set { if (value) SetLanguage(LanguagePreference.English); }
    }

    private void SetLanguage(LanguagePreference preference)
    {
        if (_currentSettings.Language == preference)
        {
            return;
        }

        var updated = _currentSettings with { Language = preference };
        try
        {
            _settings.Save(updated);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText = LocalizationManager.Instance.Format("SettingsSaveFailed", ex.Message);
            OnPropertyChanged(nameof(IsChineseUi));
            OnPropertyChanged(nameof(IsEnglishUi));
            Settings.SyncLanguageFromShell();
            return;
        }
        _currentSettings = updated;

        ApplyLanguage();

        OnPropertyChanged(nameof(CurrentSettings));
        OnPropertyChanged(nameof(IsChineseUi));
        OnPropertyChanged(nameof(IsEnglishUi));

        Settings.SyncLanguageFromShell();
    }

    private static bool IsChineseCulture(CultureInfo culture) =>
        (culture.Name.Length > 0 ? culture.Name : culture.TwoLetterISOLanguageName)
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------
    // Shared status line
    // ---------------------------------------------------------------

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            (ReviewRecoveryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            _archiveRecoveryCommand.RaiseCanExecuteChanged();
            Prepare.RaiseCanExecuteChanged();
            Restore.RaiseCanExecuteChanged();
            Layouts.RaiseCanExecuteChanged();
            Settings.RaiseCanExecuteChanged();
            (QuickSwitchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (AuthorizationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void RefreshRecoveryText()
    {
        foreach (string name in new[] { nameof(HasRecoveryNotice), nameof(ShowArchiveRecovery), nameof(CanArchiveRecovery),
            nameof(RecoveryActionLabel), nameof(RecoveryNoticeTitle), nameof(RecoveryNoticeMessage) }) OnPropertyChanged(name);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    /// <summary>
    /// Builds the progress reporter handed to the Core workflows.
    /// </summary>
    /// <remarks>
    /// Core reports <see cref="ProgressKey"/> values, never sentences, so the
    /// wording is resolved here in the UI's current language.
    /// </remarks>
    public IProgress<ProgressReport> CreateProgress()
    {
        int generation = _progressGeneration;
        var timing = _switchTiming;
        var ui = new Progress<ProgressReport>(report =>
        {
            if (_disposed || generation != _progressGeneration) return;
            if (timing?.IsDesktopReady == true && report.Key != ProgressKey.Done) return;
            string text = LocalizationManager.Instance[report.Key.ToString() switch
            {
                "DetectingDesktopPath" => "ProgressDetectingDesktopPath",
                "ReadingCurrentLayout" => "ProgressReadingCurrentLayout",
                "CapturingPreview" => "ProgressCapturingPreview",
                "WritingLayoutSnapshot" => "ProgressWritingLayoutSnapshot",
                "RedirectingDesktop" => "ProgressRedirectingDesktop",
                "MovingPublicShortcuts" => "ProgressMovingPublicShortcuts",
                "RestoringPublicShortcuts" => "ProgressRestoringPublicShortcuts",
                "ApplyingWallpaper" => "ProgressApplyingWallpaper",
                "RestoringWallpaper" => "ProgressRestoringWallpaper",
                "RestartingExplorer" => "ProgressRestartingExplorer",
                "RefreshingDesktop" => "ProgressRefreshingDesktop",
                "WritingShellBagBlob" => "ProgressWritingShellBagBlob",
                "ApplyingLayout" => "ProgressApplyingLayout",
                "VerifyingLayout" => "ProgressVerifyingLayout",
                _ => "ProgressDone",
            }];

            StatusText = text;

            if (report.Fraction is { } fraction)
            {
                ProgressValue = fraction * 100;
            }
        });
        return new TimedSwitchProgress(timing, ui);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Prepare.Dispose();
        ThemeManager.Changed -= OnThemeChanged;
        if (_permission is not null) { _permission.AuthorizationChanged -= OnAuthorizationChanged; _permission.Dispose(); }
    }
}

/// <summary>A page that wants to know when it becomes visible.</summary>
public interface IPageViewModel
{
    void OnActivated();

    /// <summary>Re-reads any text this page cached in a field.</summary>
    void RefreshLocalizedText();
}

internal enum DailySwitchDirection { Recovery, ToAlternate, ToOriginal }

/// <summary>A single refreshed decision shared by startup, the page labels and its action.</summary>
internal sealed record DailySwitchPlan(
    DailySwitchDirection Direction, string? CurrentPath, string? TargetPath, SessionInfo? Session)
{
    public string StartupPage => Direction != DailySwitchDirection.Recovery ? "Prepare" :
        Session is not null ? "Restore" : "Diagnostics";

    public static DailySwitchPlan Resolve(SessionState state, DesktopLocation location,
        bool recordExists, SessionInfo? session, string alternate)
    {
        var review = new DailySwitchPlan(DailySwitchDirection.Recovery, location.ResolvedPath, null, session);
        if (!location.IsUsable) return review;
        if (state == SessionState.Swapped && recordExists &&
            RecoveryRecordReview.Inspect(location, recordExists, session) == RecoveryRecordState.MeetingDesktopActive)
            return new(DailySwitchDirection.ToOriginal, location.ResolvedPath, session!.OriginalDesktopPath, session);
        if (state != SessionState.Normal || recordExists || session is not null) return review;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(location.ResolvedPath!) ||
                !System.IO.Path.IsPathFullyQualified(alternate) ||
                ShellViewReadiness.SamePath(location.ResolvedPath!, alternate)) return review;
            return new(DailySwitchDirection.ToAlternate, location.ResolvedPath, alternate, null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.IOException)
        { return review; }
    }

    // SessionInfo contains topology lists: record equality would compare those by reference after each JSON reload.
    public bool Matches(DailySwitchPlan other) => Direction == other.Direction &&
        System.Text.Json.JsonSerializer.Serialize(Session) == System.Text.Json.JsonSerializer.Serialize(other.Session) &&
        SamePath(CurrentPath, other.CurrentPath) && SamePath(TargetPath, other.TargetPath);

    private static bool SamePath(string? left, string? right) => left is not null && right is not null &&
        ShellViewReadiness.SamePath(left, right);
}
