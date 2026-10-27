namespace MeetingDesktop.Core.Models;

/// <summary>
/// One candidate window found while looking for the desktop icon listview.
/// </summary>
/// <remarks>
/// The prototype threw this data away into a single formatted string and only
/// produced it when the search had already failed. Keeping it structured means
/// the Diagnostics page can always show it, which is the difference between a
/// bug report that says "it doesn't work" and one that can be acted on.
/// </remarks>
/// <param name="Hwnd">Window handle, for cross-referencing with a window inspector.</param>
/// <param name="ProcessId">Owning process.</param>
/// <param name="ClassName">Window class, e.g. <c>SHELLDLL_DefView</c>.</param>
/// <param name="Title">Window text. The desktop's own view has an empty title.</param>
/// <param name="UnderDesktopHost">
/// True when this window is a descendant of a <c>Progman</c> or <c>WorkerW</c>
/// top-level window, i.e. it belongs to the desktop rather than to a File
/// Explorer window.
/// </param>
public sealed record DiagnosticEntry(
    nint Hwnd,
    uint ProcessId,
    string ClassName,
    string Title,
    bool UnderDesktopHost);
