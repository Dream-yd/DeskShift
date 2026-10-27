using System.IO;
using MeetingDesktop.App.ViewModels;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;
using MeetingDesktop.App.Services;

namespace MeetingDesktop.App.Tests;

public sealed class SettingsPersistenceTests
{
    [Fact]
    public void CompletingAuthorizationResumesTheVisibleTargetDetails() => StaTest.Run(() =>
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        try
        {
            using var context = new Context();
            context.Shell.IsBusy = true;
            var frame = new System.Windows.Threading.DispatcherFrame();
            bool resumed = false;
            context.Shell.Prepare.PropertyChanged += (_, e) =>
            {
                if (!context.Shell.IsBusy && e.PropertyName == nameof(PrepareViewModel.ReadinessSummary) &&
                    context.Shell.Prepare.ReadinessSummary != Localization.LocalizationManager.Instance["CommonNotAvailable"])
                { resumed = true; frame.Continue = false; }
            };
            var finishAuthorization = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            finishAuthorization.Tick += (_, _) => { finishAuthorization.Stop(); context.Shell.IsBusy = false; };
            var deadline = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            deadline.Tick += (_, _) => { deadline.Stop(); frame.Continue = false; };
            finishAuthorization.Start(); deadline.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally { finishAuthorization.Stop(); deadline.Stop(); }
            Assert.True(resumed, "Busy→idle must resume details without another navigation or state refresh.");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    [Fact]
    public void LateProgressCannotReplaceTheResultOrTheNextSwitchStatus() => StaTest.Run(() =>
    {
        using var context = new Context();
        var previous = SynchronizationContext.Current;
        var queue = new QueuedContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            context.Shell.BeginSwitchTiming();
            var first = context.Shell.CreateProgress();
            first.Report(new ProgressReport(ProgressKey.RefreshingDesktop));
            context.Shell.EndSwitchTiming();
            context.Shell.StatusText = "Completed";
            queue.Drain();
            Assert.Equal("Completed", context.Shell.StatusText);
            context.Shell.BeginSwitchTiming();
            first.Report(new ProgressReport(ProgressKey.Done));
            context.Shell.StatusText = "Second switch";
            queue.Drain();
            Assert.Equal("Second switch", context.Shell.StatusText);
            context.Shell.CreateProgress().Report(new ProgressReport(ProgressKey.ReadingCurrentLayout));
            queue.Drain();
            Assert.Equal(Localization.LocalizationManager.Instance["ProgressReadingCurrentLayout"], context.Shell.StatusText);
            context.Shell.EndSwitchTiming();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    [Fact]
    public void QueuedProgressCannotReplaceTheDesktopReadyMessage() => StaTest.Run(() =>
    {
        using var context = new Context();
        var previous = SynchronizationContext.Current;
        var queue = new QueuedContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            context.Shell.BeginSwitchTiming();
            var progress = context.Shell.CreateProgress();
            progress.Report(new ProgressReport(ProgressKey.RefreshingDesktop));
            context.Shell.MarkDesktopReady();
            string ready = context.Shell.StatusText;
            queue.Drain();
            Assert.Equal(ready, context.Shell.StatusText);
            context.Shell.EndSwitchTiming(true);
            Assert.NotNull(Assert.Single(context.Shell.SwitchTimings.Snapshot()).ReadyMilliseconds);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> _callbacks = new();
        public override void Post(SendOrPostCallback d, object? state) => _callbacks.Enqueue(() => d(state));
        public void Drain() { while (_callbacks.TryDequeue(out var callback)) callback(); }
    }

    [Fact]
    public void NotificationAndRetentionAutoSaveWithoutCommittingLanguageOrFolderDrafts() => StaTest.Run(() =>
    {
        using var context = new Context();
        var page = context.Shell.Settings;
        page.BeginTrackingQuickAccessChanges();
        page.BeginTrackingUserLanguageChanges();
        var language = context.Store.Load().Language;
        page.SelectedLanguageIndex = 1;
        page.MeetingDesktopPath = context.NewFolder;
        page.ShowSwitchNotification = false;
        page.SelectedSnapshotRetentionIndex = 2;
        Assert.False(context.Store.Load().ShowSwitchNotification);
        Assert.Equal(15, context.Store.Load().AutomaticSnapshotRetentionDays);
        Assert.Equal(language, context.Store.Load().Language);
        Assert.Equal(context.OldFolder, context.Store.Load().MeetingDesktopPath);
        Assert.Equal(context.NewFolder, page.MeetingDesktopPath);
        Assert.Equal(1, page.SelectedLanguageIndex);
    });

    [Fact]
    public void FailedOrBusyRetentionSaveRevertsTheDisplayedChoice() => StaTest.Run(() =>
    {
        using var context = new Context();
        var page = context.Shell.Settings;
        page.BeginTrackingQuickAccessChanges();
        using (context.LockSettings()) page.SelectedSnapshotRetentionIndex = 1;
        Assert.Equal(0, page.SelectedSnapshotRetentionIndex);
        context.Shell.IsBusy = true;
        page.ShowSwitchNotification = false;
        Assert.True(page.ShowSwitchNotification);
        Assert.Equal(0, context.Store.Load().AutomaticSnapshotRetentionDays);
    });

    [Fact]
    public void FailedDesktopSwitchReplacesTheProgressStatusWithItsActualError() => StaTest.Run(() =>
    {
        using var context = new Context();
        context.Shell.StatusText = "Refreshing desktop...";
        var report = typeof(PrepareViewModel).GetMethod("ReportFailure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        report.Invoke(context.Shell.Prepare, [new DesktopViewVerificationException("The active view is unchanged.")]);
        Assert.True(context.Shell.Prepare.HasError);
        Assert.Equal(context.Shell.Prepare.ErrorMessage, context.Shell.StatusText);
    });

    [Fact]
    public void CompatibilityFallbackOnlySavesTheChosenModeAndPreservesUnrelatedDrafts() => StaTest.Run(() =>
    {
        var elevation = new CountingElevation();
        using var context = new Context(elevation);
        context.Shell.Settings.MeetingDesktopPath = context.NewFolder;
        context.Shell.Settings.CapturePreview = true;
        ReportViewFailure(context);
        var page = context.Shell.Prepare;
        Assert.True(page.ShowCompatibilityFallback);
        Assert.True(page.UseCompatibilityCommand.CanExecute(null));
        Assert.Equal(SwitchingExperience.Responsive, context.Store.Load().SwitchingExperience);

        page.UseCompatibilityCommand.Execute(null);

        Assert.Equal(SwitchingExperience.Compatibility, context.Store.Load().SwitchingExperience);
        Assert.False(context.Shell.Settings.UseLiveDesktopSwitching);
        Assert.Equal(context.OldFolder, context.Store.Load().MeetingDesktopPath);
        Assert.False(context.Store.Load().CaptureReturnPreview);
        Assert.Equal(context.NewFolder, context.Shell.Settings.MeetingDesktopPath);
        Assert.True(context.Shell.Settings.CapturePreview);
        Assert.False(page.HasError);
        Assert.False(page.ShowCompatibilityFallback);
        Assert.False(File.Exists(context.Paths.SessionFilePath));
        Assert.Equal(0, elevation.Calls);
    });

    [Fact]
    public void PendingRecoveryKeepsItsRecordAndCannotChangeModeOrClaimRollbackCompleted() => StaTest.Run(() =>
    {
        using var context = new Context();
        var sessions = new SessionStore(context.Paths.SessionFilePath);
        sessions.Save(new SessionInfo { OriginalDesktopPath = context.Original,
            MeetingDesktopPathAtSwap = context.OldFolder, SwitchMode = DesktopSwitchMode.Live });
        string before = File.ReadAllText(context.Paths.SessionFilePath);
        ReportViewFailure(context);
        var page = context.Shell.Prepare;
        Assert.True(page.ShowSwitchRecovery);
        Assert.False(page.ShowCompatibilityFallback);
        Assert.False(page.UseCompatibilityCommand.CanExecute(null));
        Assert.Equal(Localization.LocalizationManager.Instance["ErrorDesktopViewRecoveryPending"], page.ErrorMessage);
        page.UseCompatibilityCommand.Execute(null);
        Assert.Equal(before, File.ReadAllText(context.Paths.SessionFilePath));
        Assert.Equal(SwitchingExperience.Responsive, context.Store.Load().SwitchingExperience);
        page.ReviewSwitchRecoveryCommand.Execute(null);
        Assert.Same(context.Shell.Restore, context.Shell.CurrentView);
    });

    [Fact]
    public void FailedFallbackPreferenceSaveLeavesTheErrorAndCanBeRetried() => StaTest.Run(() =>
    {
        using var context = new Context();
        ReportViewFailure(context);
        using (var locked = context.LockSettings()) context.Shell.Prepare.UseCompatibilityCommand.Execute(null);
        Assert.True(context.Shell.Prepare.HasError);
        Assert.True(context.Shell.Prepare.ShowCompatibilityFallback);
        Assert.Equal(SwitchingExperience.Responsive, context.Shell.CurrentSettings.SwitchingExperience);
        context.Shell.Prepare.UseCompatibilityCommand.Execute(null);
        Assert.Equal(SwitchingExperience.Compatibility, context.Store.Load().SwitchingExperience);
    });

    [Fact]
    public void CompatibilityFallbackDoesNotPersistAnUncommittedLanguagePreview() => StaTest.Run(() =>
    {
        using var context = new Context();
        LanguagePreference savedLanguage = context.Store.Load().Language;
        context.Shell.OnSettingsChanged(context.Shell.CurrentSettings with { Language = LanguagePreference.SimplifiedChinese });
        Assert.Equal(savedLanguage, context.Store.Load().Language);
        ReportViewFailure(context);
        context.Shell.Prepare.UseCompatibilityCommand.Execute(null);
        Assert.Equal(savedLanguage, context.Store.Load().Language);
        Assert.Equal(LanguagePreference.SimplifiedChinese, context.Shell.CurrentSettings.Language);
        Assert.Equal(SwitchingExperience.Compatibility, context.Store.Load().SwitchingExperience);
    });

    [Fact]
    public void BusySwitchRejectsEvenAProgrammaticFallbackInvocation() => StaTest.Run(() =>
    {
        using var context = new Context();
        ReportViewFailure(context);
        context.Shell.IsBusy = true;
        Assert.False(context.Shell.Prepare.UseCompatibilityCommand.CanExecute(null));
        context.Shell.Prepare.UseCompatibilityCommand.Execute(null);
        Assert.Equal(SwitchingExperience.Responsive, context.Store.Load().SwitchingExperience);
        Assert.True(context.Shell.Prepare.HasError);
    });

    [Fact]
    public void OtherErrorsDoNotSuggestThatExplorerCompatibilityIsTheCause() => StaTest.Run(() =>
    {
        using var context = new Context();
        ReportViewFailure(context);
        var report = typeof(PrepareViewModel).GetMethod("ReportFailure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        report.Invoke(context.Shell.Prepare, [new IOException("Unable to save recovery data.")]);
        Assert.False(context.Shell.Prepare.ShowCompatibilityFallback);
        Assert.False(context.Shell.Prepare.ShowSwitchRecovery);
        Assert.Equal(context.Shell.Prepare.ErrorMessage, context.Shell.StatusText);
    });

    private static void ReportViewFailure(Context context)
    {
        var report = typeof(PrepareViewModel).GetMethod("ReportFailure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        report.Invoke(context.Shell.Prepare, [new DesktopViewVerificationException("Active view unchanged.")]);
    }

    private sealed class CountingElevation : IElevationBroker
    {
        internal int Calls;
        public ElevationOutcome Run(string argument) { Calls++; throw new InvalidOperationException("No real operations in a settings action."); }
    }

    [Fact]
    public void AnUnrecognizedStartupEntryIsPreservedAndReportedEvenWhenThePreferenceIsOff() => StaTest.Run(() =>
    {
        var entry = new StartupRegistrationValue("unrecognized startup payload", Microsoft.Win32.RegistryValueKind.String);
        using var context = new Context(startupStore: new MemoryStartupStore { Value = entry });
        Assert.False(context.Shell.StartWithWindows);
        Assert.Equal(MeetingDesktop.App.Localization.LocalizationManager.Instance["StartupRegistrationMismatch"], context.Shell.StartupStatusText);
    });
    [Fact]
    public void StartupControlOnlyWritesAfterLoadingAndRollsBackWhenSavingFails() => StaTest.Run(() =>
    {
        var startupStore = new MemoryStartupStore();
        using var context = new Context(startupStore: startupStore);
        context.Shell.Settings.StartWithWindows = true;
        Assert.Null(startupStore.Value);
        context.Shell.Settings.BeginTrackingQuickAccessChanges();
        using (var locked = context.LockSettings()) context.Shell.Settings.StartWithWindows = true;
        Assert.Null(startupStore.Value);
        Assert.False(context.Shell.Settings.StartWithWindows);
        context.Shell.Settings.StartWithWindows = true;
        Assert.NotNull(startupStore.Value);
        Assert.True(context.Store.Load().StartWithWindows);
        context.Shell.Settings.StartWithWindows = false;
        Assert.Null(startupStore.Value);
    });

    [Fact]
    public void FailedExperienceSaveRestoresTheEffectiveMode() => StaTest.Run(() =>
    {
        using var context = new Context();
        var page = context.Shell.Settings;
        page.BeginTrackingQuickAccessChanges();
        using (var locked = context.LockSettings()) page.UseLiveDesktopSwitching = false;
        Assert.True(page.UseLiveDesktopSwitching);
        page.UseLiveDesktopSwitching = false;
        Assert.Equal(SwitchingExperience.Compatibility, context.Store.Load().SwitchingExperience);
    });
    [Fact]
    public void SkinAndTrayChoicesPersistWithoutCommittingTheFolderDraft() => StaTest.Run(() =>
    {
        using var context = new Context();
        context.Shell.Settings.MeetingDesktopPath = context.NewFolder;
        context.Shell.IsLiquidGlass = true;
        context.Shell.ChangeTrayDoubleClickAction(TrayDoubleClickAction.SwitchDesktop);
        Assert.Equal(SkinPreference.LiquidGlass, context.Store.Load().Skin);
        Assert.Equal(TrayDoubleClickAction.SwitchDesktop, context.Store.Load().TrayDoubleClickAction);
        Assert.Equal(context.OldFolder, context.Store.Load().MeetingDesktopPath);
        context.Shell.IsLiquidGlass = false;
        Assert.Equal(SkinPreference.Classic, context.Store.Load().Skin);
    });

    [Fact]
    public void MissingSessionPermissionDoesNotLaunchAnElevationRequest() => StaTest.Run(() =>
    {
        var permission = new NoPermission();
        using var context = new Context(permission);
        Assert.False(context.Shell.EnsureSwitchPermission());
        Assert.Equal(0, permission.Requests);
        Assert.False(File.Exists(context.Paths.SessionFilePath));
    });

    [Fact]
    public void SkinSaveFailureKeepsTheClassicSkinAndCanBeRetried() => StaTest.Run(() =>
    {
        using var context = new Context();
        using (var file = context.LockSettings()) context.Shell.IsLiquidGlass = true;
        Assert.False(context.Shell.IsLiquidGlass);
        Assert.False(string.IsNullOrWhiteSpace(context.Shell.StatusText));
        context.Shell.IsLiquidGlass = true;
        Assert.Equal(SkinPreference.LiquidGlass, context.Store.Load().Skin);
    });
    [Fact]
    public void SavingAFolderImmediatelyRefreshesTheHomePathAndEffectivePathBinding() => StaTest.Run(() =>
    {
        using var context = new Context();
        var changed = new List<string?>();
        context.Shell.Settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        context.Shell.Settings.MeetingDesktopPath = context.NewFolder;
        context.Shell.Settings.SaveCommand.Execute(null);
        Assert.Equal(context.NewFolder, context.Store.Load().MeetingDesktopPath);
        Assert.Equal(context.NewFolder, context.Shell.Prepare.MeetingDesktopPath);
        Assert.Contains(nameof(SettingsViewModel.EffectiveMeetingDesktopPath), changed);
    });

    [Fact]
    public void LeavingSettingsCommitsOnlyTheValidFolderDraft() => StaTest.Run(() =>
    {
        using var context = new Context();
        context.Shell.NavigateCommand.Execute("Settings");
        context.Shell.Settings.MeetingDesktopPath = context.NewFolder;
        context.Shell.Settings.CapturePreview = true;
        context.Shell.NavigateCommand.Execute("Prepare");
        Assert.Equal(context.NewFolder, context.Store.Load().MeetingDesktopPath);
        Assert.Equal(context.NewFolder, context.Shell.Prepare.MeetingDesktopPath);
        Assert.False(context.Store.Load().CaptureReturnPreview);
    });

    [Fact]
    public void ReturningToTheDefaultFolderPersistsWithoutAnotherSave() => StaTest.Run(() =>
    {
        using var context = new Context();
        context.Shell.Settings.UseDefaultCommand.Execute(null);
        Assert.Equal(context.Paths.MeetingDesktopDirectory, context.Store.Load().MeetingDesktopPath);
        Assert.Equal(context.Paths.MeetingDesktopDirectory, context.Shell.Prepare.MeetingDesktopPath);
    });

    [Fact]
    public void InvalidFolderCannotReplaceTheEffectiveHomePathWhenLeavingSettings() => StaTest.Run(() =>
    {
        using var context = new Context();
        context.Shell.NavigateCommand.Execute("Settings");
        context.Shell.Settings.MeetingDesktopPath = context.Original;
        context.Shell.NavigateCommand.Execute("Prepare");
        Assert.True(context.Shell.Settings.HasValidationProblem);
        Assert.Equal(context.OldFolder, context.Store.Load().MeetingDesktopPath);
        Assert.Equal(context.OldFolder, context.Shell.Prepare.MeetingDesktopPath);
        Assert.False(string.IsNullOrWhiteSpace(context.Shell.StatusText));
    });

    [Fact]
    public void FailedFolderCommitLeavesTheDraftButKeepsTheStoredAndHomePaths() => StaTest.Run(() =>
    {
        using var context = new Context();
        context.Shell.NavigateCommand.Execute("Settings");
        context.Shell.Settings.MeetingDesktopPath = context.NewFolder;
        byte[] before = File.ReadAllBytes(context.Paths.SettingsFilePath);
        using (var file = context.LockSettings()) context.Shell.NavigateCommand.Execute("Prepare");
        Assert.Equal(context.OldFolder, context.Shell.Prepare.MeetingDesktopPath);
        Assert.Equal(context.OldFolder, context.Shell.CurrentSettings.MeetingDesktopPath);
        Assert.Equal(context.NewFolder, context.Shell.Settings.MeetingDesktopPath);
        Assert.Equal(before, File.ReadAllBytes(context.Paths.SettingsFilePath));
        Assert.False(string.IsNullOrWhiteSpace(context.Shell.StatusText));
    });

    [Fact]
    public void HotkeyChangeSavesAndUpdatesTheLiveSettingsWithoutASaveCommand() => StaTest.Run(() =>
    {
        using var context = new Context();
        context.Shell.Settings.BeginTrackingQuickAccessChanges();
        context.Shell.Settings.SelectedQuickHotkeyIndex = (int)QuickSwitchHotkey.ControlAltD;
        Assert.Equal(QuickSwitchHotkey.ControlAltD, context.Store.Load().QuickSwitchHotkey);
        Assert.Equal(QuickSwitchHotkey.ControlAltD, context.Shell.CurrentSettings.QuickSwitchHotkey);
    });

    [Fact]
    public void QuickControlsAlsoSaveMouseConfirmationTrayAndAnimationChanges() => StaTest.Run(() =>
    {
        using var context = new Context();
        var page = context.Shell.Settings;
        page.BeginTrackingQuickAccessChanges();
        page.SelectedMouseGestureIndex = (int)DesktopMouseGesture.Disabled;
        Assert.Equal(DesktopMouseGesture.Disabled, context.Store.Load().DesktopMouseGesture);
        page.ConfirmQuickSwitch = false;
        Assert.False(context.Store.Load().ConfirmQuickSwitch);
        page.CloseToTray = true;
        Assert.True(context.Store.Load().CloseToTray);
        page.SmoothDesktopTransition = false;
        Assert.False(context.Store.Load().SmoothDesktopTransition);
    });

    [Fact]
    public void InitialBindingValuesDoNotRewriteTheFileOrOverrideStoredChoices() => StaTest.Run(() =>
    {
        using var context = new Context();
        byte[] before = File.ReadAllBytes(context.Paths.SettingsFilePath);
        var page = context.Shell.Settings;
        page.SelectedQuickHotkeyIndex = 0;
        page.SelectedMouseGestureIndex = 0;
        page.BeginTrackingQuickAccessChanges();
        Assert.Equal(before, File.ReadAllBytes(context.Paths.SettingsFilePath));
        Assert.Equal((int)QuickSwitchHotkey.ControlAltM, page.SelectedQuickHotkeyIndex);
        Assert.Equal((int)DesktopMouseGesture.MiddleDoubleClick, page.SelectedMouseGestureIndex);
    });

    [Fact]
    public void FailedHotkeySaveRevertsTheSelectionAndCanRetryTheSameChoice() => StaTest.Run(() =>
    {
        using var context = new Context();
        var page = context.Shell.Settings;
        page.BeginTrackingQuickAccessChanges();
        byte[] before = File.ReadAllBytes(context.Paths.SettingsFilePath);
        using (var file = context.LockSettings())
        {
            page.SelectedQuickHotkeyIndex = (int)QuickSwitchHotkey.ControlAltD;
            Assert.Equal((int)QuickSwitchHotkey.ControlAltM, page.SelectedQuickHotkeyIndex);
            Assert.Equal(QuickSwitchHotkey.ControlAltM, context.Shell.CurrentSettings.QuickSwitchHotkey);
            Assert.False(string.IsNullOrWhiteSpace(context.Shell.StatusText));
        }
        Assert.Equal(before, File.ReadAllBytes(context.Paths.SettingsFilePath));
        page.SelectedQuickHotkeyIndex = (int)QuickSwitchHotkey.ControlAltD;
        Assert.Equal(QuickSwitchHotkey.ControlAltD, context.Store.Load().QuickSwitchHotkey);
    });

    [Fact]
    public void BusyOrInvalidQuickEditsDoNotLeaveUnsavedSelections() => StaTest.Run(() =>
    {
        using var context = new Context();
        var page = context.Shell.Settings;
        page.BeginTrackingQuickAccessChanges();
        page.SelectedQuickHotkeyIndex = -1;
        page.SelectedMouseGestureIndex = 999;
        Assert.Equal((int)QuickSwitchHotkey.ControlAltM, page.SelectedQuickHotkeyIndex);
        Assert.Equal((int)DesktopMouseGesture.MiddleDoubleClick, page.SelectedMouseGestureIndex);
        context.Shell.IsBusy = true;
        page.SelectedQuickHotkeyIndex = (int)QuickSwitchHotkey.ControlAltD;
        Assert.Equal((int)QuickSwitchHotkey.ControlAltM, page.SelectedQuickHotkeyIndex);
        Assert.Equal(QuickSwitchHotkey.ControlAltM, context.Store.Load().QuickSwitchHotkey);
    });

    [Fact]
    public void SavingAHotkeyDoesNotCommitOtherSettingsDrafts() => StaTest.Run(() =>
    {
        using var context = new Context();
        var page = context.Shell.Settings;
        page.BeginTrackingQuickAccessChanges();
        page.MeetingDesktopPath = context.NewFolder;
        page.CapturePreview = true;
        page.SelectedQuickHotkeyIndex = (int)QuickSwitchHotkey.ControlAltD;
        Assert.Equal(QuickSwitchHotkey.ControlAltD, context.Store.Load().QuickSwitchHotkey);
        Assert.Equal(context.OldFolder, context.Store.Load().MeetingDesktopPath);
        Assert.False(context.Store.Load().CaptureReturnPreview);
    });

    private sealed class Context : IDisposable
    {
        public AppPaths Paths { get; }
        public SettingsStore Store { get; }
        public MainViewModel Shell { get; }
        public string Original { get; }
        public string OldFolder { get; }
        public string NewFolder { get; }
        public Context(IElevationBroker? elevation = null, IStartupRegistrationStore? startupStore = null)
        {
            Paths = AppPaths.Resolve(["--data-dir", Path.Combine(Path.GetTempPath(), "MeetingDesktopSettingsTests", Guid.NewGuid().ToString("N"))]);
            Paths.EnsureCreated();
            Original = Directory.CreateDirectory(Path.Combine(Paths.DataDirectory, "Original")).FullName;
            OldFolder = Directory.CreateDirectory(Path.Combine(Paths.DataDirectory, "MeetingOld")).FullName;
            NewFolder = Directory.CreateDirectory(Path.Combine(Paths.DataDirectory, "MeetingNew")).FullName;
            Store = new(Paths.SettingsFilePath);
            Store.Save(new AppSettings { MeetingDesktopPath = OldFolder, CaptureReturnPreview = false });
            Shell = new(Paths, Store, new SessionStore(Paths.SessionFilePath), new LayoutStore(Paths.LayoutsDirectory), new Location(Original), elevation ?? new RejectElevation(),
                new WindowsStartupRegistration(@"C:\MeetingDesktop.exe", Paths.DataDirectory, startupStore ?? new MemoryStartupStore()));
        }
        public FileStream LockSettings() => new(Paths.SettingsFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        public void Dispose() { Shell.Dispose(); Directory.Delete(Paths.DataDirectory, true); }
    }
    private sealed class MemoryStartupStore : IStartupRegistrationStore
    {
        public StartupRegistrationValue? Value;
        public StartupRegistrationValue? Read() => Value;
        public void Write(StartupRegistrationValue value) => Value = value;
        public void Delete() => Value = null;
    }
    private sealed class Location(string original) : DesktopLocationService(new DesktopPathService())
    { public override DesktopLocation Detect(string? meetingDesktopDirectory = null) => new(DesktopLocationStatus.Agreed, original, [], false); }
    private sealed class RejectElevation : IElevationBroker
    { public ElevationOutcome Run(string argument) => throw new InvalidOperationException("Real operations are forbidden in this test."); }

    private sealed class NoPermission : ISessionElevationBroker
    {
        public int Requests;
        public bool IsAuthorized => false;
        public bool IsAuthorizing => false;
        public event EventHandler? AuthorizationChanged { add { } remove { } }
        public Task<ElevationOutcome> AuthorizeAsync(CancellationToken token = default) { Requests++; return Task.FromResult(ElevationOutcome.Failed); }
        public ElevationOutcome Run(string argument) => throw new InvalidOperationException("No unapproved operations.");
        public void Revoke() { }
        public void Dispose() { }
    }
}
