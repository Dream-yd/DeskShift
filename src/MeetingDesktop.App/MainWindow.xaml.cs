using System.Windows.Shell;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using MeetingDesktop.App.Services;
using MeetingDesktop.App.Localization;
using MeetingDesktop.App.ViewModels;

namespace MeetingDesktop.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private DesktopQuickAccess? _quickAccess;
    private bool _exitRequested;
    private readonly LiquidGlassCoordinator _liquidGlass;
    private bool _glassChrome;
    public bool StartInTray { get; init; }

    public MainWindow()
    {
        InitializeComponent();
        _liquidGlass = new LiquidGlassCoordinator(this, DesktopScene);
        LiquidGlassMaterial.SetCoordinator(this, _liquidGlass);
        _liquidGlass.Changed += OnGlassAppearanceChanged;
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is not RadioButton { GroupName: "Nav", CommandParameter: string page } button ||
                    DataContext is not ViewModels.MainViewModel shell) return;
                bool selected = page switch
                {
                    "Prepare" => shell.IsPrepareSelected, "Restore" => shell.IsRestoreSelected,
                    "Layouts" => shell.IsLayoutsSelected, "Settings" => shell.IsSettingsSelected,
                    "Diagnostics" => shell.IsDiagnosticsSelected, _ => true,
                };
                // Automation and keyboard selection must navigate, not just repaint the radio.
                if (!selected && button.Command?.CanExecute(page) == true) button.Command.Execute(page);
            }));
        SizeChanged += (_, _) =>
        {
            SidebarColumn.Width = new GridLength(ActualWidth < 1000 ? 184 : 224);
            // The state badge keeps the complete hint in its tooltip. At short
            // heights reserve the sidebar for navigation, language and state.
            SessionHint.Visibility = ActualHeight < 680 ? Visibility.Collapsed : Visibility.Visible;
        };
        SourceInitialized += (_, _) => Services.NativeBackdrop.Attach(this);
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (DataContext is not MainViewModel shell) return;
        _quickAccess = new DesktopQuickAccess(this);
        _quickAccess.SwitchRequested += (_, _) =>
        {
            if (shell.QuickSwitchCommand.CanExecute(null)) shell.QuickSwitchCommand.Execute(null);
        };
        _quickAccess.OpenRequested += (_, _) => ShowFromTray();
        _quickAccess.TrayDoubleClickActionChanged += action =>
        {
            shell.ChangeTrayDoubleClickAction(action);
            _quickAccess.Apply(shell.CurrentSettings);
        };
        _quickAccess.ExitRequested += (_, _) =>
        {
            if (shell.IsBusy) { ShowFromTray(); return; }
            _exitRequested = true;
            Close();
        };
        _quickAccess.AvailabilityChanged += (_, _) => RefreshQuickAccessStatus(shell);
        shell.PropertyChanged += OnShellChanged;
        shell.QuickSwitchVisibilityRequested += OnQuickSwitchVisibility;
        shell.NotificationRequested += OnNotification;
        _quickAccess.Apply(shell.CurrentSettings);
        _liquidGlass.Apply(shell.CurrentSettings.Skin);
        if (StartInTray)
        {
            if (_quickAccess.IsTrayAvailable) Hide();
            else WindowState = WindowState.Minimized;
        }
    }

    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel shell || _quickAccess is null) return;
        if (e.PropertyName == nameof(MainViewModel.CurrentSettings))
        {
            _quickAccess.Apply(shell.CurrentSettings);
            _liquidGlass.Apply(shell.CurrentSettings.Skin);
        }
        if (e.PropertyName == nameof(MainViewModel.IsBusy) && !shell.IsBusy) _ = _liquidGlass.RefreshAsync();
        if (e.PropertyName == nameof(MainViewModel.SessionStateText))
        {
            _quickAccess.UpdateTooltip();
            RefreshQuickAccessStatus(shell);
        }
    }

    private void RefreshQuickAccessStatus(MainViewModel shell)
    {
        if (_quickAccess is null) return;
        string hotkey = LocalizationManager.Instance[_quickAccess.HotkeyStatus switch
        {
            HotkeyStatus.Active => "QuickHotkeyActive", HotkeyStatus.Unavailable => "QuickHotkeyConflict", _ => "QuickHotkeyDisabled",
        }];
        string tray = LocalizationManager.Instance[_quickAccess.IsTrayAvailable ? "QuickTrayActive" : "QuickTrayUnavailable"];
        shell.Settings.QuickAccessStatus = hotkey + "  ·  " + tray +
            (_quickAccess.IsMouseAvailable ? "" : "  ·  " + LocalizationManager.Instance["QuickMouseUnavailable"]);
        if (!_quickAccess.IsTrayAvailable && !IsVisible) ShowFromTray();
    }

    private void OnQuickSwitchVisibility(bool visible)
    {
        if (visible) ShowFromTray();
        else if (_quickAccess?.IsTrayAvailable == true) Hide();
        else WindowState = WindowState.Minimized;
    }

    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnNotification(string message) => _quickAccess?.ShowNotification(message);
    private void OnSkinSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel shell && SkinSwitch.IsChecked is { } selected && shell.IsLiquidGlass != selected)
            shell.IsLiquidGlass = selected;
    }

    private void OnGlassAppearanceChanged(object? sender, EventArgs e)
    {
        if (_glassChrome == _liquidGlass.IsEnabled) return;
        _glassChrome = _liquidGlass.IsEnabled;
        GlassCaption.Visibility = _glassChrome ? Visibility.Visible : Visibility.Collapsed;
        WindowChrome.SetWindowChrome(this, _glassChrome ? new WindowChrome
        {
            CaptionHeight = 36, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12),
            UseAeroCaptionButtons = false,
        } : null);
    }

    private void OnMinimizeWindow(object sender, System.Windows.Input.ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximizeWindow(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void OnCloseWindow(object sender, System.Windows.Input.ExecutedRoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel shell)
        {
            shell.PropertyChanged -= OnShellChanged;
            shell.QuickSwitchVisibilityRequested -= OnQuickSwitchVisibility;
            shell.NotificationRequested -= OnNotification;
            shell.Dispose();
        }
        _quickAccess?.Dispose();
        _liquidGlass.Changed -= OnGlassAppearanceChanged;
        _liquidGlass.Dispose();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Let the active operation finish or roll back before closing its owner.
        if (DataContext is MainViewModel shell)
        {
            if (shell.IsBusy) e.Cancel = true;
            else if (!_exitRequested && shell.CurrentSettings.CloseToTray && _quickAccess?.IsTrayAvailable == true)
            {
                e.Cancel = true;
                Hide();
            }
        }
        base.OnClosing(e);
    }
}
