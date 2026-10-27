using System.Windows;
using System.IO;
using System.Diagnostics;
using Microsoft.Win32;
using System.Text;
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
/// Undoes a swap: points the desktop back, then re-applies the saved layout.
/// </summary>
/// <remarks>
/// Everything this page shows is derived from the record the prepare step left
/// behind, so it keeps no state between visits and can be rebuilt at any time.
/// That record is re-read on every activation rather than once at startup,
/// because the restore can be performed while this page is off screen and
/// nothing else in the app would tell it so.
/// </remarks>
public sealed class RestoreViewModel : ObservableObject, IPageViewModel
{
    /// <summary>
    /// Name of the capture the prepare step wrote.
    /// </summary>
    /// <remarks>
    /// Fixed rather than dated. There is only one pending swap, so a dated name
    /// would accumulate previews of desktops nobody is returning to.
    /// </remarks>
    private const string PreviewFileName = "return-preview.png";

    private readonly AppPaths _paths;
    private readonly SessionStore _sessions;
    private readonly RestoreWorkflow _workflow;
    private readonly IElevationBroker _elevationBroker;

    public IReadOnlyList<string> ShortcutConflicts => _elevationBroker.LastReport?.Result.Conflicts
        .Select(c => Path.GetFileName(c.RequestedPath) + " → " + Path.GetFileName(c.PreservedPath)).ToArray() ?? [];
    public bool HasShortcutConflicts => ShortcutConflicts.Count > 0;
    private readonly MainViewModel _shell;
    private readonly AsyncRelayCommand _restoreCommand;
    private readonly RelayCommand _returnToSwitchPageCommand;

    private readonly RelayCommand _adoptLegacyCommand;
    public bool CanAdoptLegacy => !_sessions.Exists;
    public ICommand AdoptLegacyCommand => _adoptLegacyCommand;
    public ICommand ArchiveMeetingFolderCommand { get; }
    private async Task ArchiveMeetingFolderAsync()
    {
        if (_shell.IsBusy || string.IsNullOrWhiteSpace(_meetingFolder)) return;
        var picker = new OpenFolderDialog { Title = LocalizationManager.Instance["RecoveryDetailArchive"] };
        if (picker.ShowDialog() != true) return;
        _shell.IsBusy = true;
        try
        {
            string archive = await Task.Run(() => MeetingFolderArchive.Copy(_meetingFolder, picker.FolderName));
            _shell.StatusText = LocalizationManager.Instance["RecoveryDetailArchiveDone"] + " " + archive;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _shell.StatusText = LocalizationManager.Instance["RecoveryDetailArchiveFailed"]; }
        finally { _shell.IsBusy = false; }
    }

    private void AdoptLegacy()
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationManager.Instance["RecoveryDetailLegacySelect"],
            Filter = "original_desktop_path.txt|original_desktop_path.txt",
            FileName = "original_desktop_path.txt",
            CheckFileExists = true,
            Multiselect = false
        };
        try
        {
            if (dialog.ShowDialog() != true) return;
            if (_shell.IsBusy || _sessions.Exists) throw new InvalidOperationException();
            DesktopLocation current = new DesktopLocationService(new DesktopPathService()).Detect(_shell.CurrentSettings.MeetingDesktopPath);
            if (!current.IsUsable || current.ResolvedPath is null) throw new InvalidOperationException();
            SessionInfo adopted = LegacyRecoveryRecord.Read(dialog.FileName, current.ResolvedPath);
            if (_sessions.Exists) throw new InvalidOperationException();
            string confirmation = LocalizationManager.Instance["RecoveryDetailLegacyConfirm"] + "\n\n" + adopted.OriginalDesktopPath;
            if (MessageBox.Show(confirmation, LocalizationManager.Instance["RestoreTitle"],
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _sessions.Save(adopted);
            _shell.RefreshSessionState();
            OnActivated();
            _shell.StatusText = LocalizationManager.Instance["RecoveryDetailLegacyImported"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            _shell.StatusText = LocalizationManager.Instance["RecoveryDetailLegacyInvalid"];
        }
    }

    private RestoreResult? _recoveryResult;
    private string? _meetingFolder;
    private readonly RelayCommand _openMeetingFolderCommand;

    public bool HasRecoveryResult => _recoveryResult is not null;
    public string RecoverySummary => _recoveryResult is { } result
        ? result.Layout is { } layout
            ? LocalizationManager.Instance.Format("RecoveryDetailCounts", layout.Requested, layout.Placed, MissingIcons.Count, layout.OffTarget.Count)
            : LocalizationManager.Instance["RecoveryDetailNoLayout"]
        : string.Empty;
    public string RecoveryTopology => _recoveryResult is { } result
        ? LocalizationManager.Instance[result.TopologyChanged ? "RecoveryDetailTopologyChanged" : "RecoveryDetailTopologySame"] : string.Empty;
    public IReadOnlyList<string> MissingIcons => _recoveryResult?.Layout is { } layout
        ? layout.NotFound.Take(Math.Max(0, layout.NotFound.Count - _recoveryResult.UnmappedIcons.Count)).ToArray()
        : Array.Empty<string>();
    public IReadOnlyList<string> UnmappedIcons => _recoveryResult?.UnmappedIcons ?? Array.Empty<string>();
    public bool HasUnmappedIcons => UnmappedIcons.Count > 0;
    public IReadOnlyList<string> MovedIcons => _recoveryResult?.Layout?.OffTarget.Select(icon =>
        $"{icon.Name}: ({icon.WantedX}, {icon.WantedY}) → ({icon.ActualX}, {icon.ActualY})").ToArray() ?? Array.Empty<string>();
    public IReadOnlyList<string> LeftoverFiles => _recoveryResult?.FilesLeftOnMeetingDesktop ?? Array.Empty<string>();
    public string LeftoverSummary => _recoveryResult is { LeftoverScanComplete: false }
        ? LocalizationManager.Instance["RestoreInventoryUnavailable"]
        : LocalizationManager.Instance.Format("RecoveryDetailLeftovers", LeftoverFiles.Count);
    public ICommand OpenMeetingFolderCommand => _openMeetingFolderCommand;

    private void NotifyRecoveryResult()
    {
        foreach (string name in new[] { nameof(HasRecoveryResult), nameof(RecoverySummary), nameof(RecoveryTopology), nameof(MissingIcons), nameof(UnmappedIcons), nameof(HasUnmappedIcons), nameof(MovedIcons), nameof(LeftoverFiles), nameof(LeftoverSummary) })
            OnPropertyChanged(name);
        _openMeetingFolderCommand.RaiseCanExecuteChanged();
    }

    private void OpenMeetingFolder()
    {
        try
        {
            if (_meetingFolder is not null && Directory.Exists(_meetingFolder))
                Process.Start(new ProcessStartInfo { FileName = _meetingFolder, UseShellExecute = true, Verb = "open" });
            else
                _shell.StatusText = LocalizationManager.Instance["RecoveryDetailFolderUnavailable"];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _shell.StatusText = LocalizationManager.Instance["RecoveryDetailFolderUnavailable"];
        }
    }

    private SessionInfo? _session;
    private string _snapshotSummary = string.Empty;
    private ImageSource? _previewImage;
    private bool _hasPreview;

    public RestoreViewModel(
        AppPaths paths,
        LayoutStore layouts,
        SessionStore sessions,
        IElevationBroker elevation,
        MainViewModel shell)
    {
        _elevationBroker = elevation;
        _paths = paths;
        _sessions = sessions;
        _shell = shell;
        ArchiveMeetingFolderCommand = new AsyncRelayCommand(ArchiveMeetingFolderAsync, () => !_shell.IsBusy);

        _adoptLegacyCommand = new RelayCommand(AdoptLegacy, () => !_shell.IsBusy && CanAdoptLegacy);
        _openMeetingFolderCommand = new RelayCommand(OpenMeetingFolder, () => !string.IsNullOrWhiteSpace(_meetingFolder));
        _workflow = new RestoreWorkflow(paths, layouts, sessions, elevation);

        // The predicate reads the shell rather than a copy of the state: the
        // state is refreshed by the shell after every operation, and a copy
        // here would be the one thing that goes stale.
        _restoreCommand = new AsyncRelayCommand(RestoreAsync, () => !_shell.IsBusy && _shell.CanRestore);
        _returnToSwitchPageCommand = new RelayCommand(() => _shell.NavigateCommand.Execute("Prepare"), () => !_shell.IsBusy);

        RefreshSnapshot();
    }

    public ICommand RestoreCommand => _restoreCommand;
    public ICommand ReturnToSwitchPageCommand => _returnToSwitchPageCommand;
    public string PageTitle => LocalizationManager.Instance["RestoreTitle"];
    public string PageDescription => LocalizationManager.Instance["RestoreDescription"];

    public string PreviewCaption => LocalizationManager.Instance["RestorePreviewReturning"];

    public ImageSource? PreviewImage => _previewImage;

    public bool HasPreview => _hasPreview;

    public string SnapshotSummary
    {
        get => _snapshotSummary;
        private set => SetProperty(ref _snapshotSummary, value);
    }

    /// <summary>
    /// The folder the desktop will point at again.
    /// </summary>
    /// <remarks>
    /// Shown because it is the one question a swapped desktop raises and
    /// nothing else on the page answers: <em>where did my own desktop go?</em>
    /// While the swap is active the desktop really does point at the empty
    /// folder, so every other panel - including the Prepare page's detected
    /// path and the preview here - is showing the alternate one. The only
    /// place the user's own folder still appears is the swap record, and a
    /// user who cannot see that it was written down has no reason to believe
    /// pressing the button brings it back.
    /// </remarks>
    public string? OriginalDesktopPath => _session?.OriginalDesktopPath;

    /// <summary>False when there is no record, so the row hides rather than showing a blank.</summary>
    public bool HasOriginalDesktopPath => !string.IsNullOrWhiteSpace(OriginalDesktopPath);

    /// <summary>
    /// True when there is a saved layout the restore can actually apply.
    /// </summary>
    /// <remarks>
    /// A session file naming a snapshot that has since been deleted does not
    /// count. That restore still succeeds, but by re-flowing the icons, and the
    /// user is better told so before pressing the button than after.
    /// </remarks>
    public bool HasSnapshot =>
        _session is not null &&
        _session.CapturedIconCount > 0 &&
        !string.IsNullOrWhiteSpace(_session.AutoLayoutSnapshotPath) &&
        File.Exists(_session.AutoLayoutSnapshotPath);

    /// <summary>Re-reads the swap record and the capture taken before it.</summary>
    public void OnActivated()
    {
        RefreshSnapshot();
        LoadPreview();
        RaiseCanExecuteChanged();
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageDescription));
        OnPropertyChanged(nameof(PreviewCaption));

        // The summary carries a localized unit, so it has to be rebuilt rather
        // than merely re-notified.
        RefreshSnapshotSummary();
        NotifyRecoveryResult();
    }

    /// <summary>
    /// Re-queries the enabled state. The shell calls this whenever the session
    /// state the predicate depends on changes.
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        _restoreCommand.RaiseCanExecuteChanged();
        _returnToSwitchPageCommand.RaiseCanExecuteChanged();
        _adoptLegacyCommand.RaiseCanExecuteChanged();
        (ArchiveMeetingFolderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanAdoptLegacy));
    }

    // -------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------

    private void RefreshSnapshot()
    {
        _session = _sessions.Load();

        RefreshSnapshotSummary();

        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(OriginalDesktopPath));
        OnPropertyChanged(nameof(HasOriginalDesktopPath));
    }

    private void RefreshSnapshotSummary()
    {
        if (_session is null || !HasSnapshot)
        {
            // Empty rather than "0 icons": the warning banner below is the
            // message in that case, and a bare count next to it reads as if
            // some icons were saved when none were.
            SnapshotSummary = string.Empty;
            return;
        }

        SnapshotSummary = LocalizationManager.Instance.Format("LayoutsCounts", _session.CapturedIconCount);
    }

    // -------------------------------------------------------------------
    // Preview
    // -------------------------------------------------------------------

    private void LoadPreview()
    {
        _previewImage = HasSnapshot && _session?.HasReturnPreview == true
            ? TryLoadFrozenPreview(Path.Combine(_paths.StateDirectory, PreviewFileName)) : null;
        _hasPreview = _previewImage is not null;

        OnPropertyChanged(nameof(PreviewImage));
        OnPropertyChanged(nameof(HasPreview));
    }

    /// <summary>
    /// Loads a PNG fully into memory and freezes it.
    /// </summary>
    /// <remarks>
    /// <c>OnLoad</c> together with <c>Freeze</c> is what stops the file being
    /// held open: WPF otherwise keeps a lazy decoder bound to the stream, and
    /// the next prepare could not replace the capture. The stream is used
    /// rather than <c>UriSource</c> because a data directory is a user-chosen
    /// path, and a '#' in it would be read as a fragment and truncate the name.
    ///
    /// A missing, locked or damaged file is not an error. A preview is a
    /// convenience; "no preview available" is an acceptable answer.
    /// </remarks>
    private static ImageSource? TryLoadFrozenPreview(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or FileFormatException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    // -------------------------------------------------------------------
    // The operation
    // -------------------------------------------------------------------

    private Task RestoreAsync() => RestoreAsync(false);

    internal Task RestoreFromShortcutAsync() => RestoreAsync(true);

    internal Task ReturnFromSwitchPageAsync() => RestoreAsync(false, dailyReturn: true);

    private async Task RestoreAsync(bool quick, bool dailyReturn = false)
    {
        if (_shell.IsBusy) return;
        DailySwitchPlan displayed = _shell.DailySwitch;
        _shell.RefreshSessionState();
        if ((quick || dailyReturn) && (!_shell.CanReturnToOriginal || !displayed.Matches(_shell.DailySwitch)))
        {
            _shell.ReviewRecoveryCommand.Execute(null);
            return;
        }
        if (!_shell.EnsureSwitchPermission()) return;
        bool completed = false;
        _shell.BeginSwitchTiming(returning: true);
        _shell.IsBusy = true;
        _shell.ProgressValue = 0;
        string? meetingFolder = _sessions.Load()?.MeetingDesktopPathAtSwap;
        _recoveryResult = null;
        NotifyRecoveryResult();

        try
        {
            if (quick) _shell.SetQuickSwitchVisibility(false);
            DesktopSwitchMode mode = _sessions.Load()?.SwitchMode ?? DesktopSwitchMode.RestartExplorer;
            await using var transition = new SwitchFeedback(_shell.CurrentSettings, mode, () => _shell.StatusText);
            RestoreResult result = await _workflow
                .RunAsync(_shell.CreateProgress(), beforeDesktopChange: transition.ShowAsync,
                    desktopReady: () => { _shell.MarkDesktopReady(); transition.DesktopReady(); })
                .ConfigureAwait(true);

            _recoveryResult = result;
            _meetingFolder = meetingFolder;
            NotifyRecoveryResult();
            _shell.StatusText = Describe(result);
            transition.Complete();
            completed = true;
        }
        catch (RestoreIncompleteException ex)
        {
            _recoveryResult = ex.Result;
            _meetingFolder = meetingFolder;
            NotifyRecoveryResult();
            _shell.StatusText = Describe(ex.Result) + " " + LocalizationManager.Instance["ErrorRestoreRecordKept"];
        }
        catch (DesktopViewVerificationException)
        {
            _shell.StatusText = LocalizationManager.Instance["ErrorDesktopViewVerification"] + " " +
                LocalizationManager.Instance["ErrorRestoreRecordKept"];
        }
        catch (RestoreNotPossibleException)
        {
            // The exception's own message is English and developer-facing, so
            // only its category reaches the user.
            _shell.StatusText = FailureStatus(_sessions.Load());
        }
        catch (ElevationDeclinedException)
        {
            _shell.StatusText = LocalizationManager.Instance["ErrorElevationCancelled"];
        }
        catch (DesktopInteropException ex)
        {
            // Core reports a kind and never a sentence; the keys mirror the
            // enum, which is what lets the wording be translated.
            _shell.StatusText = LocalizationManager.Instance[$"Error{ex.Kind}"];
        }
        catch (Exception)
        {
            // AsyncRelayCommand.Execute is async void, so an exception escaping
            // here ends the process. There is nothing left in flight at this
            // point, and a raw crash is worse than a vague sentence.
            _shell.StatusText = LocalizationManager.Instance["ErrorRestoreRecordKept"];
        }
        finally
        {
            if (quick && !completed) _shell.Notify(_shell.StatusText);
            _shell.EndSwitchTiming(completed);
            OnPropertyChanged(nameof(ShortcutConflicts));
            OnPropertyChanged(nameof(HasShortcutConflicts));
            _shell.IsBusy = false;

            // A successful restore deletes the swap record, and a failed one
            // can leave the machine in a state the detector reads differently,
            // so nothing shown a moment ago can be trusted now.
            _shell.RefreshSessionState();
            RefreshSnapshot();
            RaiseCanExecuteChanged();
            if (!quick)
            {
                LoadPreview();
                if (dailyReturn)
                {
                    if (!completed) _shell.ReviewRecoveryCommand.Execute(null);
                    else _shell.Prepare.OnActivated();
                }
            }
        }
    }

    internal static string FailureStatus(SessionInfo? session) => LocalizationManager.Instance[
        session?.ShortcutRestoreUncertain == true ? "ErrorShortcutRestoreUncertain" : "ErrorRestoreRecordKept"];

    /// <summary>
    /// Turns the structured result into the single line the status bar shows.
    /// </summary>
    /// <remarks>
    /// The layout verdict, the monitor warning and the files left behind are
    /// independent facts, so they are appended rather than chosen between: a
    /// restore that misplaced icons <em>and</em> left files behind has to
    /// report both.
    /// </remarks>
    internal static string Describe(RestoreResult result)
    {
        var text = new StringBuilder();

        if (result.Layout is { } layout)
        {
            text.Append(layout.IsExact
                ? LocalizationManager.Instance.Format("ResultRestoreExact", layout.Placed)
                : LocalizationManager.Instance.Format(
                    "ResultRestoreApproximate", layout.OffTarget.Count + Math.Max(0, layout.Requested - layout.Placed), layout.Requested));

            int missing = Math.Max(0, layout.NotFound.Count - result.UnmappedIcons.Count);
            if (missing > 0)
            {
                text.Append(' ')
                    .Append(LocalizationManager.Instance.Format("ResultRestoreNotFound", missing));
            }

            if (result.UnmappedIcons.Count > 0)
                text.Append(' ').Append(LocalizationManager.Instance.Format("ResultRestoreUnmapped", result.UnmappedIcons.Count));

            if (layout.UsedTwoPhase && !layout.ParkingSucceeded)
            {
                text.Append(' ').Append(LocalizationManager.Instance["ResultParkingDegraded"]);
            }
        }
        else
        {
            // No snapshot was applied, so the desktop is whatever Explorer made
            // of the folder - which is exactly what that string warns about.
            text.Append(LocalizationManager.Instance["RestoreNoSnapshot"]);
        }

        if (result.TopologyChanged)
        {
            text.Append(' ').Append(LocalizationManager.Instance["RestoreTopologyChanged"]);
        }

        if (!result.LeftoverScanComplete)
            text.Append(' ').Append(LocalizationManager.Instance["RestoreInventoryUnavailable"]);
        else if (result.FilesLeftOnMeetingDesktop.Count > 0)
        {
            text.Append(' ')
                .Append(LocalizationManager.Instance.Format(
                    "RestoreStrayFiles", result.FilesLeftOnMeetingDesktop.Count));
        }

        return text.ToString();
    }
}
