using System.Runtime.InteropServices;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

internal interface ILiveDesktopShell
{
    IReadOnlyList<IconPosition> Capture();
    string ReadDesktopPath();
    string ReadPublicDesktopPath();
    void WriteDesktopPath(string path);
    void NotifySettings();
    void NotifyDirectory(string path);
    void Refresh();
}

/// <summary>Verifies the active view before and after removing its binding marker.</summary>
internal sealed class LiveDesktopSwitcher(ILiveDesktopShell shell, TimeProvider? timeProvider = null,
    Action<TimeSpan>? pause = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan SwitchBudget = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan ProofPollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan PendingPollInterval = TimeSpan.FromMilliseconds(80);

    internal IReadOnlyList<IconPosition> Run(string desktopPath)
    {
        long started = clock.GetTimestamp();
        string target = Path.GetFullPath(desktopPath);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("The target desktop folder is missing.");
        var virtualIdentities = CaptureBaseline(started).Where(icon => icon.IsFileSystem == false)
            .Select(icon => icon.Identity!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string markerPath = Path.Combine(target, ".meetingdesktop-switch-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var marker = new FileStream(markerPath,
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete,
            1, FileOptions.DeleteOnClose))
        {
            if (clock.GetElapsedTime(started) >= SwitchBudget)
                throw new DesktopViewVerificationException("The switch budget expired before changing the desktop path.");
            shell.WriteDesktopPath(target);
            shell.NotifySettings();
            shell.NotifyDirectory(target);
            WaitForReady(target, virtualIdentities, markerPath, markerMustExist: true, started);
        }
        shell.NotifyDirectory(target);
        return WaitForReady(target, virtualIdentities, markerPath, markerMustExist: false, started);
    }

    private IReadOnlyList<IconPosition> CaptureBaseline(long started)
    {
        Exception? lastError = null;
        while (clock.GetElapsedTime(started) < SwitchBudget)
        {
            try { return shell.Capture(); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or DesktopViewUnavailableException)
            { lastError = ex; }
            PauseWithinBudget(started, PendingPollInterval);
        }
        throw new DesktopViewVerificationException("The baseline desktop view was unavailable before switching.", lastError);
    }

    private void PauseWithinBudget(long started, TimeSpan interval)
    {
        var remaining = SwitchBudget - clock.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) return;
        (pause ?? Thread.Sleep)(remaining < interval ? remaining : interval);
    }

    private IReadOnlyList<IconPosition> WaitForReady(string target, IReadOnlySet<string> virtualIdentities,
        string markerPath, bool markerMustExist, long started)
    {
        string publicDesktop = shell.ReadPublicDesktopPath();
        string? previous = null;
        int stable = 0;
        Exception? lastError = null;
        string lastState = "No snapshot.";
        bool refreshPending = false;
        bool refreshed = false;
        // Both phases share this budget, leaving headroom below the helper's
        // 20-second hard limit. A blocked COM call is still bounded by its parent.
        var budget = SwitchBudget;
        while (clock.GetElapsedTime(started) < budget)
        {
            try
            {
                if (refreshPending) { shell.Refresh(); refreshPending = false; refreshed = true; }
                var icons = shell.Capture();
                var files = DesktopFiles(target);
                string fingerprint = string.Join("\n", icons.Select(icon => icon.Identity)
                    .OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase));
                bool markerVisible = icons.Any(icon => icon.IsFileSystem == true &&
                    string.Equals(icon.Identity, markerPath, StringComparison.OrdinalIgnoreCase));
                bool ready = markerVisible == markerMustExist &&
                    ShellViewReadiness.SamePath(shell.ReadDesktopPath(), target) &&
                    ShellViewReadiness.Matches(target, publicDesktop, files.Required, icons, files.Optional, virtualIdentities);
                // Settings/directory notifications may have already rebound the view.
                // Avoid repainting it again when the binding proof is already visible.
                if (!ready && markerMustExist && !refreshed) refreshPending = true;
                lastState = $"Phase={(markerMustExist ? "binding-marker" : "marker-cleanup")}, markerVisible={markerVisible}, " +
                    $"visible={icons.Count}, required={files.Required.Length}, optional={files.Optional.Length}, matches={ready}.";
                stable = ready ? (fingerprint == previous ? stable + 1 : 1) : 0;
                previous = ready ? fingerprint : null;
                if (stable >= 3 && clock.GetElapsedTime(started) < budget) return icons;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or DesktopViewUnavailableException or FileNotFoundException)
            {
                lastError = ex;
                stable = 0;
                previous = null;
                if (markerMustExist && !refreshed) refreshPending = true;
            }
            // A fully matching sample earns a shorter confirmation interval, even
            // if its fingerprint changed. Pending/failed reads reset stability and
            // back off. Three samples remain required, over a shorter time window.
            PauseWithinBudget(started, stable > 0 ? ProofPollInterval : PendingPollInterval);
        }
        throw new DesktopViewVerificationException("Explorer did not show the target desktop within the time limit. " +
            $"No Explorer process was restarted. {lastState}", lastError);
    }

    internal static (string[] Required, string[] Optional) DesktopFiles(string directory)
    {
        var required = new List<string>();
        var optional = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(path);
            ((attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 ? required : optional).Add(path);
        }
        return (required.ToArray(), optional.ToArray());
    }
}
