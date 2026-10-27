namespace MeetingDesktop.Core.Models;

/// <summary>
/// One desktop icon and where it sits.
/// </summary>
/// <param name="Name">The icon's caption - the file name, or a virtual item's display name.</param>
/// <param name="X">Virtual-desktop X, in physical pixels.</param>
/// <param name="Y">Virtual-desktop Y, in physical pixels.</param>
/// <remarks>
/// The coordinates are <em>virtual desktop</em> coordinates, not screen
/// pixels. Their origin moves with the monitor topology - attaching a second
/// monitor to the left of the primary shifts every X by that monitor's width.
/// This is exactly why layouts are saved by name as well as by position: the
/// name survives a topology change, the raw coordinates do not.
/// </remarks>
public sealed record IconPosition(string Name, int X, int Y, string? Identity = null, bool? IsFileSystem = null);
