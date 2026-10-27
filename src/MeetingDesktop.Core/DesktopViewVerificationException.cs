namespace MeetingDesktop.Core;

/// <summary>
/// The active Explorer view could not be verified. A successful path setter or
/// registry readback is not evidence that the user's desktop actually changed.
/// </summary>
public sealed class DesktopViewVerificationException : TimeoutException
{
    public DesktopViewVerificationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
