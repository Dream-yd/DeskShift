using MeetingDesktop.Core.Models;
namespace MeetingDesktop.Core;

/// <summary>Correlates independent Shell and listview snapshots without assuming their index order agrees.</summary>
internal static class CompatibilityIconIdentity
{
    internal static IReadOnlyList<IconPosition> Enrich(IReadOnlyList<IconPosition> icons, IReadOnlyList<IconPosition> shell)
    {
        bool Same(IconPosition a, IconPosition b) => a.X == b.X && a.Y == b.Y &&
            string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return icons.Select(icon =>
        {
            var candidates = shell.Where(other => Same(icon, other)).ToArray();
            return icons.Count(other => Same(icon, other)) == 1 && candidates.Length == 1 &&
                !string.IsNullOrWhiteSpace(candidates[0].Identity)
                ? icon with { Identity = candidates[0].Identity, IsFileSystem = candidates[0].IsFileSystem }
                : icon;
        }).ToArray();
    }

    internal static IReadOnlyList<IconPosition> TryEnrich(IReadOnlyList<IconPosition> icons, CancellationToken cancellationToken)
    {
        try { return Enrich(icons, DesktopShellWorker.Capture(cancellationToken)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Compatibility remains available when the experimental COM bridge is unavailable.
            // The matcher will then reject ambiguous names rather than guess.
            return icons;
        }
    }
}
