using System.IO;
using System.Windows;
using MeetingDesktop.App.Localization;
using MeetingDesktop.App.Services;
using MeetingDesktop.App.ViewModels;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.App;

/// <summary>Dispatches helper operations and constructs the localized desktop application.</summary>
public partial class App : Application
{
    private Mutex? _instance;
    private InstanceActivation? _activation;
    private MainViewModel? _viewModel;
    private readonly CancellationTokenSource _shellLifetime = new();
    protected override void OnStartup(StartupEventArgs e)
    {
        // Program has already dispatched helper modes without loading WPF resources.

        string user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        _instance = new Mutex(true, @"Local\MeetingDesktop-" + user, out bool firstInstance);
        if (!firstInstance)
        {
            _instance.Dispose();
            _instance = null;
            if (!e.Args.Contains("--start-in-tray", StringComparer.OrdinalIgnoreCase) &&
                !InstanceActivation.TrySignal(InstanceActivation.Name(user, Environment.ProcessPath!)))
            {
                MessageBox.Show("幕间已经在运行。若要使用新版本，请先在右下角托盘图标的菜单中选择“退出”，再打开新版本。\n\nDeskShift is already running. To use this version, exit the running copy from its tray menu first.",
                    "幕间 / DeskShift", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Language must be resolved before any window exists. Setting the UI
        // culture afterwards would leave anything already rendered in the
        // previous language, and a window that opens in the wrong language
        // looks like a bug even after it corrects itself.
        StartupData? data = StartupData.Open(e.Args, out StartupFailure? failure);
        if (data is null)
        {
            LocalizationManager.Instance.ApplyPreference(failure?.Language ?? LanguagePreference.Auto);
            MessageBox.Show(
                LocalizationManager.Instance.Format("StartupDataError", failure?.Detail ?? ""),
                LocalizationManager.Instance["AppTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }

        LocalizationManager.Instance.ApplyPreference(data.Settings.Language);
        ThemeManager.Initialize(data.Settings.Theme);

        var startup = new WindowsStartupRegistration(Environment.ProcessPath!, data.Paths.DataDirectory);
        var viewModel = new MainViewModel(
            data.Paths,
            data.SettingsStore,
            data.Sessions,
            data.Layouts,
            new DesktopLocationService(new DesktopPathService()),
            new SessionElevationBroker(), startup, data.Settings);
        _viewModel = viewModel;

        MainWindow window = new()
        {
            DataContext = viewModel,
            StartInTray = e.Args.Contains("--start-in-tray", StringComparer.OrdinalIgnoreCase),
            ShowActivated = !e.Args.Contains("--start-in-tray", StringComparer.OrdinalIgnoreCase),
        };

        MainWindow = window;
        try
        {
            _activation = InstanceActivation.Listen(InstanceActivation.Name(user, Environment.ProcessPath!), () =>
            {
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(() => { if (window.IsLoaded) window.ShowFromTray(); }));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { /* A second launch will show the existing-instance message instead. */ }
        window.Show();
        _ = WarmShellAsync();
        IconTransitionConnection.WarmUp();
    }

    private async Task WarmShellAsync()
    {
        try { await DesktopShellWorker.WarmUpAsync(_shellLifetime.Token); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or
            TimeoutException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { /* Optional preload; a later command can establish a new connection. */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activation?.Dispose();
        _viewModel?.Dispose();
        IconTransitionConnection.Shutdown();
        _shellLifetime.Cancel();
        DesktopShellWorker.Shutdown();
        _shellLifetime.Dispose();
        ThemeManager.Shutdown();
        if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }
}
