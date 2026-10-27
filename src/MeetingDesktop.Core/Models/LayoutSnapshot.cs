namespace MeetingDesktop.Core.Models;

/// <summary>
/// A saved set of icon positions.
/// </summary>
/// <remarks>
/// Positions are stored <b>by name</b>, not only as raw coordinates. The name
/// survives a change of screen resolution or monitor arrangement; the
/// coordinates do not. <see cref="Topology"/> records what the coordinates
/// meant at capture time so a mismatch can be detected rather than guessed at.
/// </remarks>
public sealed record LayoutSnapshot
{
    /// <summary>Bumped when the on-disk shape changes in a breaking way.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Display name. Null for the automatic pre-swap snapshot.</summary>
    public string? Name { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>True for snapshots taken automatically before a desktop swap.</summary>
    public bool IsAutomatic { get; init; }

    /// <summary>Source desktop folder; null for legacy snapshots with no provenance.</summary>
    public string? SourceDesktopPath { get; init; }

    public MonitorTopology Topology { get; init; } = MonitorTopology.Empty;

    public IReadOnlyList<IconPosition> Icons { get; init; } = Array.Empty<IconPosition>();

    /// <summary>
    /// A name that is safe to use as a file name: no path separators, no
    /// characters Windows rejects, never empty.
    /// </summary>
    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "layout";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name
            .Select(c => invalid.Contains(c) ? '_' : c)
            .ToArray())
            .Trim()
            .TrimEnd('.');

        return string.IsNullOrEmpty(cleaned) ? "layout" : cleaned;
    }
}
