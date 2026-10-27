namespace MeetingDesktop.Core.Models;

/// <summary>An icon that ended up somewhere other than where it was asked to go.</summary>
public sealed record IconMismatch(string Name, int WantedX, int WantedY, int ActualX, int ActualY);

/// <summary>
/// Outcome of a placement operation.
/// </summary>
/// <remarks>
/// The prototype returned a pre-formatted multi-line string, which is why the
/// UI could not highlight which icons failed. Structured results let the
/// caller decide what is worth showing, and in which language.
/// </remarks>
/// <param name="Requested">How many icons the caller asked to place.</param>
/// <param name="Placed">How many were found on the desktop and moved.</param>
/// <param name="OffTarget">Icons that did not end up at the requested position.</param>
/// <param name="NotFound">Requested names that are not on the desktop.</param>
/// <param name="UsedTwoPhase">Whether the park-then-place path ran.</param>
/// <param name="ParkingSucceeded">
/// Whether the parking pass actually moved every icon to a distinct cell.
/// False means the placement fell back to a single pass, which can cascade.
/// </param>
public sealed record PlaceResult(
    int Requested,
    int Placed,
    IReadOnlyList<IconMismatch> OffTarget,
    IReadOnlyList<string> NotFound,
    bool UsedTwoPhase,
    bool ParkingSucceeded)
{
    /// <summary>True when every requested icon is exactly where it was asked to go.</summary>
    public bool IsExact =>
        NotFound.Count == 0 &&
        OffTarget.Count == 0 &&
        Placed == Requested;
}
