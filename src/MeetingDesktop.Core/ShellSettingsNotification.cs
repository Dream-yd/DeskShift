namespace MeetingDesktop.Core;

internal static class ShellSettingsNotification
{
    internal static void Send(Func<nint> getShellWindow, Action<nint> send)
    {
        nint window = getShellWindow();
        // HWND_BROADCAST applies its timeout separately to every application.
        // An unavailable Shell is handled by view verification, never a broadcast.
        if (window != 0 && window != 0xffff) send(window);
    }
}
