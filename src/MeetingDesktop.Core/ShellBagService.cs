using System.Security;
using Microsoft.Win32;

namespace MeetingDesktop.Core;

/// <summary>
/// Moves the <c>IconLayouts</c> blob, the opaque binary record Windows keeps
/// for the desktop icon layout.
/// </summary>
/// <remarks>
/// <para>
/// The value lives at <c>HKCU\Software\Microsoft\Windows\Shell\Bags\1\Desktop</c>
/// as <c>REG_BINARY</c>. Its format is undocumented and has changed between
/// Windows versions, so this class never parses it. It reads bytes, writes
/// bytes, and copies them to and from a file - nothing else.
/// </para>
/// <para>
/// <b>explorer must be stopped before <see cref="WriteIconLayouts"/> is
/// called.</b> explorer reads this blob when it starts and writes its own
/// in-memory copy back when it exits, so a write performed while explorer is
/// running is clobbered on the way out and the restore appears to do nothing at
/// all. The ordering - stop explorer, write the blob, start explorer, then
/// apply positions - belongs to the workflow layer, because only it knows
/// whether explorer is currently down. This class cannot detect the violation
/// and deliberately does not try to enforce it; a guard here could only fail
/// spuriously or block the one caller that is doing it correctly.
/// </para>
/// <para>
/// <b>This blob is the secondary record.</b> It is bound to the monitor
/// arrangement it was captured under: change the resolution, plug in the
/// projector, or undock the laptop and the coordinates inside it no longer mean
/// what they meant. The primary record is the name-based
/// <see cref="MeetingDesktop.Core.Models.LayoutSnapshot"/>, which survives that
/// change and is what <see cref="DesktopIconPlacer"/> applies. The blob only
/// restores the frame of reference the positions were captured against.
/// Restoring the blob without the name-based pass, or dropping the name-based
/// pass because "the registry has the layout anyway", is the regression this
/// paragraph exists to prevent.
/// </para>
/// <para>
/// Every method is best-effort and reports failure with <c>null</c> or
/// <c>false</c> instead of throwing. That is not sloppiness: the blob is an
/// optimisation on top of the name-based restore, so a profile where it cannot
/// be read is still a profile the app can put back together. Throwing here
/// would turn a recoverable degradation into a failed restore.
/// </para>
/// <para>
/// Reading and writing emit no progress. The workflow layer reports
/// <see cref="MeetingDesktop.Core.Models.ProgressKey.WritingShellBagBlob"/>
/// around the write, so the key stays in the layer that knows the sequence.
/// </para>
/// </remarks>
public sealed class ShellBagService
{
    /// <summary>The bag Windows keeps the desktop's view state in.</summary>
    /// <remarks>
    /// The <c>1</c> is the bag number, not a version or a per-user index; it is
    /// the desktop's fixed slot and has been since the shell first grew bags.
    /// </remarks>
    private const string BagKeyPath = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";

    /// <summary>The value name, under which the whole layout is one binary blob.</summary>
    private const string IconLayoutsValueName = "IconLayouts";

    /// <summary>
    /// Ceiling on what <see cref="ImportFrom"/> will load. A real blob is a few
    /// kilobytes - roughly 32 bytes per icon. The limit is not about blobs: it
    /// is about a user picking a video file in the import dialog and the app
    /// reading the whole thing into memory as if it were a layout.
    /// </summary>
    private const int MaxImportedBlobBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Reads the blob, or <c>null</c> when there is nothing usable to read.
    /// </summary>
    /// <remarks>
    /// <c>null</c> means "no blob", never "an empty blob". A zero-length
    /// <c>REG_BINARY</c> is not a layout either, and handing it back would have
    /// the caller write it over a good one.
    /// </remarks>
    public byte[]? ReadIconLayouts()
    {
        try
        {
            // Read-only, so a read can never create the key. The default
            // registry view is correct on purpose: WOW64 redirects
            // HKCU\Software\Classes, not HKCU\Software\Microsoft\Windows\Shell,
            // so forcing a 32/64-bit view here would either be a no-op or read
            // the wrong hive.
            using RegistryKey? bag = Registry.CurrentUser.OpenSubKey(BagKeyPath);

            // The type check is not paranoia. Other tools write a value called
            // IconLayouts too, and one of them writes it as text; passing those
            // bytes on as a layout would corrupt the desktop rather than fail
            // cleanly.
            return bag?.GetValue(IconLayoutsValueName) is byte[] { Length: > 0 } blob ? blob : null;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the blob.
    /// </summary>
    /// <param name="blob">
    /// Bytes from <see cref="ReadIconLayouts"/> or <see cref="ImportFrom"/>.
    /// </param>
    /// <returns><c>true</c> when the value was written.</returns>
    /// <remarks>
    /// The caller must have stopped explorer first - see the type remarks.
    /// </remarks>
    public bool WriteIconLayouts(byte[] blob)
    {
        // An empty blob is refused rather than written. Windows treats an empty
        // or truncated IconLayouts as a corrupt bag and resets the desktop to
        // whatever order it likes, so writing one destroys the layout that was
        // there. Doing nothing leaves the previous blob intact, which is the
        // better failure.
        if (blob is null || blob.Length == 0)
        {
            return false;
        }

        try
        {
            // CreateSubKey rather than OpenSubKey: on a profile where explorer
            // has never saved a layout the bag does not exist yet, and creating
            // it costs nothing - explorer adopts whatever key it finds.
            using RegistryKey? bag = Registry.CurrentUser.CreateSubKey(BagKeyPath, writable: true);

            if (bag is null)
            {
                return false;
            }

            bag.SetValue(IconLayoutsValueName, blob, RegistryValueKind.Binary);
            return true;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the blob to a file, so a layout can outlive the registry value
    /// that holds it.
    /// </summary>
    /// <param name="blob">The layout bytes.</param>
    /// <param name="destinationPath">Where to put them. Existing files are replaced.</param>
    /// <returns><c>true</c> when the file was written.</returns>
    /// <remarks>
    /// Written to a temporary file and then moved into place. The blob is
    /// opaque, so a truncated file is undetectable on import - it is a
    /// perfectly well-formed byte array that restores the wrong layout. The
    /// move makes the write atomic, which is the only way to tell a complete
    /// file from a torn one. The caller owns the directory; this method does
    /// not create it, because a missing layout folder is a problem the workflow
    /// layer has to report, not paper over.
    /// </remarks>
    public bool ExportTo(byte[] blob, string destinationPath)
    {
        if (blob is null || blob.Length == 0 || string.IsNullOrWhiteSpace(destinationPath))
        {
            return false;
        }

        string? temporaryPath = null;

        try
        {
            string fullPath = Path.GetFullPath(destinationPath);
            temporaryPath = fullPath + ".tmp";

            File.WriteAllBytes(temporaryPath, blob);
            File.Move(temporaryPath, fullPath, overwrite: true);

            temporaryPath = null;
            return true;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return false;
        }
        finally
        {
            DeleteQuietly(temporaryPath);
        }
    }

    /// <summary>
    /// Loads a blob previously written by <see cref="ExportTo"/>.
    /// </summary>
    /// <param name="sourcePath">The file to read.</param>
    /// <returns>The bytes, or <c>null</c> when the file cannot be used.</returns>
    /// <remarks>
    /// A file that is missing, empty, unreadable or implausibly large returns
    /// <c>null</c> so the caller can keep the current layout instead of
    /// replacing it with nothing.
    /// </remarks>
    public byte[]? ImportFrom(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        try
        {
            // Size is checked before the read, not after, so a wrongly chosen
            // multi-gigabyte file is rejected without being loaded first.
            FileInfo file = new(sourcePath);

            if (!file.Exists || file.Length == 0 || file.Length > MaxImportedBlobBytes)
            {
                return null;
            }

            return File.ReadAllBytes(sourcePath);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// The failures this service treats as "the blob is unavailable".
    /// </summary>
    /// <remarks>
    /// The list is deliberately narrow. A policy block, a locked hive, a bad
    /// path or an I/O error are all cases the app can carry on past, because
    /// the name-based snapshot still restores the layout. Anything else - a bug
    /// in this class, an out-of-memory - must surface instead of being
    /// disguised as "this machine has no saved blob", which is what a blanket
    /// <c>catch (Exception)</c> would do.
    /// </remarks>
    private static bool IsUnavailable(Exception ex) =>
        ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException;

    /// <summary>
    /// Removes a leftover temporary file, ignoring the fact that it may not
    /// exist or may be locked.
    /// </summary>
    /// <remarks>
    /// Used from a <c>finally</c>, where throwing would replace the real
    /// failure with a cleanup failure that says nothing about the write.
    /// </remarks>
    private static void DeleteQuietly(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            // Nothing useful to do; the file is a ".tmp" nobody will import.
        }
    }
}
