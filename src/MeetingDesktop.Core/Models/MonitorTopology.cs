namespace MeetingDesktop.Core.Models;

/// <summary>One display, in virtual-desktop coordinates.</summary>
/// <param name="DeviceName">e.g. <c>\\.\DISPLAY1</c>.</param>
/// <param name="X">Left edge in virtual-desktop coordinates; negative when left of the primary.</param>
/// <param name="Y">Top edge in virtual-desktop coordinates.</param>
/// <param name="Width">Width in physical pixels.</param>
/// <param name="Height">Height in physical pixels.</param>
/// <param name="IsPrimary">Whether Windows treats this as the primary display.</param>
public sealed record MonitorInfo(
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary);

/// <summary>
/// The display arrangement a layout snapshot was taken under.
/// </summary>
/// <remarks>
/// This exists because desktop icon coordinates are <em>virtual desktop</em>
/// coordinates, and their origin moves with the monitor arrangement. Attaching
/// a second monitor to the left of the primary shifts every icon's X by that
/// monitor's width, so the same icon in the same visual spot has a different
/// X than it did before.
///
/// Recording the topology lets restore detect the change and say so, instead
/// of silently placing icons at coordinates that no longer mean what they
/// meant when they were saved. That silent mis-placement is exactly the bug
/// that prompted this project.
/// </remarks>
public sealed record MonitorTopology(IReadOnlyList<MonitorInfo> Monitors)
{
    public static MonitorTopology Empty { get; } = new(Array.Empty<MonitorInfo>());

    /// <summary>
    /// A cheap fingerprint: geometry and primary display, ignoring device names
    /// (which change between sessions for the same physical monitor).
    /// </summary>
    public string Signature =>
        string.Join(
            ";",
            Monitors
                .OrderBy(m => m.X)
                .ThenBy(m => m.Y)
                .ThenBy(m => m.Width).ThenBy(m => m.Height).ThenBy(m => m.IsPrimary)
                .Select(m => $"{m.X},{m.Y},{m.Width},{m.Height},{m.IsPrimary}"));

    /// <summary>
    /// True when this topology describes the same display geometry as
    /// <paramref name="other"/>.
    /// </summary>
    public bool Matches(MonitorTopology? other) =>
        other is not null && Monitors.Count > 0 && other.Monitors.Count > 0 &&
        Signature == other.Signature;
}
