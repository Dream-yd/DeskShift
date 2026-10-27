using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Workflows;

/// <summary>Desktop, Explorer, and wallpaper operations used by the workflows.</summary>
/// <remarks>
/// Workflow tests replace this boundary to verify operation order and recovery
/// without modifying the real desktop or stopping the test runner's Explorer.
/// </remarks>
internal interface IDesktopOperations
{
    IReadOnlyList<IconPosition> ReadShellIcons(CancellationToken cancellationToken);
    void SwitchLiveDesktop(string desktopPath, CancellationToken cancellationToken);
    PlaceResult PlaceShellIcons(IReadOnlyList<IconPosition> icons, CancellationToken cancellationToken);

    /// <summary>Verifies the active Shell folder and stable, complete compatibility icon enumeration.
    /// Null expectedIcons validates target contents; a saved capture additionally requires every saved icon.
    /// Empty targets use an owned DeleteOnClose marker, removed and verified absent before returning.</summary>
    void WaitForCompatibleDesktop(string targetPath, IReadOnlyList<IconPosition>? expectedIcons, CancellationToken cancellationToken);

    /// <summary>Reads the current icon captions and coordinates.</summary>
    IReadOnlyList<IconPosition> ReadIcons(CancellationToken cancellationToken);

    /// <summary>Moves the named icons to the requested coordinates.</summary>
    PlaceResult PlaceIcons(IReadOnlyList<IconPosition> icons, CancellationToken cancellationToken);

    /// <summary>
    /// Snapshots the desktop and writes it as a PNG, returning the path, or
    /// null when no preview could be produced.
    /// </summary>
    string? CapturePreview(string destinationPath);

    /// <summary>The wallpaper as it is right now.</summary>
    WallpaperState ReadWallpaper();

    /// <summary>Applies a wallpaper, returning false when it did not take.</summary>
    bool ApplyWallpaper(WallpaperState state);

    /// <summary>Points the shell's desktop at <paramref name="desktopPath"/>.</summary>
    void WriteDesktopPath(string desktopPath);

    /// <summary>The native <c>IconLayouts</c> blob, or null when unreadable.</summary>
    byte[]? ReadIconLayoutBlob();

    /// <summary>Copies a blob to a file. Returns false on failure.</summary>
    bool ExportIconLayoutBlob(byte[] blob, string destinationPath);

    /// <summary>Reads a blob back from a file, or null when unreadable.</summary>
    byte[]? ImportIconLayoutBlob(string sourcePath);

    /// <summary>Writes a blob into the registry. Returns false on failure.</summary>
    bool WriteIconLayoutBlob(byte[] blob);

    /// <summary>True when at least one explorer.exe process exists.</summary>
    bool IsExplorerRunning();

    /// <summary>Starts explorer. Verify its folder and icons separately before reporting readiness.</summary>
    void StartExplorer();

    /// <summary>Terminates every explorer.exe process.</summary>
    void StopExplorer();

    /// <summary>Stops the shell, then starts one.</summary>
    void RestartExplorer();
}

/// <summary>
/// The real implementation: every member forwards to the service that owns it.
/// </summary>
internal sealed class DesktopOperations : IDesktopOperations
{
    public void WaitForCompatibleDesktop(string targetPath, IReadOnlyList<IconPosition>? expectedIcons, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        string publicPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        try
        {
            CompatibilityDesktopReadiness.Verify(targetPath, publicPath,
                proof => WaitOnSta(targetPath, publicPath, expectedIcons, proof, token), token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DesktopViewVerificationException("The active Shell folder could not be verified within 20 seconds.", ex);
        }
        finally { timeout.Cancel(); }
    }

    private static CompatibilityDesktopSnapshot WaitOnSta(string targetPath, string publicPath,
        IReadOnlyList<IconPosition>? expectedIcons, CompatibilityBindingProof? proof, CancellationToken token)
    {
        var completion = new TaskCompletionSource<CompatibilityDesktopSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var snapshot = CompatibilityDesktopReadiness.Wait(targetPath, publicPath, expectedIcons, CompatibilityShellView.Capture,
                    () => CompatibilityDesktopReadiness.EnumerateContents(targetPath, publicPath), token, bindingProof: proof);
                completion.TrySetResult(snapshot);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Compatibility desktop verification" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // This background reader has no setters or placement operations to run
        // later. Its caller owns and closes any marker even when COM hangs.
        return completion.Task.WaitAsync(token).GetAwaiter().GetResult();
    }
    public IReadOnlyList<IconPosition> ReadShellIcons(CancellationToken cancellationToken) =>
        DesktopShellWorker.Capture(cancellationToken);
    public void SwitchLiveDesktop(string desktopPath, CancellationToken cancellationToken) =>
        DesktopShellWorker.SwitchTo(desktopPath, cancellationToken);
    public PlaceResult PlaceShellIcons(IReadOnlyList<IconPosition> icons, CancellationToken cancellationToken) =>
        DesktopShellWorker.Place(icons, cancellationToken);

    private readonly ShellBagService _shellBags = new();
    private readonly WallpaperService _wallpapers = new();
    private readonly DesktopPreviewService _preview = new();
    private readonly DesktopPathService _desktopPaths = new();

    public IReadOnlyList<IconPosition> ReadIcons(CancellationToken cancellationToken) =>
        DesktopIconReader.Read(cancellationToken);

    public PlaceResult PlaceIcons(IReadOnlyList<IconPosition> icons, CancellationToken cancellationToken) =>
        DesktopIconPlacer.Place(icons, cancellationToken);

    public string? CapturePreview(string destinationPath)
    {
        PreviewImage? image = _preview.Capture();

        return image is not null && _preview.SavePng(image, destinationPath) ? destinationPath : null;
    }

    public WallpaperState ReadWallpaper() => _wallpapers.Read();

    public bool ApplyWallpaper(WallpaperState state) => _wallpapers.Apply(state);

    public void WriteDesktopPath(string desktopPath) => _desktopPaths.Write(desktopPath);

    public byte[]? ReadIconLayoutBlob() => _shellBags.ReadIconLayouts();

    public bool ExportIconLayoutBlob(byte[] blob, string destinationPath) =>
        _shellBags.ExportTo(blob, destinationPath);

    public byte[]? ImportIconLayoutBlob(string sourcePath) => _shellBags.ImportFrom(sourcePath);

    public bool WriteIconLayoutBlob(byte[] blob) => _shellBags.WriteIconLayouts(blob);

    public bool IsExplorerRunning() => ExplorerService.IsRunning();

    public void StartExplorer() => ExplorerService.Start();

    public void StopExplorer() => ExplorerService.StopAll();

    public void RestartExplorer() => ExplorerService.StopThenStart();
}
