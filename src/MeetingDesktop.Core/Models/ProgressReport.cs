namespace MeetingDesktop.Core.Models;

/// <summary>
/// A step in a long-running operation.
/// </summary>
/// <remarks>
/// Core emits keys, never sentences. The UI maps each key to a localized
/// string. The prototype hard-coded Chinese sentences inside the interop
/// layer, which means an English build showed Chinese to a non-Chinese user
/// and no third language was ever possible without touching the interop code.
/// </remarks>
public enum ProgressKey
{
    DetectingDesktopPath,
    ReadingCurrentLayout,
    CapturingPreview,
    WritingLayoutSnapshot,
    RedirectingDesktop,
    MovingPublicShortcuts,
    RestartingExplorer,
    RefreshingDesktop,
    WritingShellBagBlob,
    ApplyingLayout,
    VerifyingLayout,
    RestoringPublicShortcuts,
    ApplyingWallpaper,
    RestoringWallpaper,
    Done,
}

/// <summary>
/// One progress notification.
/// </summary>
/// <param name="Key">Which step this is.</param>
/// <param name="Arguments">
/// Values the UI may interpolate into the localized string - counts, paths.
/// Never a sentence.
/// </param>
/// <param name="Fraction">0..1 when the step has measurable progress, otherwise null.</param>
public sealed record ProgressReport(
    ProgressKey Key,
    object[]? Arguments = null,
    double? Fraction = null);
