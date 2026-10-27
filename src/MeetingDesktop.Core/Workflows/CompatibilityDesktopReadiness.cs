using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Workflows;

internal sealed record CompatibilityDesktopSnapshot(
    string? FolderPath,
    IReadOnlyList<IconPosition> ShellIcons,
    IReadOnlyList<IconPosition> CompatibilityIcons)
{
    public string? KnownFolderPath { get; init; }
    public nint ViewWindow { get; init; }
    public uint ProcessId { get; init; }
}

internal sealed record CompatibilityDesktopContents(
    IReadOnlyList<string> RequiredFiles, IReadOnlyList<string> OptionalFiles);

internal sealed record CompatibilityBindingProof(string TargetPath, nint ViewWindow, uint ProcessId)
{
    public string? MarkerPath { get; init; }
}

/// <summary>A window or a Known Folder value alone cannot prove the Shell binding.</summary>
internal static class CompatibilityDesktopReadiness
{
    internal static CompatibilityDesktopSnapshot Wait(
        string targetPath, string publicPath, IReadOnlyList<IconPosition>? expectedIcons,
        Func<CancellationToken, CompatibilityDesktopSnapshot> capture,
        Func<CompatibilityDesktopContents> contents,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        Action<CancellationToken>? pause = null,
        CompatibilityBindingProof? bindingProof = null)
    {
        var watch = Stopwatch.StartNew();
        string? previous = null;
        int stable = 0;
        Exception? lastError = null;
        string lastState = "No active Shell folder snapshot.";
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = capture(cancellationToken);
                var files = contents();
                bool ready = IsReady(targetPath, publicPath, expectedIcons, snapshot, files, bindingProof);
                string fingerprint = JsonSerializer.Serialize(snapshot.ShellIcons
                    .OrderBy(i => i.Identity, StringComparer.OrdinalIgnoreCase)
                    .Select(i => new { i.Identity, i.Name, i.IsFileSystem }));
                stable = ready ? (fingerprint == previous ? stable + 1 : 1) : 0;
                previous = ready ? fingerprint : null;
                lastState = $"Active folder='{snapshot.FolderPath ?? "virtual Desktop"}', Known Folder='{snapshot.KnownFolderPath ?? "unverified"}', Shell={snapshot.ShellIcons.Count}, " +
                    $"listview={snapshot.CompatibilityIcons.Count}, saved={expectedIcons?.Count.ToString() ?? "none"}, stable={stable}.";
                if (stable >= 3)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return snapshot;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or
                DesktopInteropException or IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                stable = 0;
                previous = null;
            }

            if (watch.Elapsed >= (timeout ?? TimeSpan.FromSeconds(15))) break;
            if (pause is not null) pause(cancellationToken);
            else if (cancellationToken.WaitHandle.WaitOne(150)) cancellationToken.ThrowIfCancellationRequested();
        } while (true);

        throw new DesktopViewVerificationException("The active Shell folder and complete desktop icon listing could not be verified. " + lastState, lastError);
    }

    internal static bool IsReady(string targetPath, string publicPath, IReadOnlyList<IconPosition>? expectedIcons,
        CompatibilityDesktopSnapshot snapshot, CompatibilityDesktopContents files, CompatibilityBindingProof? bindingProof = null)
    {
        bool physicalBinding = ShellViewReadiness.SamePath(snapshot.FolderPath, targetPath);
        if (!physicalBinding && snapshot.FolderPath is not null) return false;
        var requiredTarget = files.RequiredFiles.Where(path => ShellViewReadiness.SamePath(Path.GetDirectoryName(path), targetPath)).ToArray();
        var optionalTarget = files.OptionalFiles.Where(path => ShellViewReadiness.SamePath(Path.GetDirectoryName(path), targetPath)).ToArray();
        if (!ShellViewReadiness.Matches(targetPath, publicPath, requiredTarget, snapshot.ShellIcons, optionalTarget)) return false;

        var visible = snapshot.ShellIcons.Where(i => Path.IsPathFullyQualified(i.Identity!))
            .Select(i => Path.GetFullPath(i.Identity!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var required = files.RequiredFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowed = required.Concat(files.OptionalFiles.Select(Path.GetFullPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!required.IsSubsetOf(visible) || !visible.IsSubsetOf(allowed)) return false;

        // The real desktop folder is often the virtual Desktop's empty PIDL.
        // Complete parsed contents prove a nonempty target independently of the
        // Known Folder cache. Public/virtual icons alone cannot prove an empty one.
        bool targetContentsProveBinding = visible.Any(path => ShellViewReadiness.SamePath(Path.GetDirectoryName(path), targetPath));
        bool sameProvenView = bindingProof is not null && bindingProof.ViewWindow != 0 && bindingProof.ProcessId != 0 &&
            bindingProof.ViewWindow == snapshot.ViewWindow && bindingProof.ProcessId == snapshot.ProcessId &&
            ShellViewReadiness.SamePath(bindingProof.TargetPath, targetPath);
        bool ownMarkerVisible = bindingProof?.MarkerPath is { } marker &&
            ShellViewReadiness.SamePath(bindingProof.TargetPath, targetPath) &&
            ShellViewReadiness.SamePath(Path.GetDirectoryName(marker), targetPath) && visible.Contains(Path.GetFullPath(marker));
        if (!physicalBinding && !(targetContentsProveBinding && ShellViewReadiness.SamePath(snapshot.KnownFolderPath, targetPath)) &&
            !ownMarkerVisible && !sameProvenView) return false;

        // The compatibility placer reads the listview. A complete COM view alone
        // is insufficient while that control is still loading its own entries.
        return snapshot.ShellIcons.Count == snapshot.CompatibilityIcons.Count &&
            ShellIconMatcher.Match(snapshot.ShellIcons, snapshot.CompatibilityIcons).Count == snapshot.ShellIcons.Count &&
            (expectedIcons is null || ShellIconMatcher.Match(expectedIcons, snapshot.CompatibilityIcons).Count == expectedIcons.Count);
    }

    internal static void Verify(string targetPath, string publicPath,
        Func<CompatibilityBindingProof?, CompatibilityDesktopSnapshot> wait,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = EnumerateContents(targetPath, publicPath);
        bool hasOrdinaryTargetEntries = files.RequiredFiles.Any(path => ShellViewReadiness.SamePath(Path.GetDirectoryName(path), targetPath));
        // The caller owns this handle, outside the STA probe. Even if a COM read
        // hangs, timeout/cancellation closes it before the workflow can return.
        using var marker = hasOrdinaryTargetEntries ? null : new FileStream(
            Path.Combine(targetPath, ".meetingdesktop-ready-" + Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete,
            1, FileOptions.DeleteOnClose);
        cancellationToken.ThrowIfCancellationRequested();
        var verified = wait(marker is null ? null : new(targetPath, 0, 0) { MarkerPath = marker.Name });
        if (marker is null) return;

        marker.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        // Require another complete stable listing after our marker disappears.
        // Proof may carry only across the very same Shell view and process.
        wait(new(targetPath, verified.ViewWindow, verified.ProcessId));
    }

    internal static CompatibilityDesktopContents EnumerateContents(string targetPath, string publicPath)
    {
        List<string> required = [], optional = [];
        foreach (string directory in new[] { targetPath, publicPath }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
            {
                if (ShellViewReadiness.SamePath(directory, targetPath)) throw new DirectoryNotFoundException(targetPath);
                continue;
            }
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                ((attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 ? required : optional).Add(path);
            }
        }
        return new(required, optional);
    }
}
