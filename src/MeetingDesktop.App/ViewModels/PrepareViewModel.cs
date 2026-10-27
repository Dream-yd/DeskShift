using System.IO;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeetingDesktop.App.Localization;
using MeetingDesktop.App.Services;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.App.ViewModels;

/// <summary>
/// The page shown before a swap: which folder the app thinks the desktop is,
/// what is on it right now, and the one action that redirects it.
/// </summary>
/// <remarks>
/// <para>
/// Everything on this page is read-only until the user confirms. That is the
/// point of it existing: the two facts a presenter cannot check afterwards -
/// which folder is really the desktop, and what is about to vanish from it -
/// are on screen before anything is written.
/// </para>
/// <para>
/// Detection and the preview are both re-read on activation rather than cached,
/// because another page can move the meeting folder or put the desktop back,
/// and either one changes what this page should be saying.
/// </para>
/// </remarks>
public sealed class PrepareViewModel : ObservableObject, IPageViewModel, IDisposable
{
    /// <summary>
    /// Shared prefix of the per-kind error strings, so a new
    /// <see cref="InteropFailureKind"/> only needs a name, not a lookup table.
    /// </summary>
    private const string ErrorKeyPrefix = "Error";

    private readonly AppPaths _paths;
    private readonly LayoutStore _layouts;
    private readonly SessionStore _sessions;
    private readonly DesktopLocationService _locations;
    private readonly IElevationBroker _elevation;
    private readonly MainViewModel _shell;
    private readonly DesktopPreviewService _preview = new();

    private DesktopLocation? _location;
    private string _meetingDesktopPath = string.Empty;
    private string _detectedDesktopPath = string.Empty;
    private string _detectionNote = string.Empty;
    private bool _hasDetectionProblem;
    private string _previewCaption = string.Empty;
    private ImageSource? _previewImage;
    private string _errorKey = string.Empty;
    private string _errorMessage = string.Empty;
    private string _errorDetails = string.Empty;
    private bool _hasError;
    private bool _viewVerificationFailure;
    private readonly DailySwitchPageAction _dailySwitch;
    private readonly LatestRefresh<DetailRequest, TargetDetails> _details;
    private string? _detailsTarget;
    private bool _disposed;

    public PrepareViewModel(
        AppPaths paths,
        LayoutStore layouts,
        SessionStore sessions,
        DesktopLocationService locations,
        IElevationBroker elevation,
        MainViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(elevation);
        ArgumentNullException.ThrowIfNull(shell);

        _paths = paths;
        _layouts = layouts;
        _sessions = sessions;
        _locations = locations;
        _elevation = elevation;
        _shell = shell;
        _details = new(ReadTargetDetails, ApplyTargetDetails);

        OpenCurrentFolderCommand = new RelayCommand(() => OpenFolder(_location?.ResolvedPath));
        OpenTargetFolderCommand = new RelayCommand(() => OpenFolder(TargetDesktopPath));
        PrepareCommand = new AsyncRelayCommand(PrepareAsync, CanPrepare);
        _dailySwitch = new DailySwitchPageAction(() => _shell.IsBusy,
            () => { _shell.RefreshSessionState(); return _shell.DailySwitch; },
            PrepareAsync, () => _shell.Restore.ReturnFromSwitchPageAsync(),
            () => _shell.ReviewRecoveryCommand.Execute(null));
        SwitchCommand = new AsyncRelayCommand(SwitchAsync, () => !_shell.IsBusy);
        RefreshPreviewCommand = new AsyncRelayCommand(RefreshPreviewAsync, () => !_shell.IsBusy);
        UseCompatibilityCommand = new RelayCommand(UseCompatibility, CanUseCompatibility);

        // The shell owns both the session state and the busy flag. Neither is
        // this page's to set, and both decide what it may enable, so it listens
        // rather than duplicating them.
        _shell.PropertyChanged += OnShellPropertyChanged;

        RefreshDetection();
        RefreshPreviewCaption();
        _ = RefreshPreviewAsync();
    }

    public string ReadinessSummary { get; private set; } = string.Empty;
    public string ReadinessWarning { get; private set; } = string.Empty;
    public string PreviewMonitorNotice { get; private set; } = string.Empty;
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();
    private MonitorInfo? _selectedMonitor;
    private bool _updatingMonitors;
    private int _previewGeneration;
    public MonitorInfo? SelectedMonitor
    {
        get => _selectedMonitor;
        set { if (SetProperty(ref _selectedMonitor, value) && !_updatingMonitors) _ = RefreshPreviewAsync(); }
    }
    public ICommand OpenCurrentFolderCommand { get; }
    public ICommand OpenTargetFolderCommand { get; }
    public ICommand UseCompatibilityCommand { get; }
    public ICommand ReviewSwitchRecoveryCommand => _shell.ReviewRecoveryCommand;
    public bool ShowCompatibilityFallback => _viewVerificationFailure && !_sessions.Exists &&
        _shell.CurrentSettings.SwitchingExperience == SwitchingExperience.Responsive;
    public bool ShowSwitchRecovery => HasError && _sessions.Exists;
    public ObservableCollection<string> TargetFiles { get; } = new();
    public ImageSource? TargetPreview { get; private set; }
    public bool IsReturningToOriginal => _shell.CanReturnToOriginal;
    public string? TargetDesktopPath => _shell.DailySwitch.TargetPath;
    public string DirectionLabel => LocalizationManager.Instance[IsReturningToOriginal ? "DailySwitchReturnTitle" :
        _shell.CanPrepare ? "DailySwitchHomeTitle" : "RecoveryReviewTitle"];
    public string TargetLabel => LocalizationManager.Instance[IsReturningToOriginal ? "RestoreOriginalDesktopHeading" :
        _shell.CanPrepare ? "SettingsMeetingDesktopPath" : "CommonNotAvailable"];
    public string SwitchActionLabel => LocalizationManager.Instance[IsReturningToOriginal ? "DailySwitchReturnAction" :
        _shell.CanPrepare ? "PrepareAction" : "RecoveryReviewAction"];
    public string SwitchDescription => LocalizationManager.Instance[IsReturningToOriginal ? "DailySwitchReturnDescription" :
        _shell.CanPrepare ? "DailySwitchHomeDescription" : "RecoveryPendingMessage"];
    public bool NeedsRecoveryReview => _shell.DailySwitch.Direction == DailySwitchDirection.Recovery;
    public ICommand SwitchCommand { get; }

    private void RefreshDirectionText()
    {
        foreach (string name in new[] { nameof(IsReturningToOriginal), nameof(TargetDesktopPath), nameof(DirectionLabel),
            nameof(TargetLabel), nameof(SwitchActionLabel), nameof(SwitchDescription), nameof(NeedsRecoveryReview) })
            OnPropertyChanged(name);
    }

    private async Task SwitchAsync()
    {
        DailySwitchPlan displayed = _shell.DailySwitch;
        try { await _dailySwitch.ExecuteAsync(displayed); }
        catch (Exception ex) { ReportFailure(ex); }
    }

    private static ImageSource? BuildTargetPreview(DetailRequest request, IReadOnlyList<string> names)
    {
        if (request.Plan.TargetPath is null) return null;
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(228, 236, 248)), null, new Rect(0, 0, 640, 360));
            // A return preview belongs to the recorded original, never the alternate wallpaper in settings.
            string? wallpaper = ResolveTargetWallpaper(request.Plan, request.AlternateWallpaper, File.Exists);
            if (request.Plan.Direction != DailySwitchDirection.ToOriginal && wallpaper is null) wallpaper = new WallpaperService().Read().Path;
            try
            {
                if (!string.IsNullOrWhiteSpace(wallpaper) && File.Exists(wallpaper))
                {
                    using var stream = File.OpenRead(wallpaper);
                    var image = new BitmapImage();
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 640; image.StreamSource = stream; image.EndInit(); image.Freeze();
                    context.DrawImage(image, new Rect(0, 0, 640, 360));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException) { }
            int index = 0;
            foreach (string name in names.Take(24))
            {
                int col = index / 6, row = index % 6;
                double x = 16 + col * 120, y = 14 + row * 54;
                context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), null, new Rect(x, y, 28, 25), 5, 5);
                var label = new FormattedText(name.Length > 16 ? name[..15] + "…" : name,
                    request.Culture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 10, Brushes.White, 1);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(155, 20, 28, 45)), null,
                    new Rect(x - 3, y + 26, Math.Min(112, label.Width + 6), 16));
                context.DrawText(label, new Point(x, y + 27));
                index++;
            }
        }
        drawing.Freeze();
        var preview = new DrawingImage(drawing); preview.Freeze();
        return preview;
    }

    internal static string? ResolveTargetWallpaper(DailySwitchPlan plan, string? alternateWallpaper, Func<string, bool> fileExists)
    {
        if (plan.Direction == DailySwitchDirection.ToAlternate) return alternateWallpaper;
        if (plan.Direction != DailySwitchDirection.ToOriginal) return null;
        return plan.Session?.WallpaperBackupPath is { } backup && fileExists(backup)
            ? backup : plan.Session?.WallpaperAtSwap?.Path;
    }
    public string PublicResidualSummary { get; private set; } = string.Empty;
    private void OpenFolder(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else _shell.StatusText = LocalizationManager.Instance["CommonNotAvailable"];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { _shell.StatusText = LocalizationManager.Instance["CommonNotAvailable"]; }
    }

    // ---------------------------------------------------------------
    // What the app detected
    // ---------------------------------------------------------------

    /// <summary>Folder the desktop is redirected to during a presentation.</summary>
    public string MeetingDesktopPath
    {
        get => _meetingDesktopPath;
        private set => SetProperty(ref _meetingDesktopPath, value);
    }

    /// <summary>The real desktop folder, or a localized "not available".</summary>
    public string DetectedDesktopPath
    {
        get => _detectedDesktopPath;
        private set => SetProperty(ref _detectedDesktopPath, value);
    }

    /// <summary>How confident the resolution is, in one line.</summary>
    public string DetectionNote
    {
        get => _detectionNote;
        private set => SetProperty(ref _detectionNote, value);
    }

    /// <summary>
    /// True when the desktop could not be resolved, which is the one condition
    /// that stops the swap.
    /// </summary>
    public bool HasDetectionProblem
    {
        get => _hasDetectionProblem;
        private set => SetProperty(ref _hasDetectionProblem, value);
    }

    // ---------------------------------------------------------------
    // Preview
    // ---------------------------------------------------------------

    public string PreviewCaption
    {
        get => _previewCaption;
        private set => SetProperty(ref _previewCaption, value);
    }

    /// <summary>
    /// The captured desktop, or null when the capture failed.
    /// </summary>
    /// <remarks>
    /// Always frozen. The capture is built on a worker thread - it is GDI
    /// interop and has no business running on the UI thread - and WPF refuses
    /// to hand an unfrozen bitmap from one thread to another.
    /// </remarks>
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (SetProperty(ref _previewImage, value))
            {
                OnPropertyChanged(nameof(HasPreview));
            }
        }
    }

    public bool HasPreview => _previewImage is not null;

    // ---------------------------------------------------------------
    // Last failure
    // ---------------------------------------------------------------

    /// <summary>True when the last run failed in a way the user must read.</summary>
    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    /// <summary>The localized headline of the last failure.</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>
    /// The raw, developer-facing message behind <see cref="ErrorMessage"/>.
    /// </summary>
    /// <remarks>
    /// English and unlocalized on purpose. It names the Win32 lane that failed
    /// and is meant to be pasted into a bug report, so paraphrasing it into the
    /// current language would remove the only part a maintainer can search for.
    /// </remarks>
    public string ErrorDetails
    {
        get => _errorDetails;
        private set => SetProperty(ref _errorDetails, value);
    }

    // ---------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------

    public ICommand PrepareCommand { get; }

    public ICommand RefreshPreviewCommand { get; }

    /// <summary>Mirrors the shell's busy flag, which owns the busy state.</summary>
    public bool IsBusy => _shell.IsBusy;

    // ---------------------------------------------------------------
    // Activation
    // ---------------------------------------------------------------

    /// <summary>Re-reads everything a previous visit or another page could have changed.</summary>
    public void OnActivated()
    {
        if (!_shell.IsBusy) _shell.RefreshSessionState();
        RefreshDetection();
        RefreshPreviewCaption();
        RaiseCanExecuteChanged();
        _ = RefreshPreviewAsync();
    }

    internal void RefreshSettings()
    {
        RefreshDetection();
        RefreshPreviewCaption();
    }

    /// <summary>Re-resolves every sentence this page cached in a field.</summary>
    public void RefreshLocalizedText()
    {
        RefreshDetectionText();
        RefreshPreviewCaption();
        RefreshErrorMessage();
        RefreshDirectionText();
    }

    public void RaiseCanExecuteChanged()
    {
        (PrepareCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SwitchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshPreviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UseCompatibilityCommand as RelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ShowCompatibilityFallback));
        OnPropertyChanged(nameof(ShowSwitchRecovery));
        if (_viewVerificationFailure) RefreshErrorMessage();
    }

    // ---------------------------------------------------------------
    // The operation
    // ---------------------------------------------------------------

    private bool CanPrepare() =>
        !_shell.IsBusy &&
        _shell.CanPrepare &&
        _location is { IsUsable: true };

    private Task PrepareAsync() => PrepareAsync(false);

    internal Task PrepareFromShortcutAsync()
    {
        if (!_shell.EnsureSwitchPermission()) return Task.CompletedTask;
        MeetingDesktopPath = _paths.ResolveMeetingDesktopDirectory(_shell.CurrentSettings);
        _location = _locations.Detect(MeetingDesktopPath);
        HasDetectionProblem = !_location.IsUsable;
        RefreshDetectionText();
        if (!CanPrepare())
        {
            _shell.StatusText = LocalizationManager.Instance["QuickSwitchFailed"];
            _shell.Notify(_shell.StatusText);
            return Task.CompletedTask;
        }
        return PrepareAsync(true);
    }

    private async Task PrepareAsync(bool quick)
    {
        if (_shell.IsBusy) return;
        DailySwitchPlan displayed = _shell.DailySwitch;
        _shell.RefreshSessionState();
        if (!_shell.CanPrepare || !displayed.Matches(_shell.DailySwitch))
        {
            _shell.ReviewRecoveryCommand.Execute(null);
            return;
        }
        if (!_shell.EnsureSwitchPermission()) return;
        DailySwitchPlan approved = _shell.DailySwitch;
        MeetingDesktopPath = approved.TargetPath!;
        bool completed = false;
        _shell.IsBusy = true;
        try
        {
            if (_shell.CurrentSettings.ConfirmQuickSwitch && !ConfirmPrepare()) return;
            // A modal confirmation pumps messages. Revalidate its exact direction and target afterwards.
            _shell.RefreshSessionState();
            if (!approved.Matches(_shell.DailySwitch))
            {
                _shell.StatusText = LocalizationManager.Instance["RecoveryReviewTitle"];
                return;
            }
            ClearError();
            _shell.BeginSwitchTiming();
            if (quick) _shell.SetQuickSwitchVisibility(false);
            DesktopSwitchMode mode = _shell.CurrentSettings.SwitchingExperience == SwitchingExperience.Responsive
                ? DesktopSwitchMode.Live : DesktopSwitchMode.RestartExplorer;
            await using var transition = new SwitchFeedback(_shell.CurrentSettings, mode, () => _shell.StatusText);
            var workflow = new PrepareWorkflow(_paths, _layouts, _sessions, _locations, _elevation);

            PrepareResult result = await workflow.RunAsync(
                new PrepareRequest(
                    MeetingDesktopPath,
                    SwitchFeedback.ShouldCapturePreview(mode, _shell.CurrentSettings.CaptureReturnPreview),

                    // Null unless the user picked one, in which case the swap
                    // leaves their wallpaper alone and restore has nothing to
                    // undo.
                    _shell.CurrentSettings.MeetingWallpaperPath,
                    mode, _shell.CurrentSettings.AutomaticSnapshotRetentionDays),
                _shell.CreateProgress(), beforeDesktopChange: transition.ShowAsync,
                desktopReady: () => { _shell.MarkDesktopReady(); transition.DesktopReady(); });

            _shell.StatusText = LocalizationManager.Instance.Format(
                "ResultPrepareDone",
                result.IconCount,
                result.MeetingDesktopPath);
            if (result.WallpaperBackupUnavailable)
            {
                ReadinessWarning = LocalizationManager.Instance["PreflightWallpaperBackupUnavailable"];
                OnPropertyChanged(nameof(ReadinessWarning));
                _shell.StatusText += " " + ReadinessWarning;
            }
            transition.Complete();
            completed = true;
        }
        catch (Exception ex)
        {
            // Nothing may escape: this runs from an async void command handler,
            // where an unhandled exception ends the process - after the desktop
            // may already have been redirected.
            ReportFailure(ex);
            if (quick)
            {
                _shell.StatusText = ErrorMessage;
                _shell.Notify(ErrorMessage);
            }
        }
        finally
        {
            _shell.EndSwitchTiming(completed);
            _shell.IsBusy = false;

            // Re-read the state even after a failure. Every step up to the
            // registry write leaves the machine untouched, but the ones after
            // it do not, and a run that failed halfway is exactly when the
            // shell's idea of the state is stale.
            _shell.RefreshSessionState();

            RefreshDetection();
            RefreshPreviewCaption();
            if (!quick) _ = RefreshPreviewAsync();
        }
    }

    /// <summary>
    /// Asks before doing anything, with No as the default answer.
    /// </summary>
    /// <remarks>
    /// The operation moves the user's desktop and closes every open Explorer
    /// window, so a Yes/No box is deliberately not answerable by pressing Enter
    /// out of habit. The owner is set so the prompt cannot end up behind the
    /// window that raised it.
    /// </remarks>
    private bool ConfirmPrepare()
    {
        string title = LocalizationManager.Instance["PrepareConfirmTitle"];
        string body = LocalizationManager.Instance[_shell.CurrentSettings.SwitchingExperience == SwitchingExperience.Responsive
            ? "PrepareConfirmLiveBody" : "PrepareConfirmBody"];

        if (Application.Current?.MainWindow is { IsLoaded: true } owner)
        {
            return MessageBox.Show(
                owner, body, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                == MessageBoxResult.Yes;
        }

        return MessageBox.Show(
            body, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            == MessageBoxResult.Yes;
    }

    private void ReportFailure(Exception error)
    {
        _viewVerificationFailure = error is DesktopViewVerificationException;
        _errorKey = ErrorKeyFor(error);
        ErrorDetails = error.Message;

        // A declined prompt is not a malfunction: the user answered it, and the
        // workflow guarantees nothing was changed. It belongs in the status
        // line, not in something shaped like a crash report.
        bool declined = error is ElevationDeclinedException { WasDeclined: true };

        HasError = !declined;
        RefreshErrorMessage();

        _shell.StatusText = ErrorMessage;
        RaiseCanExecuteChanged();
    }

    private bool CanUseCompatibility() => ShowCompatibilityFallback && !_shell.IsBusy && _shell.CanPrepare;

    private void UseCompatibility()
    {
        if (!CanUseCompatibility() || !_shell.UseCompatibilitySwitching()) return;
        ClearError();
        _shell.StatusText = LocalizationManager.Instance["CompatibilitySelectedAfterFailure"];
    }

    private void ClearError()
    {
        _viewVerificationFailure = false;
        _errorKey = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
        ErrorDetails = string.Empty;
        RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Maps a failure to the string that names it.
    /// </summary>
    /// <remarks>
    /// The interop kinds map onto <c>Error</c> plus the enum member name, which
    /// is why both names have to travel together: the kind is the contract with
    /// Core, and the UI owns the wording.
    /// </remarks>
    private static string ErrorKeyFor(Exception error) => error switch
    {
        DesktopViewVerificationException => "ErrorDesktopViewVerification",
        ElevationDeclinedException { WasDeclined: true } => "ErrorElevationCancelled",
        DesktopInteropException interop => ErrorKeyPrefix + interop.Kind,
        _ => "ErrorUnexpected",
    };

    // ---------------------------------------------------------------
    // Reading the machine
    // ---------------------------------------------------------------

    private sealed record DetailRequest(DailySwitchPlan Plan, string? AlternateWallpaper,
        System.Globalization.CultureInfo Culture);
    private sealed record TargetDetails(DesktopReadiness? Facts, string[] Files, string[] Residual, ImageSource? Preview);

    private void RefreshDetection()
    {
        MeetingDesktopPath = _paths.ResolveMeetingDesktopDirectory(_shell.CurrentSettings);
        RefreshDirectionText();
        _location = _shell.CurrentDesktopLocation ?? _locations.Detect(MeetingDesktopPath);
        HasDetectionProblem = !_location.IsUsable;
        RefreshDetectionText();
        RaiseCanExecuteChanged();
        RequestTargetDetails();
    }

    private void RequestTargetDetails()
    {
        if (_disposed || _shell.IsBusy || !ReferenceEquals(_shell.CurrentView, this))
        { _details.Invalidate(); return; }
        string? target = TargetDesktopPath;
        if (!string.Equals(target, _detailsTarget, StringComparison.OrdinalIgnoreCase))
        {
            _detailsTarget = target;
            TargetFiles.Clear(); TargetPreview = null;
            ReadinessSummary = LocalizationManager.Instance["CommonNotAvailable"];
            ReadinessWarning = PublicResidualSummary = string.Empty;
            NotifyTargetDetails();
        }
        _ = _details.Request(new(_shell.DailySwitch, _shell.CurrentSettings.MeetingWallpaperPath,
            LocalizationManager.Instance.Culture));
    }

    private static TargetDetails ReadTargetDetails(DetailRequest request)
    {
        try
        {
            string? target = request.Plan.TargetPath;
            if (target is null) return new(null, [], [], null);
            var facts = DesktopReadiness.Inspect(target);
            string[] names = Directory.Exists(target)
                ? Directory.EnumerateFileSystemEntries(target).Take(40).Select(Path.GetFileName).OfType<string>().ToArray() : [];
            string shared = PublicDesktopService.PublicDesktopDirectory;
            string[] residual = Directory.Exists(shared)
                ? Directory.EnumerateFileSystemEntries(shared)
                    .Where(p => !Path.GetExtension(p).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName).OfType<string>().ToArray() : [];
            return new(facts, names, residual, BuildTargetPreview(request, names));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { return new(null, [], [], null); }
    }

    private void ApplyTargetDetails(TargetDetails details)
    {
        if (_disposed || _shell.IsBusy || !ReferenceEquals(_shell.CurrentView, this)) return;
        ReadinessSummary = details.Facts is { } facts
            ? LocalizationManager.Instance.Format("PreflightContents", facts.FileCount, facts.FolderCount,
                Math.Round(facts.TopLevelBytes / 1024d / 1024d, 1),
                facts.FreeBytes is { } free ? Math.Round(free / 1024d / 1024d / 1024d, 1).ToString() : "—")
            : LocalizationManager.Instance["CommonNotAvailable"];
        ReadinessWarning = details.Facts?.DynamicWallpaper == true
            ? LocalizationManager.Instance["PreflightDynamicWallpaper"] : string.Empty;
        TargetFiles.Clear();
        foreach (string name in details.Files) TargetFiles.Add(name);
        PublicResidualSummary = details.Residual.Length == 0 ? string.Empty
            : LocalizationManager.Instance["PrepareModernResidual"] + " " + string.Join(", ", details.Residual);
        TargetPreview = details.Preview;
        NotifyTargetDetails();
    }

    private void NotifyTargetDetails()
    {
        foreach (string name in new[] { nameof(ReadinessSummary), nameof(ReadinessWarning),
            nameof(PublicResidualSummary), nameof(TargetPreview) }) OnPropertyChanged(name);
    }

    public void Dispose()
    {
        _disposed = true;
        _previewGeneration++;
        _details.Dispose();
        _shell.PropertyChanged -= OnShellPropertyChanged;
    }

    private void RefreshDetectionText()
    {
        DetectedDesktopPath = _location?.ResolvedPath
            ?? LocalizationManager.Instance["CommonNotAvailable"];

        DetectionNote = BuildDetectionNote();
    }

    private string BuildDetectionNote()
    {
        if (_location is null || _location.Status == DesktopLocationStatus.Unresolved)
        {
            return LocalizationManager.Instance["CommonNotAvailable"];
        }

        if (_location.Status == DesktopLocationStatus.Conflicting)
        {
            return LocalizationManager.Instance["DiagnosticsOutOfSync"];
        }

        // Worth saying next to "all sources agree": OneDrive moves the desktop
        // out of the profile, and a user looking at the path above will
        // otherwise wonder whose folder it is.
        return _location.IsOneDriveRedirected
            ? LocalizationManager.Instance["DiagnosticsInSync"] +
              " · " +
              LocalizationManager.Instance["DiagnosticsOneDrive"]
            : LocalizationManager.Instance["DiagnosticsInSync"];
    }

    /// <summary>
    /// Repoints the caption at whatever the preview is currently showing.
    /// </summary>
    /// <remarks>
    /// The capture is always of the live desktop, so the caption depends on
    /// which desktop that is - and this page stays reachable while swapped.
    /// </remarks>
    private void RefreshPreviewCaption() =>
        PreviewCaption = LocalizationManager.Instance[
            _shell.SessionState == SessionState.Swapped ? "PreparePreviewActive" : "PreparePreviewLeaving"];

    private void RefreshErrorMessage()
    {
        string key = _viewVerificationFailure
            ? _sessions.Exists ? "ErrorDesktopViewRecoveryPending" : "ErrorDesktopViewRolledBack"
            : _errorKey;
        ErrorMessage = key.Length == 0 ? string.Empty : LocalizationManager.Instance[key];
    }

    /// <summary>
    /// Captures the desktop and converts it for display.
    /// </summary>
    /// <remarks>
    /// Never throws. A preview is a convenience shown seconds before a
    /// time-critical operation, and the Core service already treats a failed
    /// capture as "no preview" rather than an error; failing the page over it
    /// would be strictly worse than the placeholder.
    /// </remarks>
    private async Task RefreshPreviewAsync()
    {
        if (_disposed || _shell.IsBusy) return;
        int generation = ++_previewGeneration;
        string? selected = _selectedMonitor?.DeviceName;
        try
        {
            var displays = await Task.Run(() => MonitorTopologyReader.Capture().Monitors);
            if (generation != _previewGeneration) return;
            _updatingMonitors = true;
            try
            {
                Monitors.Clear();
                foreach (var display in displays) Monitors.Add(display);
                _selectedMonitor = displays.FirstOrDefault(m => m.DeviceName == selected)
                    ?? displays.FirstOrDefault(m => m.IsPrimary);
                PreviewMonitorNotice = selected is not null && _selectedMonitor?.DeviceName != selected
                    ? LocalizationManager.Instance["PrepareModernDisplayChanged"] : string.Empty;
                OnPropertyChanged(nameof(PreviewMonitorNotice));
                OnPropertyChanged(nameof(SelectedMonitor));
            }
            finally { _updatingMonitors = false; }
            string? device = _selectedMonitor?.DeviceName;
            PreviewImage? captured = await Task.Run(() => _preview.Capture(device));
            ImageSource? image = captured is null ? null : await Task.Run(() => CreateBitmap(captured));
            if (generation == _previewGeneration) PreviewImage = image;
        }
        catch (Exception)
        {
            if (generation == _previewGeneration) PreviewImage = null;
        }
    }

    /// <summary>
    /// Converts a Core capture into a frozen, bindable bitmap.
    /// </summary>
    /// <remarks>
    /// Three details of <see cref="PreviewImage"/> have to be handled here, and
    /// none of them are visible in its type. GDI lays the rows out bottom-up,
    /// so the rows are copied in reverse or the preview renders upside down. A
    /// 32-bit <c>BI_RGB</c> capture leaves every alpha byte at zero, and
    /// <see cref="PixelFormats.Bgra32"/> honours that byte, so it is forced
    /// opaque or the whole image comes out invisible. And the stride is walked
    /// rather than assumed to equal <c>Width * 4</c>: it is part of the
    /// contract, and it is padding, so copying it through unchanged keeps the
    /// rows aligned exactly as GDI wrote them.
    /// </remarks>
    private static ImageSource CreateBitmap(PreviewImage source)
    {
        int rowBytes = source.Width * 4;
        var pixels = new byte[rowBytes * source.Height];

        for (int y = 0; y < source.Height; y++)
        {
            int target = y * rowBytes;

            Buffer.BlockCopy(source.Bgra, (source.Height - 1 - y) * source.Stride, pixels, target, rowBytes);

            for (int alpha = target + 3; alpha < target + rowBytes; alpha += 4)
            {
                pixels[alpha] = 0xFF;
            }
        }

        var bitmap = BitmapSource.Create(
            source.Width, source.Height, 96, 96, PixelFormats.Bgra32, null, pixels, rowBytes);

        bitmap.Freeze();
        return bitmap;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SessionState):
                RefreshDetection();
                RefreshPreviewCaption();
                break;
            case nameof(MainViewModel.CurrentSettings):
                RefreshPreviewCaption();
                RaiseCanExecuteChanged();
                break;

            case nameof(MainViewModel.CurrentView):
                RequestTargetDetails();
                break;
            case nameof(MainViewModel.IsBusy):
                if (_shell.IsBusy) { _details.Invalidate(); _previewGeneration++; }
                else RequestTargetDetails();
                OnPropertyChanged(nameof(IsBusy));
                RaiseCanExecuteChanged();
                break;
        }
    }
}

/// <summary>Dispatches only the direction and record the page actually showed.</summary>
internal sealed class DailySwitchPageAction(Func<bool> isBusy, Func<DailySwitchPlan> refresh,
    Func<Task> prepare, Func<Task> returnOnPage, Action review)
{
    private bool _running;

    public async Task ExecuteAsync(DailySwitchPlan displayed)
    {
        if (_running || isBusy()) return;
        _running = true;
        try
        {
            DailySwitchPlan current = refresh();
            if (current.Direction == DailySwitchDirection.Recovery || !displayed.Matches(current))
            { review(); return; }
            if (current.Direction == DailySwitchDirection.ToOriginal) await returnOnPage();
            else await prepare();
        }
        finally { _running = false; }
    }
}
