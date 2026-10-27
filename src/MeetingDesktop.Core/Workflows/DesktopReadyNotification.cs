namespace MeetingDesktop.Core.Workflows;

internal static class DesktopReadyNotification
{
    internal static void Send(Action? callback)
    {
        // Presentation is optional. Closing a view must not undo a verified desktop.
        try { callback?.Invoke(); }
        catch (Exception) { }
    }
}
