using Microsoft.Win32;
using MeetingDesktop.Core.Interop;

namespace MeetingDesktop.Core;

/// <summary>
/// The desktop wallpaper, as a set of values that can be put back exactly.
/// </summary>
/// <param name="Path">Full path to the image, or null when the desktop is a solid colour.</param>
/// <param name="Style">
/// Fit style, verbatim from the registry: 0 centre, 2 stretch, 6 fit, 10 fill,
/// 22 span. Kept as the raw string rather than an enum because the values are
/// not contiguous and an unknown one must survive a round trip untouched.
/// </param>
/// <param name="Tile">"1" tiles the image, "0" does not.</param>
public sealed record WallpaperState(string? Path, string Style, string Tile)
{
    public static WallpaperState Empty { get; } = new(null, "10", "0");

    public bool IsEmpty => string.IsNullOrWhiteSpace(Path);

    /// <summary>True when the two describe the same picture and the same fit.</summary>
    public bool Matches(WallpaperState? other) =>
        other is not null &&
        string.Equals(Path ?? string.Empty, other.Path ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
        Style == other.Style &&
        Tile == other.Tile;
}

/// <summary>
/// Reads and applies the desktop wallpaper.
/// </summary>
/// <remarks>
/// <para>
/// Worth treating with the same care as the icon layout, and for the same
/// reason: it is a piece of the user's own environment that a swap changes, and
/// a change nobody wrote down is a change nobody can undo. The PowerShell
/// prototype never touched the wallpaper; this does, so it has to put it back.
/// </para>
/// <para>
/// All of this is <c>HKCU</c> and needs no elevation.
/// </para>
/// </remarks>
public sealed class WallpaperService
{
    private const string DesktopKeyPath = @"Control Panel\Desktop";

    /// <summary>The wallpaper as it is right now.</summary>
    public WallpaperState Read()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(DesktopKeyPath);

            if (key is null)
            {
                return WallpaperState.Empty;
            }

            return new WallpaperState(
                Normalize(key.GetValue("WallPaper") as string),
                key.GetValue("WallpaperStyle") as string ?? "10",
                key.GetValue("TileWallpaper") as string ?? "0");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return WallpaperState.Empty;
        }
    }

    /// <summary>
    /// Applies a wallpaper, or clears it when <paramref name="state"/> has no path.
    /// </summary>
    /// <remarks>
    /// Returns false rather than throwing. The wallpaper is a cosmetic part of
    /// the swap - a conference can go ahead with the wrong background - so a
    /// failure here must not abort a prepare that has already moved the public
    /// shortcuts and is about to repoint the desktop. The caller reports it and
    /// carries on.
    /// </remarks>
    public bool Apply(WallpaperState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            // The fit style is not touched by SystemParametersInfo, so it is
            // written first - otherwise the new image is briefly drawn with the
            // previous fit, which is visible on a projector.
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(DesktopKeyPath, writable: true))
            {
                if (key is not null)
                {
                    key.SetValue("WallpaperStyle", state.Style, RegistryValueKind.String);
                    key.SetValue("TileWallpaper", state.Tile, RegistryValueKind.String);
                }
            }

            if (state.IsEmpty)
            {
                // A solid colour: an empty path is how Windows is told there is
                // no image, and the background colour comes from a separate
                // value this deliberately leaves alone.
                return NativeMethods.SystemParametersInfo(
                    NativeMethods.SPI_SETDESKWALLPAPER,
                    0,
                    string.Empty,
                    NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
            }

            if (!File.Exists(state.Path))
            {
                return false;
            }

            return NativeMethods.SystemParametersInfo(
                NativeMethods.SPI_SETDESKWALLPAPER,
                0,
                state.Path!,
                NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies a chosen wallpaper into the app's own data folder and returns the
    /// new path.
    /// </summary>
    /// <remarks>
    /// A wallpaper picked from a downloads folder or a camera card is a path
    /// that can disappear, and a wallpaper pointing at a missing file shows as
    /// a black screen on the projector. Keeping the app's own copy makes the
    /// setting durable in the same way the layout snapshot is.
    /// </remarks>
    /// <returns>The stable path, or null when the copy failed.</returns>
    public string? Import(string sourcePath, string assetsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsDirectory);

        try
        {
            if (!File.Exists(sourcePath))
            {
                return null;
            }

            Directory.CreateDirectory(assetsDirectory);

            // Keep the extension: Windows picks its image decoder by content,
            // but a mismatched extension confuses the shell preview and any
            // human who looks at the folder.
            string extension = Path.GetExtension(sourcePath);
            // Profiles retain these paths. Every import gets its own immutable
            // asset, including when the source is itself a previously imported image.
            string destination = Path.Combine(assetsDirectory, $"meeting-wallpaper-{Guid.NewGuid():N}{extension}");
            string temp = destination + ".tmp";
            try
            {
                File.Copy(sourcePath, temp, overwrite: false);
                File.Move(temp, destination, overwrite: false);
            }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Expands variables and rejects an empty or whitespace value.</summary>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // The stored value can be REG_EXPAND_SZ - a wallpaper under a roaming
        // profile is commonly written as %USERPROFILE%\... - and comparing or
        // applying an unexpanded path silently fails.
        return Environment.ExpandEnvironmentVariables(value.Trim());
    }
}
