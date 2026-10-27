using System.Collections.ObjectModel;
// System.IO and Microsoft.Win32 are named explicitly: a WPF project's implicit
// usings deliberately leave System.IO out (Path would collide with
// System.Windows.Shapes.Path), and the folder picker lives in Microsoft.Win32.
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using MeetingDesktop.App.Localization;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.ViewModels;

/// <summary>
/// The Settings page: language, the folder the desktop is swapped to, and where
/// this app keeps its own data.
/// </summary>
/// <remarks>Folder edits commit on completion; quick controls save immediately after view initialization.</remarks>
public sealed class SettingsViewModel : ObservableObject, IPageViewModel
{
    /// <summary>
    /// Language picker order: index <c>i</c> of the combo box is
    /// <c>Languages[i]</c>. The two must stay in step, so the picker declares no
    /// items of its own.
    /// </summary>
    private static readonly LanguagePreference[] Languages =
    [
        LanguagePreference.Auto,
        LanguagePreference.English,
        LanguagePreference.SimplifiedChinese,
    ];

    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly DesktopLocationService _locations;
    private readonly MainViewModel _shell;
    private readonly MeetingDesktopProvisioner _provisioner;

    private readonly RelayCommand _browseCommand;
    private readonly RelayCommand _useDefaultCommand;
    private readonly RelayCommand _saveCommand;
    private readonly RelayCommand _chooseWallpaperCommand;
    private readonly RelayCommand _useCurrentWallpaperCommand;
    private readonly AsyncRelayCommand _recoveryExportCommand;
    private string _recoveryExportStatusKey = string.Empty;
    private object?[] _recoveryExportStatusArguments = [];

    private readonly WallpaperService _wallpapers = new();

    private string? _meetingWallpaperPath;

    private string _meetingDesktopPath;
    private string _detectionSummary;
    private string _validationMessage = string.Empty;
    private bool _hasValidationProblem;
    private int _selectedLanguageIndex;
    private bool _trackUserLanguageChanges;
    private bool _capturePreview;
    private bool _useLiveDesktopSwitching;
    private bool _smoothDesktopTransition;
    private bool _showSwitchNotification;
    private int _selectedSnapshotRetentionIndex;
    private static readonly int[] SnapshotRetentionDays = [0, 7, 15, 30];
    private int _selectedQuickHotkeyIndex;
    private int _selectedMouseGestureIndex;
    private bool _confirmQuickSwitch;
    private bool _closeToTray;
    private string _quickAccessStatus = "";
    private bool _trackQuickAccessChanges;
    private string _quickSaveStatusKey = "QuickAutoSaveHint";
    private string _quickSaveError = "";
    private string _folderSaveError = "";

    /// <summary>
    /// The last verdict, kept so the message can be rebuilt in a new language.
    /// </summary>
    private PathValidation? _validation;

    public SettingsViewModel(
        AppPaths paths,
        SettingsStore settings,
        DesktopLocationService locations,
        MainViewModel shell)
    {
        _paths = paths;
        _settings = settings;
        _locations = locations;
        _shell = shell;

        // The same checker the workflows use, so a path this page accepts is not
        // refused a moment later by Prepare.
        _provisioner = new MeetingDesktopProvisioner(DetectRealDesktop);

        _browseCommand = new RelayCommand(Browse, () => CanEditMeetingDesktopPath);
        _useDefaultCommand = new RelayCommand(UseDefault, () => CanEditMeetingDesktopPath);
        _saveCommand = new RelayCommand(Save, () => CanEditMeetingDesktopPath && !HasValidationProblem);

        _chooseWallpaperCommand = new RelayCommand(ChooseWallpaper);
        _useCurrentWallpaperCommand = new RelayCommand(UseCurrentWallpaper);
        _recoveryExportCommand = new AsyncRelayCommand(RecoveryExportAsync, () => !_shell.IsBusy);
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsBusy)) _recoveryExportCommand.RaiseCanExecuteChanged();
            if (e.PropertyName == nameof(MainViewModel.CurrentSettings)) RefreshStartupSelection();
        };

        AppSettings current = _shell.CurrentSettings;
        _meetingDesktopPath = EffectiveMeetingDesktopPath;
        _detectionSummary = BuildDetectionSummary();
        _selectedLanguageIndex = IndexOf(current.Language);
        _capturePreview = current.CaptureReturnPreview;
        _useLiveDesktopSwitching = current.SwitchingExperience == SwitchingExperience.Responsive;
        _smoothDesktopTransition = current.SmoothDesktopTransition;
        _showSwitchNotification = current.ShowSwitchNotification;
        _selectedSnapshotRetentionIndex = Math.Max(0, Array.IndexOf(SnapshotRetentionDays, current.AutomaticSnapshotRetentionDays));
        _selectedQuickHotkeyIndex = Enum.IsDefined(current.QuickSwitchHotkey) ? (int)current.QuickSwitchHotkey : 0;
        _selectedMouseGestureIndex = Enum.IsDefined(current.DesktopMouseGesture) ? (int)current.DesktopMouseGesture : 0;
        _confirmQuickSwitch = current.ConfirmQuickSwitch;
        _closeToTray = current.CloseToTray;
        _meetingWallpaperPath = current.MeetingWallpaperPath;

        foreach (var profile in current.Profiles) Profiles.Add(profile);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => CanEditMeetingDesktopPath);
        ApplyProfileCommand = new RelayCommand(ApplyProfile, () => CanEditMeetingDesktopPath);
        SaveQuickAccessCommand = new RelayCommand(SaveQuickAccess, () => !_shell.IsBusy);
        ApplyMeetingDesktopPathCommand = new RelayCommand(() => CommitMeetingDesktopPath(),
            () => CanEditMeetingDesktopPath && HasUnsavedMeetingDesktopPath && !HasValidationProblem);
        RefreshValidation();
    }

    public ObservableCollection<DesktopProfile> Profiles { get; } = new();

    public int SelectedQuickHotkeyIndex
    {
        get => _selectedQuickHotkeyIndex;
        set
        {
            if (!Enum.IsDefined((QuickSwitchHotkey)value)) { OnPropertyChanged(); return; }
            if (SetProperty(ref _selectedQuickHotkeyIndex, value)) AutoSaveQuickAccess();
        }
    }
    public int SelectedMouseGestureIndex
    {
        get => _selectedMouseGestureIndex;
        set
        {
            if (!Enum.IsDefined((DesktopMouseGesture)value)) { OnPropertyChanged(); return; }
            if (SetProperty(ref _selectedMouseGestureIndex, value)) AutoSaveQuickAccess();
        }
    }
    public bool ConfirmQuickSwitch
    {
        get => _confirmQuickSwitch;
        set { if (SetProperty(ref _confirmQuickSwitch, value)) AutoSaveQuickAccess(); }
    }
    public bool CloseToTray
    {
        get => _closeToTray;
        set { if (SetProperty(ref _closeToTray, value)) AutoSaveQuickAccess(); }
    }
    public string QuickAccessStatus { get => _quickAccessStatus; internal set => SetProperty(ref _quickAccessStatus, value); }
    public bool CanEditQuickAccess => !_shell.IsBusy;
    public string QuickSettingsSaveStatus => LocalizationManager.Instance.Format(_quickSaveStatusKey, _quickSaveError);
    public bool StartWithWindows
    {
        get => _shell.StartWithWindows;
        set
        {
            if (_trackQuickAccessChanges) _shell.StartWithWindows = value;
            RefreshStartupSelection();
        }
    }
    public string StartupStatusText => _shell.StartupStatusText;
    public bool CanMigrateStartup => _shell.CanMigrateStartup;
    public ICommand MigrateStartupCommand => _shell.MigrateStartupCommand;
    public string StartupMigrationActionText => LocalizationManager.Instance[StartWithWindows ? "StartupMigrate" : "StartupRemovePrevious"];
    public void RefreshStartupSelection()
    {
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(StartupStatusText));
        OnPropertyChanged(nameof(CanMigrateStartup));
        OnPropertyChanged(nameof(StartupMigrationActionText));
    }
    public ICommand SaveQuickAccessCommand { get; }

    public void BeginTrackingQuickAccessChanges()
    {
        if (_trackQuickAccessChanges) return;
        RestoreQuickAccessChoices();
        _trackQuickAccessChanges = true;
    }

    private void AutoSaveQuickAccess()
    {
        if (_trackQuickAccessChanges) SaveQuickAccess();
    }

    private void RestoreQuickAccessChoices()
    {
        AppSettings current = _shell.CurrentSettings;
        _selectedQuickHotkeyIndex = Enum.IsDefined(current.QuickSwitchHotkey) ? (int)current.QuickSwitchHotkey : 0;
        _selectedMouseGestureIndex = Enum.IsDefined(current.DesktopMouseGesture) ? (int)current.DesktopMouseGesture : 0;
        _confirmQuickSwitch = current.ConfirmQuickSwitch;
        _closeToTray = current.CloseToTray;
        _smoothDesktopTransition = current.SmoothDesktopTransition;
        _showSwitchNotification = current.ShowSwitchNotification;
        _selectedSnapshotRetentionIndex = Math.Max(0, Array.IndexOf(SnapshotRetentionDays, current.AutomaticSnapshotRetentionDays));
        _useLiveDesktopSwitching = current.SwitchingExperience == SwitchingExperience.Responsive;
        foreach (string property in new[] { nameof(SelectedQuickHotkeyIndex), nameof(SelectedMouseGestureIndex),
            nameof(ConfirmQuickSwitch), nameof(CloseToTray), nameof(ShowSwitchNotification), nameof(SelectedSnapshotRetentionIndex), nameof(SmoothDesktopTransition), nameof(UseLiveDesktopSwitching) }) OnPropertyChanged(property);
    }

    private void SetQuickSaveStatus(string key, string error = "")
    {
        _quickSaveStatusKey = key;
        _quickSaveError = error;
        OnPropertyChanged(nameof(QuickSettingsSaveStatus));
        _shell.StatusText = QuickSettingsSaveStatus;
    }

    private void SaveQuickAccess()
    {
        if (_shell.IsBusy)
        {
            RestoreQuickAccessChoices();
            SetQuickSaveStatus("QuickAutoSaveBusy");
            return;
        }
        var key = (QuickSwitchHotkey)_selectedQuickHotkeyIndex;
        var mouse = (DesktopMouseGesture)_selectedMouseGestureIndex;
        if (!Enum.IsDefined(key) || !Enum.IsDefined(mouse)) return;
        AppSettings updated = _settings.Load() with
        {
            QuickSwitchHotkey = key, DesktopMouseGesture = mouse,
            ConfirmQuickSwitch = _confirmQuickSwitch, CloseToTray = _closeToTray,
            SmoothDesktopTransition = _smoothDesktopTransition,
            ShowSwitchNotification = _showSwitchNotification,
            AutomaticSnapshotRetentionDays = SnapshotRetentionDays[_selectedSnapshotRetentionIndex],
            SwitchingExperience = _useLiveDesktopSwitching ? SwitchingExperience.Responsive : SwitchingExperience.Compatibility,
            UseLiveDesktopSwitching = _useLiveDesktopSwitching,
        };
        try { _settings.Save(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RestoreQuickAccessChoices();
            SetQuickSaveStatus("QuickAutoSaveFailed", ex.Message);
            return;
        }
        _shell.OnSettingsChanged(updated with { Language = _shell.CurrentSettings.Language });
        SetQuickSaveStatus("QuickAutoSaved");
    }
    public DesktopProfile? SelectedProfile { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public ICommand SaveProfileCommand { get; }
    public ICommand ApplyProfileCommand { get; }

    // Kept in this owned file so the export adds no changes to the shared resource files.
    public ICommand RecoveryExportCommand => _recoveryExportCommand;
    public string RecoveryExportTitle => RecoveryExportText("RecoveryExportTitle");
    public string RecoveryExportButton => RecoveryExportText("RecoveryExportButton");
    public string RecoveryExportHint => RecoveryExportText("RecoveryExportHint");
    public string RecoveryExportStatus => string.IsNullOrEmpty(_recoveryExportStatusKey)
        ? string.Empty : RecoveryExportText(_recoveryExportStatusKey, _recoveryExportStatusArguments);

    private static string RecoveryExportText(string key, params object?[] arguments) =>
        LocalizationManager.Instance.Format(key, arguments);

    private void SetRecoveryExportStatus(string key, params object?[] arguments)
    {
        _recoveryExportStatusKey = key;
        _recoveryExportStatusArguments = arguments;
        OnPropertyChanged(nameof(RecoveryExportStatus));
        _shell.StatusText = RecoveryExportStatus;
    }

    private async Task RecoveryExportAsync()
    {
        if (_shell.IsBusy) return;
        bool ownsBusy = false;
        try
        {
            // Always start in Documents, independent of the working directory, portable
            // data location, or repository. Do not create folders just to show the dialog.
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrWhiteSpace(documents))
                documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            var dialog = new SaveFileDialog
            {
                Title = RecoveryExportText("RecoveryExportDialogTitle"),
                Filter = RecoveryExportText("RecoveryExportZipFilter"),
                DefaultExt = ".zip",
                AddExtension = true,
                CheckPathExists = true,
                ValidateNames = true,
                OverwritePrompt = true,
                RestoreDirectory = true,
                InitialDirectory = documents,
                FileName = $"MeetingDesktop-safety-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            };
            bool overwriteConfirmed = false;
            // Record existence while the dialog accepts the prompted selection. A file
            // that appears after this must not inherit permission to be overwritten.
            dialog.FileOk += (_, _) => overwriteConfirmed = File.Exists(dialog.FileName);
            if (dialog.ShowDialog(Application.Current.MainWindow) != true || _shell.IsBusy) return;
            string outputPath = dialog.FileName;
            _shell.IsBusy = true;
            ownsBusy = true;
            SetRecoveryExportStatus("RecoveryExportRunning");
            SafetyBackupExportResult result = await Task.Run(() =>
                new SafetyBackupExporter(() => SafetyBackupExporter.CaptureCurrent())
                    .Export(outputPath, overwriteConfirmed));
            if (result.Warnings.Count == 0) SetRecoveryExportStatus("RecoveryExportSucceeded", result.OutputPath);
            else SetRecoveryExportStatus("RecoveryExportPartial", result.Warnings.Count, result.OutputPath);
        }
        catch (Exception ex)
        {
            SetRecoveryExportStatus("RecoveryExportFailed", ex.Message);
        }
        finally
        {
            if (ownsBusy) _shell.IsBusy = false;
        }
    }

    private void SaveProfile()
    {
        if (!CanEditMeetingDesktopPath || string.IsNullOrWhiteSpace(ProfileName)) return;
        var validation = _provisioner.Validate(_meetingDesktopPath);
        ApplyValidation(validation);
        if (!validation.IsAcceptable || validation.NormalizedPath is null) return;
        string name = ProfileName.Trim();
        // A new name creates a version; an existing name is never silently replaced.
        if (Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _shell.StatusText = LocalizationManager.Instance["ProfileDuplicate"];
            return;
        }
        var profile = new DesktopProfile(name, validation.NormalizedPath, _meetingWallpaperPath);
        var updated = _shell.CurrentSettings with { Profiles = Profiles.Append(profile).ToArray() };
        try { _settings.Save(updated); Profiles.Add(profile); _shell.OnSettingsChanged(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _shell.StatusText = LocalizationManager.Instance["SettingsSaveFailed"]; }
    }

    private void ApplyProfile()
    {
        if (!CanEditMeetingDesktopPath || SelectedProfile is null) return;
        MeetingDesktopPath = SelectedProfile.Folder;
        _meetingWallpaperPath = SelectedProfile.Wallpaper;
        OnPropertyChanged(nameof(MeetingWallpaperPath));
        OnPropertyChanged(nameof(HasMeetingWallpaper));
        OnPropertyChanged(nameof(WallpaperDescription));
        Save();
    }

    // ---------------------------------------------------------------
    // Language
    // ---------------------------------------------------------------

    public string LanguageAuto => LocalizationManager.Instance["SettingsLanguageAuto"];

    public int SelectedThemeIndex
    {
        get => _shell.CurrentSettings.Theme switch { ThemePreference.System => 0, ThemePreference.Light => 1, _ => 2 };
        set
        {
            if (!_trackUserLanguageChanges || value is < 0 or > 2 || value == SelectedThemeIndex) return;
            _shell.ChangeTheme(value switch { 0 => ThemePreference.System, 1 => ThemePreference.Light, _ => ThemePreference.Dark });
        }
    }

    public void RefreshThemeSelection() => OnPropertyChanged(nameof(SelectedThemeIndex));

    public string LanguageEnglish => LocalizationManager.Instance["SettingsLanguageEnglish"];

    public string LanguageChinese => LocalizationManager.Instance["SettingsLanguageChinese"];

    public int SelectedLanguageIndex
    {
        get => _selectedLanguageIndex;
        set
        {
            if (!SetProperty(ref _selectedLanguageIndex, value) || !IsKnownLanguage(value))
            {
                return;
            }

            // Ignore anything that arrives before the view has finished
            // loading. See BeginTrackingUserLanguageChanges - the setter has a
            // side effect, and WPF pushes values through a two-way binding
            // while it builds the visual tree, so without this guard the
            // control's own initialisation rewrites the stored preference.
            if (!_trackUserLanguageChanges)
            {
                return;
            }

            // Applied now, persisted on Save. Going through the shell rather
            // than the localization manager alone is what refreshes the strings
            // the other pages cached in their own fields.
            _shell.OnSettingsChanged(_shell.CurrentSettings with { Language = Languages[value] });
        }
    }

    /// <summary>
    /// Starts honouring changes to the language picker.
    /// </summary>
    /// <remarks>
    /// Called once the view has loaded. This is not defensive padding: before
    /// it was added, opening the app with a stored preference of English
    /// silently rewrote it to Chinese, because the combo box pushed its own
    /// initialisation value through the two-way binding and the setter
    /// applied it. The user had done nothing.
    /// </remarks>
    public void BeginTrackingUserLanguageChanges() => _trackUserLanguageChanges = true;

    /// <summary>
    /// Re-reads the language preference after something else changed it.
    /// </summary>
    /// <remarks>
    /// Called when the sidebar switch is used, so this picker does not go on
    /// displaying the previous choice. It writes the backing field and raises
    /// the notification rather than assigning the property, because the setter
    /// would push the change straight back at the shell.
    /// </remarks>
    public void SyncLanguageFromShell()
    {
        int index = IndexOf(_shell.CurrentSettings.Language);

        if (_selectedLanguageIndex == index)
        {
            return;
        }

        _selectedLanguageIndex = index;
        OnPropertyChanged(nameof(SelectedLanguageIndex));
    }

    // ---------------------------------------------------------------
    // Meeting desktop folder
    // ---------------------------------------------------------------

    /// <summary>The editable folder; a valid draft is committed on completion or navigation.</summary>
    public string MeetingDesktopPath
    {
        get => _meetingDesktopPath;
        set
        {
            if (SetProperty(ref _meetingDesktopPath, value))
            {
                _folderSaveError = "";
                RefreshValidation();
                RefreshMeetingDesktopSaveStatus();
            }
        }
    }

    /// <summary>
    /// The folder the app would use right now: the stored override, or the
    /// built-in default when nothing has been stored.
    /// </summary>
    /// <remarks>
    /// Shown next to the box because the two differ while an edit is in
    /// progress, and "where will my desktop actually go" is the question this
    /// page exists to answer.
    /// </remarks>
    public string EffectiveMeetingDesktopPath =>
        _paths.ResolveMeetingDesktopDirectory(_shell.CurrentSettings);

    public bool HasUnsavedMeetingDesktopPath => !string.Equals(
        _validation?.NormalizedPath ?? _meetingDesktopPath, EffectiveMeetingDesktopPath, StringComparison.OrdinalIgnoreCase);

    public string MeetingDesktopSaveStatus => !string.IsNullOrEmpty(_folderSaveError)
        ? LocalizationManager.Instance.Format("SettingsFolderSaveFailed", _folderSaveError)
        : LocalizationManager.Instance[HasUnsavedMeetingDesktopPath ? "SettingsFolderPending" : "SettingsFolderReady"];

    public ICommand ApplyMeetingDesktopPathCommand { get; }

    internal void RefreshMeetingDesktopSaveStatus()
    {
        OnPropertyChanged(nameof(EffectiveMeetingDesktopPath));
        OnPropertyChanged(nameof(HasUnsavedMeetingDesktopPath));
        OnPropertyChanged(nameof(MeetingDesktopSaveStatus));
        (ApplyMeetingDesktopPathCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public bool CommitMeetingDesktopPath()
    {
        if (!HasUnsavedMeetingDesktopPath) return true;
        if (!CanEditMeetingDesktopPath) return false;
        var validation = _provisioner.Validate(_meetingDesktopPath);
        ApplyValidation(validation);
        if (!validation.IsAcceptable || validation.NormalizedPath is null)
        {
            _shell.StatusText = ValidationMessage;
            RefreshMeetingDesktopSaveStatus();
            return false;
        }
        AppSettings updated = _shell.CurrentSettings with { MeetingDesktopPath = validation.NormalizedPath };
        try { _settings.Save(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _folderSaveError = ex.Message;
            RefreshMeetingDesktopSaveStatus();
            _shell.StatusText = MeetingDesktopSaveStatus;
            return false;
        }
        _folderSaveError = "";
        MeetingDesktopPath = validation.NormalizedPath;
        _shell.OnSettingsChanged(updated);
        _shell.StatusText = LocalizationManager.Instance["SettingsFolderReady"];
        return true;
    }

    /// <summary>
    /// Whether the folder may be edited at all.
    /// </summary>
    /// <remarks>
    /// Refused while the desktop points at a meeting folder. The swap recorded
    /// the path it redirected to, and changing that path mid-swap would leave
    /// Restore looking for a folder nothing points at any more, with the user's
    /// real desktop still hidden behind it.
    /// </remarks>
    public bool CanEditMeetingDesktopPath =>
        !_shell.IsBusy && _shell.CanPrepare;

    /// <summary>Everything found wrong with the path as typed.</summary>
    /// <remarks>
    /// Blocking problems and warnings are in here together, because both are
    /// worth reading; <see cref="HasValidationProblem"/> is what separates them.
    /// </remarks>
    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    /// <summary>True when the path cannot be used. Warnings do not set this.</summary>
    public bool HasValidationProblem
    {
        get => _hasValidationProblem;
        private set => SetProperty(ref _hasValidationProblem, value);
    }

    // ---------------------------------------------------------------
    // Data folder
    // ---------------------------------------------------------------

    public string DataDirectory => _paths.DataDirectory;

    public bool IsPortable => _paths.IsPortable;

    /// <summary>The desktop folder as detected, for context beside the setting.</summary>
    public string DetectionSummary => _detectionSummary;

    // ---------------------------------------------------------------
    // Options
    // ---------------------------------------------------------------

    public bool CapturePreview
    {
        get => _capturePreview;
        set => SetProperty(ref _capturePreview, value);
    }

    public bool UseLiveDesktopSwitching
    {
        get => _useLiveDesktopSwitching;
        set { if (SetProperty(ref _useLiveDesktopSwitching, value)) AutoSaveQuickAccess(); }
    }

    internal void RefreshSwitchingExperience()
    {
        _useLiveDesktopSwitching = _shell.CurrentSettings.SwitchingExperience == SwitchingExperience.Responsive;
        OnPropertyChanged(nameof(UseLiveDesktopSwitching));
    }

    public bool SmoothDesktopTransition
    {
        get => _smoothDesktopTransition;
        set { if (SetProperty(ref _smoothDesktopTransition, value)) AutoSaveQuickAccess(); }
    }

    public bool ShowSwitchNotification
    {
        get => _showSwitchNotification;
        set { if (SetProperty(ref _showSwitchNotification, value)) AutoSaveQuickAccess(); }
    }

    public int SelectedSnapshotRetentionIndex
    {
        get => _selectedSnapshotRetentionIndex;
        set
        {
            if (value < 0 || value >= SnapshotRetentionDays.Length) { OnPropertyChanged(); return; }
            if (SetProperty(ref _selectedSnapshotRetentionIndex, value)) AutoSaveQuickAccess();
        }
    }

    // ---------------------------------------------------------------
    // Wallpaper
    // ---------------------------------------------------------------

    /// <summary>
    /// Shown while the meeting desktop is active; null keeps the user's own.
    /// </summary>
    /// <remarks>
    /// Deliberately nullable rather than defaulting to the current wallpaper's
    /// path. "Follow whatever I have" and "use this particular image" are
    /// different intentions, and only the second one should cause the swap to
    /// change anything - and therefore only the second one needs undoing.
    /// </remarks>
    public string? MeetingWallpaperPath => _meetingWallpaperPath;

    public bool HasMeetingWallpaper => !string.IsNullOrWhiteSpace(_meetingWallpaperPath);

    /// <summary>The chosen image, or a line saying the current one is kept.</summary>
    public string WallpaperDescription =>
        string.IsNullOrWhiteSpace(_meetingWallpaperPath)
            ? LocalizationManager.Instance["SettingsWallpaperCurrent"]
            : Path.GetFileName(_meetingWallpaperPath);

    public ICommand ChooseWallpaperCommand => _chooseWallpaperCommand;

    public ICommand UseCurrentWallpaperCommand => _useCurrentWallpaperCommand;

    /// <summary>
    /// Picks an image and copies it into the app's own data folder.
    /// </summary>
    /// <remarks>
    /// The copy is the point. A wallpaper chosen from a downloads folder, a
    /// camera card or a chat client's cache is a path that can stop existing,
    /// and a wallpaper pointing at a missing file shows as a black screen -
    /// discovered on the projector, in front of an audience. Importing makes
    /// the setting as durable as the layout snapshot.
    /// </remarks>
    private void ChooseWallpaper()
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationManager.Instance["SettingsWallpaperChooseTitle"],
            Filter = LocalizationManager.Instance["SettingsWallpaperImages"] +
                     "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|" +
                     LocalizationManager.Instance["CommonAllFiles"] + "|*.*",
            CheckFileExists = true,
        };

        Window? owner = Application.Current?.MainWindow;
        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);

        if (accepted != true)
        {
            return;
        }

        string assets = Path.Combine(_paths.DataDirectory, "assets");
        string? imported = _wallpapers.Import(dialog.FileName, assets);

        if (imported is null)
        {
            ValidationMessage = LocalizationManager.Instance["SettingsWallpaperCopyFailed"];
            HasValidationProblem = true;
            return;
        }

        _meetingWallpaperPath = imported;
        NotifyWallpaperChanged();
    }

    private void UseCurrentWallpaper()
    {
        _meetingWallpaperPath = null;
        NotifyWallpaperChanged();
    }

    private void NotifyWallpaperChanged()
    {
        OnPropertyChanged(nameof(MeetingWallpaperPath));
        OnPropertyChanged(nameof(HasMeetingWallpaper));
        OnPropertyChanged(nameof(WallpaperDescription));
    }

    // ---------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------

    public ICommand BrowseCommand => _browseCommand;

    public ICommand UseDefaultCommand => _useDefaultCommand;

    public ICommand SaveCommand => _saveCommand;

    /// <summary>
    /// Picks a folder through the shell's own browse dialog.
    /// </summary>
    /// <remarks>
    /// The dialog is modal to the main window when there is one. Without an
    /// owner it is not modal, and the user can start a Prepare from the page
    /// behind it while the folder is still being chosen.
    /// </remarks>
    private void Browse()
    {
        var dialog = new OpenFolderDialog
        {
            // The folder's own name reads better here than "Browse…" does.
            Title = LocalizationManager.Instance["SettingsMeetingDesktopPath"],

            // A single folder, so the dialog reports it through FolderName.
            Multiselect = false,
        };

        if (Directory.Exists(_meetingDesktopPath))
        {
            dialog.InitialDirectory = _meetingDesktopPath;
        }

        Window? owner = Application.Current?.MainWindow;
        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);

        if (accepted == true)
        {
            MeetingDesktopPath = dialog.FolderName;
            CommitMeetingDesktopPath();
        }
    }

    private void UseDefault()
    {
        // The literal default rather than a cleared setting. A stored path is
        // part of how a later run recognises a desktop pointing at its own
        // meeting folder; left null, that swap would look like one this app
        // never made, and would be reported as an adopted desktop instead.
        MeetingDesktopPath = _paths.MeetingDesktopDirectory;
        CommitMeetingDesktopPath();
    }

    private void Save()
    {
        if (!CanEditMeetingDesktopPath) return;
        PathValidation validation = _provisioner.Validate(_meetingDesktopPath);
        ApplyValidation(validation);

        if (!validation.IsAcceptable)
        {
            // The message now says why. A warning, by contrast, is not a reason
            // to refuse the save: it is a sentence the user has read and gone
            // ahead past, which is the whole point of it not being an error.
            return;
        }

        if (validation.NormalizedPath is { } normalized)
        {
            // Show what was stored. The box can hold an unexpanded or
            // over-long spelling of the same folder, and leaving the user
            // looking at text that differs from the setting is worse than a
            // small correction.
            MeetingDesktopPath = normalized;
        }

        AppSettings updated = _shell.CurrentSettings with
        {
            MeetingDesktopPath = validation.NormalizedPath,
            Language = CurrentLanguage,
            CaptureReturnPreview = _capturePreview,
            UseLiveDesktopSwitching = _useLiveDesktopSwitching,
            SwitchingExperience = _useLiveDesktopSwitching ? SwitchingExperience.Responsive : SwitchingExperience.Compatibility,
            SmoothDesktopTransition = _smoothDesktopTransition,
            ShowSwitchNotification = _showSwitchNotification,
            AutomaticSnapshotRetentionDays = SnapshotRetentionDays[_selectedSnapshotRetentionIndex],
            MeetingWallpaperPath = _meetingWallpaperPath,
        };

        try
        {
            _settings.Save(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A save that silently did nothing would lose these choices at the
            // next sign-in with nothing on screen to explain it.
            _shell.StatusText = LocalizationManager.Instance.Format("SettingsSaveFailed", ex.Message);
            _folderSaveError = ex.Message;
            RefreshMeetingDesktopSaveStatus();
            return;
        }

        _folderSaveError = "";
        _shell.OnSettingsChanged(updated);
        _shell.StatusText = LocalizationManager.Instance["SettingsSaved"];
    }

    // ---------------------------------------------------------------
    // Page lifecycle
    // ---------------------------------------------------------------

    /// <summary>Re-reads the machine state this page reports but does not own.</summary>
    public void OnActivated()
    {
        // Another page can have swapped or restored the desktop since this page
        // was last shown, and that decides whether the folder may be edited.
        OnPropertyChanged(nameof(CanEditMeetingDesktopPath));
        OnPropertyChanged(nameof(EffectiveMeetingDesktopPath));
        RaiseCanExecuteChanged();

        _detectionSummary = BuildDetectionSummary();
        OnPropertyChanged(nameof(DetectionSummary));

        RefreshValidation();
    }

    /// <summary>Re-reads the text this page assembled from resources.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(QuickSettingsSaveStatus));
        RefreshMeetingDesktopSaveStatus();
        OnPropertyChanged(nameof(RecoveryExportTitle));
        OnPropertyChanged(nameof(RecoveryExportButton));
        OnPropertyChanged(nameof(RecoveryExportHint));
        OnPropertyChanged(nameof(RecoveryExportStatus));
        // Both of these are put together out of resource strings, so a language
        // change has to rebuild them; everything else here is a path or a flag.
        _detectionSummary = BuildDetectionSummary();
        OnPropertyChanged(nameof(DetectionSummary));

        _validationMessage = BuildIssueMessage(_validation);
        OnPropertyChanged(nameof(ValidationMessage));
    }

    /// <summary>Re-queries the buttons, for a caller that has moved the session state.</summary>
    public void RaiseCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanEditQuickAccess));
        OnPropertyChanged(nameof(CanEditMeetingDesktopPath));
        _browseCommand.RaiseCanExecuteChanged();
        _useDefaultCommand.RaiseCanExecuteChanged();
        _saveCommand.RaiseCanExecuteChanged();
        _recoveryExportCommand.RaiseCanExecuteChanged();
        (SaveQuickAccessCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplyMeetingDesktopPathCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplyProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ---------------------------------------------------------------
    // Validation
    // ---------------------------------------------------------------

    private void RefreshValidation()
    {
        if (!CanEditMeetingDesktopPath)
        {
            // Deliberately no verdict while the desktop is swapped. Detection
            // then reports the meeting folder as the desktop, so the overlap
            // check would reject the very path the app is running on - and the
            // page cannot act on the answer anyway.
            ApplyValidation(null);
            return;
        }

        ApplyValidation(_provisioner.Validate(_meetingDesktopPath));
    }

    private void ApplyValidation(PathValidation? validation)
    {
        _validation = validation;
        ValidationMessage = BuildIssueMessage(validation);

        // IsAcceptable is false only for blocking issues. Collapsing the two
        // kinds into one flag here would either block a path that is merely on a
        // removable drive, or wave through one that is the real desktop.
        HasValidationProblem = validation is { IsAcceptable: false };

        _saveCommand.RaiseCanExecuteChanged();
    }

    private static string BuildIssueMessage(PathValidation? validation)
    {
        if (validation is null || validation.Issues.Count == 0)
        {
            return string.Empty;
        }

        // One line per finding: validation collects everything rather than
        // stopping at the first, so the folder can be put right in one edit.
        return string.Join(
            Environment.NewLine,
            validation.Issues.Select(issue => LocalizationManager.Instance[IssueKey(issue)]));
    }

    /// <summary>Resource key for a finding.</summary>
    /// <remarks>
    /// Enum member names match the key suffixes exactly - PathIssue.NotWritable
    /// is PathIssueNotWritable - so there is no table to keep in step. An issue
    /// this build has no wording for shows up as <c>!PathIssueFoo!</c>, which is
    /// visible, rather than as a blank line that is not.
    /// </remarks>
    private static string IssueKey(PathIssue issue) => "PathIssue" + issue;

    // ---------------------------------------------------------------
    // Detection
    // ---------------------------------------------------------------

    private string BuildDetectionSummary()
    {
        if (TryDetect() is not { ResolvedPath: { } resolved } location)
        {
            // Also the answer when the sources disagree. Saying so is better
            // than showing one of them and implying the others agreed.
            return LocalizationManager.Instance["CommonNotAvailable"];
        }

        // OneDrive is called out because a redirected desktop is exactly the
        // case where the folder the user has in mind and the one Windows
        // reports are not the same folder.
        return location.IsOneDriveRedirected
            ? $"{resolved}  ·  {LocalizationManager.Instance["DiagnosticsOneDrive"]}"
            : resolved;
    }

    /// <summary>
    /// The real desktop as the overlap check sees it, or null when it cannot be
    /// told apart from the meeting folder.
    /// </summary>
    private string? DetectRealDesktop() => TryDetect()?.ResolvedPath;

    private DesktopLocation? TryDetect()
    {
        try
        {
            // The folder actually in use is passed so candidates pointing at it
            // are annotated, exactly as the shell does when it resolves the
            // state. AppPaths resolves it - the raw setting is null on a fresh
            // install, and that is the common case, not an edge one.
            return _locations.Detect(_paths.ResolveMeetingDesktopDirectory(_shell.CurrentSettings));
        }
        catch (Exception)
        {
            // Detection reads the registry and probes the disk. It is a guard,
            // not a precondition: an unreadable hive must not take out the page
            // that exists to let the user work around it.
            return null;
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private LanguagePreference CurrentLanguage =>
        IsKnownLanguage(_selectedLanguageIndex) ? Languages[_selectedLanguageIndex] : LanguagePreference.Auto;

    private static bool IsKnownLanguage(int index) => index >= 0 && index < Languages.Length;

    /// <summary>Picker index for a stored preference.</summary>
    private static int IndexOf(LanguagePreference preference)
    {
        // A preference this build does not know - a file from a newer version -
        // reads as "no preference" rather than as whatever sits at index 0.
        int index = Array.IndexOf(Languages, preference);
        return index < 0 ? Array.IndexOf(Languages, LanguagePreference.Auto) : index;
    }
}
