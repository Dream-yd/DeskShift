using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Services;

/// <summary>Live switches fade only the icon child. Restart compatibility mode retains its desktop cover.</summary>
internal sealed class SwitchFeedback : IAsyncDisposable
{
    private readonly DesktopTransition _transition;
    private readonly SwitchNotification? _notification;
    private readonly LiveIconTransition? _icons;

    public SwitchFeedback(AppSettings settings, DesktopSwitchMode mode, Func<string> status)
    {
        _transition = new DesktopTransition(ShouldCoverDesktop(mode, settings.SmoothDesktopTransition));
        if (mode == DesktopSwitchMode.Live && settings.SmoothDesktopTransition &&
            System.Windows.SystemParameters.ClientAreaAnimation && !System.Windows.SystemParameters.HighContrast)
            _icons = new LiveIconTransition();
        if (!settings.ShowSwitchNotification) return;
        try
        {
            _notification = new SwitchNotification(status, settings.SmoothDesktopTransition);
            _notification.Show();
        }
        catch (Exception)
        {
            // Optional presentation cannot interrupt a desktop operation.
            _notification?.Dispose();
            _notification = null;
        }
    }

    internal static bool ShouldCoverDesktop(DesktopSwitchMode mode, bool smooth) =>
        mode == DesktopSwitchMode.RestartExplorer && smooth;

    internal static bool ShouldCapturePreview(DesktopSwitchMode mode, bool requested) =>
        mode == DesktopSwitchMode.RestartExplorer && requested;

    public async Task ShowAsync(CancellationToken token)
    {
        if (_icons is not null) await _icons.HideAsync(token);
        await _transition.ShowAsync(token);
    }
    public void Complete() => _notification?.Complete(true);

    public void DesktopReady()
    {
        _icons?.Dispose();
        // DesktopTransition starts its fade synchronously and never waits for it.
        // Keep the status capsule alive until the remaining work finishes.
        _ = _transition.DisposeAsync();
    }

    public ValueTask DisposeAsync()
    {
        // Complete() keeps the success message briefly, without delaying the switch.
        _notification?.Complete(false);
        _icons?.Dispose();
        return _transition.DisposeAsync();
    }
}
