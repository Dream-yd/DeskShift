using System.IO;
using MeetingDesktop.App.ViewModels;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.App.Tests;

public sealed class QuickAccessSettingsTests
{
    [Fact]
    public void SavesShortcutChoicesEvenWhenAnOldRecordBlocksFolderEditing() => StaTest.Run(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetingDesktopQuickAccessTests", Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths paths = AppPaths.Resolve(["--data-dir", root]);
            paths.EnsureCreated();
            var settings = new SettingsStore(paths.SettingsFilePath);
            settings.Save(new AppSettings { MeetingDesktopPath = Path.Combine(root, "Meeting"), CaptureReturnPreview = false });
            var sessions = new SessionStore(paths.SessionFilePath);
            sessions.Save(new SessionInfo { OriginalDesktopPath = root, MeetingDesktopPathAtSwap = Path.Combine(root, "Meeting") });
            byte[] original = File.ReadAllBytes(paths.SessionFilePath);
            using var shell = new MainViewModel(paths, settings, sessions, new LayoutStore(paths.LayoutsDirectory), new FixedLocation(root), new RejectElevation());
            Assert.False(shell.Settings.CanEditMeetingDesktopPath);
            Assert.True(shell.Settings.SaveQuickAccessCommand.CanExecute(null));
            shell.Settings.SelectedQuickHotkeyIndex = (int)QuickSwitchHotkey.ControlAltD;
            shell.Settings.SelectedMouseGestureIndex = (int)DesktopMouseGesture.LeftDoubleClickBlank;
            shell.Settings.ConfirmQuickSwitch = false;
            shell.Settings.CloseToTray = true;
            shell.Settings.SaveQuickAccessCommand.Execute(null);
            AppSettings saved = settings.Load();
            Assert.Equal(QuickSwitchHotkey.ControlAltD, saved.QuickSwitchHotkey);
            Assert.Equal(DesktopMouseGesture.LeftDoubleClickBlank, saved.DesktopMouseGesture);
            Assert.False(saved.ConfirmQuickSwitch);
            Assert.True(saved.CloseToTray);
            Assert.Equal(saved.QuickSwitchHotkey, shell.CurrentSettings.QuickSwitchHotkey);
            Assert.Equal(saved.MeetingDesktopPath, shell.CurrentSettings.MeetingDesktopPath);
            Assert.Equal(original, File.ReadAllBytes(paths.SessionFilePath));
            shell.IsBusy = true;
            Assert.False(shell.Settings.SaveQuickAccessCommand.CanExecute(null));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    private sealed class FixedLocation(string original) : DesktopLocationService(new DesktopPathService())
    {
        public override DesktopLocation Detect(string? meetingDesktopDirectory = null) => new(DesktopLocationStatus.Agreed, original, [], false);
    }

    private sealed class RejectElevation : IElevationBroker
    {
        public ElevationOutcome Run(string argument) => throw new InvalidOperationException("Real operations are forbidden in this test.");
    }
}
