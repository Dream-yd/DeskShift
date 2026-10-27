using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

public enum RecoveryRecordState
{
    None,
    Unreadable,
    DesktopUnresolved,
    OriginalDesktopActive,
    MeetingDesktopActive,
    DifferentDesktopActive,
}

/// <summary>Describes a record without assuming that its old icon layout is still wanted.</summary>
public static class RecoveryRecordReview
{
    public static RecoveryRecordState Inspect(DesktopLocation location, bool recordExists, SessionInfo? session)
    {
        if (!recordExists) return RecoveryRecordState.None;
        if (session is null) return RecoveryRecordState.Unreadable;
        if (!location.IsUsable || location.ResolvedPath is null) return RecoveryRecordState.DesktopUnresolved;
        try
        {
            if (string.IsNullOrWhiteSpace(session.OriginalDesktopPath) || string.IsNullOrWhiteSpace(session.MeetingDesktopPathAtSwap) ||
                !Path.IsPathFullyQualified(session.OriginalDesktopPath) || !Path.IsPathFullyQualified(session.MeetingDesktopPathAtSwap) ||
                !Path.IsPathFullyQualified(location.ResolvedPath)) return RecoveryRecordState.DesktopUnresolved;
            if (ShellViewReadiness.SamePath(session.OriginalDesktopPath, session.MeetingDesktopPathAtSwap))
                return RecoveryRecordState.DesktopUnresolved;
            if (ShellViewReadiness.SamePath(location.ResolvedPath, session.OriginalDesktopPath))
                return RecoveryRecordState.OriginalDesktopActive;
            return ShellViewReadiness.SamePath(location.ResolvedPath, session.MeetingDesktopPathAtSwap)
                ? RecoveryRecordState.MeetingDesktopActive : RecoveryRecordState.DifferentDesktopActive;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return RecoveryRecordState.DesktopUnresolved;
        }
    }
}
