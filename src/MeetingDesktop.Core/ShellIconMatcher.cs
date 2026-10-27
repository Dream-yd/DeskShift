using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

internal sealed record ShellIconMatch(IconPosition Wanted, int CurrentIndex);

internal static class ShellIconMatcher
{
    internal static IReadOnlyList<ShellIconMatch> Match(
        IReadOnlyList<IconPosition> wanted, IReadOnlyList<IconPosition> current)
    {
        var wantedNames = wanted.ToLookup(icon => icon.Name, StringComparer.OrdinalIgnoreCase);
        var indexed = current.Select((icon, index) => (Icon: icon, Index: index)).ToArray();
        var byIdentity = indexed.ToLookup(item => item.Icon.Identity, StringComparer.OrdinalIgnoreCase);
        var byName = indexed.ToLookup(item => item.Icon.Name, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ShellIconMatch>();
        foreach (var icon in wanted)
        {
            bool hasIdentity = !string.IsNullOrWhiteSpace(icon.Identity);
            if (!hasIdentity && wantedNames[icon.Name].Take(2).Count() != 1) continue;
            var matches = hasIdentity ? byIdentity[icon.Identity] : byName[icon.Name];
            if (matches.Take(2).Count() == 1) candidates.Add(new(icon, matches.First().Index));
        }
        // Do not resolve ambiguity by enumeration order or give two requests one item.
        return candidates.GroupBy(match => match.CurrentIndex)
            .Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
    }
}

internal static class ShellViewReadiness
{
    internal static bool Matches(string targetDirectory, string publicDirectory,
        IReadOnlyList<string> expectedFiles, IReadOnlyList<IconPosition> actual,
        IReadOnlyList<string>? optionalFiles = null, IReadOnlySet<string>? allowedVirtualIdentities = null)
    {
        var visibleTargetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var icon in actual)
        {
            if (string.IsNullOrWhiteSpace(icon.Identity)) return false;
            if (!Path.IsPathFullyQualified(icon.Identity))
            {
                if (icon.IsFileSystem == true || !icon.Identity.StartsWith("::{", StringComparison.Ordinal) ||
                    (allowedVirtualIdentities is not null && !allowedVirtualIdentities.Contains(icon.Identity))) return false;
                continue;
            }
            string? parent = Path.GetDirectoryName(icon.Identity);
            if (SamePath(parent, publicDirectory)) continue;
            if (!SamePath(parent, targetDirectory)) return false;
            if (!visibleTargetPaths.Add(Path.GetFullPath(icon.Identity))) return false;
        }
        var required = expectedFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowed = required.Concat((optionalFiles ?? []).Select(Path.GetFullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return required.IsSubsetOf(visibleTargetPaths) && visibleTargetPaths.IsSubsetOf(allowed);
    }

    internal static bool SamePath(string? first, string second) => first is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
}
