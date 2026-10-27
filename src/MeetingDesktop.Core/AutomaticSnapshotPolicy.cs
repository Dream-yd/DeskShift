using System.Text.Json;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

/// <summary>Conservative eligibility and complete, order-independent capture keys.</summary>
internal static class AutomaticSnapshotPolicy
{
    internal static bool TryNormalizePath(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            // Source provenance is a folder path, never an ADS or a device path.
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
            string remainder = path[Path.GetPathRoot(path)!.Length..];
            return path.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
                remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                    .All(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsValid(LayoutSnapshot snapshot) =>
        snapshot.SchemaVersion == LayoutSnapshot.CurrentSchemaVersion &&
        snapshot.CreatedUtc != default &&
        snapshot.IsAutomatic &&
        TryNormalizePath(snapshot.SourceDesktopPath, out _) &&
        snapshot.Icons is not null &&
        snapshot.Icons.All(icon => icon is not null && !string.IsNullOrWhiteSpace(icon.Name)) &&
        snapshot.Topology?.Monitors is { Count: > 0 } monitors &&
        monitors.All(m => m is not null && !string.IsNullOrWhiteSpace(m.DeviceName) && m.Width > 0 && m.Height > 0) &&
        monitors.Count(m => m.IsPrimary) == 1 &&
        monitors.Select(m => m.DeviceName).Distinct(StringComparer.Ordinal).Count() == monitors.Count;

    // Deserialization supplies defaults for missing fields. That is useful to legacy
    // Load callers, but is not sufficient evidence for reuse or destructive retention.
    internal static bool HasCaptureFields(JsonElement root)
    {
        if (!HasProperties(root, "SchemaVersion", "CreatedUtc", "IsAutomatic", "SourceDesktopPath", "Icons", "Topology") ||
            !HasUniqueProperties(root)) return false;
        JsonElement icons = root.GetProperty("Icons");
        JsonElement topology = root.GetProperty("Topology");
        if (icons.ValueKind != JsonValueKind.Array || !HasProperties(topology, "Monitors")) return false;
        JsonElement monitors = topology.GetProperty("Monitors");
        return monitors.ValueKind == JsonValueKind.Array &&
            icons.EnumerateArray().All(icon => HasProperties(icon, "Name", "X", "Y")) &&
            monitors.EnumerateArray().All(m => HasProperties(m, "DeviceName", "X", "Y", "Width", "Height", "IsPrimary"));
    }

    private static bool HasProperties(JsonElement value, params string[] names) =>
        value.ValueKind == JsonValueKind.Object && names.All(name => value.TryGetProperty(name, out _));

    private static bool HasUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            return value.EnumerateObject().All(p => names.Add(p.Name) && HasUniqueProperties(p.Value));
        }
        return value.ValueKind != JsonValueKind.Array || value.EnumerateArray().All(HasUniqueProperties);
    }

    // Do not use MonitorTopology.Signature: it intentionally omits device names.
    // JSON encodes strings unambiguously; sorted arrays preserve multiplicity.
    internal static string TopologyKey(LayoutSnapshot snapshot) => JsonSerializer.Serialize(
        snapshot.Topology.Monitors.OrderBy(m => m.DeviceName, StringComparer.Ordinal)
            .ThenBy(m => m.X).ThenBy(m => m.Y).ThenBy(m => m.Width).ThenBy(m => m.Height).ThenBy(m => m.IsPrimary));

    internal static string IconKey(LayoutSnapshot snapshot) => JsonSerializer.Serialize(
        snapshot.Icons.OrderBy(i => i.Identity, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal)
            .ThenBy(i => i.IsFileSystem).ThenBy(i => i.X).ThenBy(i => i.Y));

    internal static bool HasNoReparseAncestors(string directory)
    {
        try
        {
            for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
            {
                if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
