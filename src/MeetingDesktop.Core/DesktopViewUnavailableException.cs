namespace MeetingDesktop.Core;

/// <summary>The active view is temporarily absent or changed during enumeration.</summary>
internal sealed class DesktopViewUnavailableException(string message) : Exception(message);
