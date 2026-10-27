using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using MeetingDesktop.Core.Interop;
using MeetingDesktop.Core.Models;
using static MeetingDesktop.Core.Interop.NativeStructs;

namespace MeetingDesktop.Core;

/// <summary>
/// Captures a picture of the desktop itself: wallpaper, icons, nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The capture comes from <c>PrintWindow</c> against the window that hosts the
/// desktop, not from a screen grab. That distinction is the entire reason this
/// class exists. <c>Graphics.CopyFromScreen</c> copies whatever pixels are
/// currently on the monitor, so the preview would show the very window that is
/// displaying the preview - useless to someone about to decide whether the
/// desktop is ready to swap. <c>PrintWindow</c> asks the window to render its
/// own content, so overlapping windows are simply absent from the result.
/// </para>
/// <para>
/// Everything here degrades to <see langword="null"/> instead of throwing. A
/// preview is a convenience shown seconds before a time-critical operation;
/// "no preview available" is an acceptable outcome, a crash is not.
/// </para>
/// </remarks>
public sealed class DesktopPreviewService
{
    /// <summary>
    /// Depth to request from GDI. 32 is the only depth that copies out as
    /// BGRA without a palette or a conversion pass.
    /// </summary>
    private const int BitsPerPixel = 32;

    /// <summary>
    /// Refuse to capture beyond this many pixels. A desktop larger than 8K is
    /// not a preview, and a bogus window rectangle would otherwise turn into a
    /// multi-gigabyte allocation in a library called from the UI thread.
    /// </summary>
    private const long MaxPixels = 64_000_000;

    /// <summary>Bound on the ancestor walk, in case a window chain is ever cyclic.</summary>
    private const int MaxAncestorDepth = 32;

    /// <summary>PNG file signature.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// Renders the desktop into a raw pixel buffer.
    /// </summary>
    /// <returns>
    /// The capture, or <see langword="null"/> when the desktop window could not
    /// be found, refused to render, or produced nothing usable.
    /// </returns>
    public PreviewImage? Capture() => Capture(null);

    /// <summary>Captures a selected display, falling back to the primary if disconnected.</summary>
    public PreviewImage? Capture(string? deviceName)
    {
        try
        {
            nint host = FindHostWindow();
            if (host == nint.Zero)
            {
                return null;
            }

            if (!NativeMethods.GetWindowRect(host, out RECT rect))
            {
                return null;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            {
                return null;
            }

            PreviewImage? whole = CaptureWindow(host, width, height);

            return whole is null ? null : CropToMonitor(whole, rect, deviceName);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // No gdi32 at all. Nothing to report and nothing to recover.
            return null;
        }
    }

    /// <summary>
    /// Trims a capture of the whole virtual desktop down to the primary monitor.
    /// </summary>
    /// <remarks>
    /// The desktop host window spans every monitor, so a raw capture is the
    /// union of all of them - 4480x1440 on a machine with a 2560-wide primary
    /// and a 1920-wide second display. Scaled into a preview panel, that is a
    /// small, unreadable image in which the screen the user actually presents
    /// from occupies a fraction of the frame.
    ///
    /// Cropping to the primary monitor is the honest choice for a preview whose
    /// job is "this is what the projector will show". Icons on a secondary
    /// display are real, but they are not what the audience sees.
    ///
    /// The buffer is bottom-up, which is why the crop reads as a contiguous
    /// slice starting at <c>Height - y - height</c> rather than at <c>y</c>.
    /// </remarks>
    private static PreviewImage? CropToMonitor(PreviewImage source, RECT hostRect, string? deviceName)
    {
        var monitors = MonitorTopologyReader.Capture().Monitors;
        MonitorInfo? primary = monitors.FirstOrDefault(m => m.DeviceName == deviceName)
            ?? monitors.FirstOrDefault(m => m.IsPrimary);

        if (primary is null)
        {
            return source;
        }

        // The host rect's origin can be negative - a monitor left of the
        // primary puts the virtual desktop into negative X - so the crop is
        // expressed relative to it rather than in absolute screen coordinates.
        int x = primary.X - hostRect.Left;
        int y = primary.Y - hostRect.Top;
        int width = Math.Min(primary.Width, source.Width - x);
        int height = Math.Min(primary.Height, source.Height - y);

        if (x < 0 || y < 0 || width <= 0 || height <= 0)
        {
            return source;
        }

        if (x == 0 && y == 0 && width == source.Width && height == source.Height)
        {
            return source;
        }

        int rowBytes = width * 4;
        var cropped = new byte[rowBytes * height];
        int firstSourceRow = source.Height - y - height;

        for (int row = 0; row < height; row++)
        {
            int sourceOffset = ((firstSourceRow + row) * source.Stride) + (x * 4);
            Array.Copy(source.Bgra, sourceOffset, cropped, row * rowBytes, rowBytes);
        }

        return new PreviewImage(width, height, rowBytes, cropped);
    }

    /// <summary>
    /// Writes the image to <paramref name="path"/> as a PNG.
    /// </summary>
    /// <remarks>
    /// A convenience for bug reports and for the "save this preview" affordance.
    /// The encoder is hand-rolled because Core references neither WPF nor
    /// System.Drawing; it emits 8-bit truecolour, which is lossless for a screen
    /// capture and drops the alpha channel. That is deliberate: GDI leaves the
    /// alpha byte of a 32-bit <c>BI_RGB</c> capture at zero, so a PNG carrying
    /// it would open fully transparent in every viewer.
    /// </remarks>
    /// <returns><see langword="false"/> when the file could not be written.</returns>
    public bool SavePng(PreviewImage image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // The record does not enforce its own invariants, and reading past a
        // short buffer here would throw an index error at the caller.
        if (image.Width <= 0 ||
            image.Height <= 0 ||
            image.Stride < image.Width * 4 ||
            image.Bgra.Length < image.ByteCount)
        {
            return false;
        }

        try
        {
            using FileStream file = File.Create(path);
            WritePng(file, image);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the top-level window that hosts the desktop icons.
    /// </summary>
    /// <remarks>
    /// The retry budget is deliberately much smaller than the reader's: the
    /// desktop is captured immediately before a swap, so a shell that is
    /// mid-restart gets a fraction of a second and is then skipped rather than
    /// stalling the operation the preview exists to inform.
    /// </remarks>
    private static nint FindHostWindow()
    {
        nint view = DesktopGridView.FindView(retries: 3, delay: TimeSpan.FromMilliseconds(100));
        if (view == nint.Zero)
        {
            return nint.Zero;
        }

        // The listview is a grandchild of Progman or WorkerW; those are the
        // windows that own the wallpaper as well as the icons.
        nint current = view;
        for (int depth = 0; depth < MaxAncestorDepth; depth++)
        {
            nint parent = NativeMethods.GetParent(current);
            if (parent == nint.Zero)
            {
                break;
            }

            current = parent;
        }

        return current;
    }

    private static PreviewImage? CaptureWindow(nint window, int width, int height)
    {
        nint screenDc = Gdi32Methods.CreateDC(Gdi32Methods.DisplayDevice, null, null, nint.Zero);
        if (screenDc == nint.Zero)
        {
            return null;
        }

        nint memoryDc = nint.Zero;
        nint bitmap = nint.Zero;

        try
        {
            memoryDc = Gdi32Methods.CreateCompatibleDC(screenDc);
            if (memoryDc == nint.Zero)
            {
                return null;
            }

            bitmap = Gdi32Methods.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == nint.Zero)
            {
                return null;
            }

            nint displaced = Gdi32Methods.SelectObject(memoryDc, bitmap);
            if (displaced == nint.Zero)
            {
                return null;
            }

            bool printed;
            try
            {
                printed = NativeMethods.PrintWindow(window, memoryDc, NativeMethods.PW_RENDERFULLCONTENT);
            }
            finally
            {
                // GetDIBits refuses to read a bitmap that is still selected
                // into a DC, so this restore is a precondition of the read
                // below, not just tidy-up.
                Gdi32Methods.SelectObject(memoryDc, displaced);
            }

            if (!printed)
            {
                return null;
            }

            return ReadPixels(memoryDc, bitmap, width, height);
        }
        finally
        {
            if (bitmap != nint.Zero)
            {
                Gdi32Methods.DeleteObject(bitmap);
            }

            if (memoryDc != nint.Zero)
            {
                Gdi32Methods.DeleteDC(memoryDc);
            }

            Gdi32Methods.DeleteDC(screenDc);
        }
    }

    private static PreviewImage? ReadPixels(nint memoryDc, nint bitmap, int width, int height)
    {
        var info = new Gdi32Methods.BITMAPINFO
        {
            Header = new Gdi32Methods.BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<Gdi32Methods.BITMAPINFOHEADER>(),
                Width = width,
                // Positive height keeps GDI's native bottom-up row order, which
                // is the layout PreviewImage documents. A negative height would
                // be top-down and every consumer would render it flipped.
                Height = height,
                Planes = 1,
                BitCount = BitsPerPixel,
                Compression = Gdi32Methods.BI_RGB,
            },
        };

        // Requesting 32bpp reduces the alignment rule to width * 4, but the
        // stride is still derived from the rule rather than hardcoded, because
        // the value is part of the contract handed to the UI layer.
        int stride = (((width * BitsPerPixel) + 31) / 32) * 4;
        var pixels = new byte[stride * height];

        int lines = Gdi32Methods.GetDIBits(
            memoryDc,
            bitmap,
            0,
            (uint)height,
            pixels,
            ref info,
            Gdi32Methods.DIB_RGB_COLORS);

        return lines == height ? new PreviewImage(width, height, stride, pixels) : null;
    }

    private static void WritePng(Stream output, PreviewImage image)
    {
        output.Write(PngSignature);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)image.Height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: truecolour RGB
        header[10] = 0; // compression: deflate
        header[11] = 0; // filter method: adaptive
        header[12] = 0; // not interlaced

        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, Compress(image));
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Deflates the image into a single zlib stream of filter-0 scanlines.
    /// </summary>
    /// <remarks>
    /// One IDAT chunk rather than several. PNG allows any number, and streaming
    /// the chunk would avoid holding the compressed copy in memory, but the
    /// writer stays small this way and a desktop capture compresses to a few
    /// megabytes at most.
    /// </remarks>
    private static byte[] Compress(PreviewImage image)
    {
        using var compressed = new MemoryStream();

        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + (image.Width * 3)];

            for (int y = 0; y < image.Height; y++)
            {
                // PNG scanlines run top-down and the capture is bottom-up.
                int source = (image.Height - 1 - y) * image.Stride;
                row[0] = 0; // filter type: none

                for (int x = 0; x < image.Width; x++)
                {
                    int pixel = source + (x * 4);
                    int target = 1 + (x * 3);

                    row[target] = image.Bgra[pixel + 2];
                    row[target + 1] = image.Bgra[pixel + 1];
                    row[target + 2] = image.Bgra[pixel];
                }

                deflate.Write(row, 0, row.Length);
            }
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        // The checksum covers the type and the data. The length field is
        // excluded, which is the one detail every hand-rolled writer gets
        // wrong the first time.
        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        crc ^= 0xFFFFFFFFu;

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            uint value = index;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
