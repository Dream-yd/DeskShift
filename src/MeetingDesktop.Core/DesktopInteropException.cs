namespace MeetingDesktop.Core;

/// <summary>
/// What went wrong, in a form the UI can map to a localized message.
/// </summary>
/// <remarks>
/// The prototype threw <c>Exception</c> with a Chinese sentence baked into
/// the interop layer. That cannot be localized afterwards, and it means the
/// English build shows a Chinese error to a non-Chinese user. The kind is the
/// contract; the wording belongs to the UI layer.
/// </remarks>
public enum InteropFailureKind
{
    /// <summary>No SHELLDLL_DefView -> SysListView32 pair could be located.</summary>
    DesktopViewNotFound,

    /// <summary>explorer.exe is not running, so there is no desktop to talk to.</summary>
    ExplorerNotRunning,

    /// <summary>OpenProcess was refused. Usually an integrity-level mismatch.</summary>
    AccessDenied,

    /// <summary>VirtualAllocEx in explorer.exe failed.</summary>
    RemoteAllocationFailed,

    /// <summary>Reading or writing explorer's memory failed.</summary>
    RemoteMemoryAccessFailed,

    /// <summary>explorer's UI thread did not answer within the timeout.</summary>
    ListViewMessageTimeout,

    /// <summary>explorer is running at a higher integrity level than this process.</summary>
    ExplorerHigherIntegrity,
}

/// <summary>
/// A failure that happened while talking to the Windows shell.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> is English and developer-facing: it goes to
/// the log file and to a "details" expander, never straight to the user.
/// <see cref="Diagnostics"/> carries the window-enumeration dump so a bug
/// report can say what was on screen instead of "it didn't work".
/// </remarks>
public sealed class DesktopInteropException : Exception
{
    public DesktopInteropException(
        InteropFailureKind kind,
        string message,
        IReadOnlyList<string>? diagnostics = null,
        int? win32ErrorCode = null,
        uint? processId = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Diagnostics = diagnostics ?? Array.Empty<string>();
        Win32ErrorCode = win32ErrorCode;
        ProcessId = processId;
    }

    public InteropFailureKind Kind { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    public int? Win32ErrorCode { get; }

    public uint? ProcessId { get; }
}
