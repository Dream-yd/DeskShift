namespace MeetingDesktop.Core.Models;

/// <summary>
/// A raw bitmap of the desktop, ready for a UI toolkit to display.
/// </summary>
/// <remarks>
/// Pixels rather than a framework bitmap, deliberately. Core does not
/// reference WPF, so it cannot return a <c>BitmapSource</c>; and referencing
/// <c>System.Drawing</c> would drag a Windows-only imaging dependency into a
/// library that otherwise only needs P/Invoke. The app converts this to
/// whatever its UI wants in a few lines.
///
/// The buffer is BGRA, bottom-up, with a stride that may exceed
/// <c>Width * 4</c> - a device context's rows are aligned, so callers must
/// honour <see cref="Stride"/> rather than assuming it.
/// </remarks>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Stride">Bytes per row. May be larger than <c>Width * 4</c>.</param>
/// <param name="Bgra">The pixel data.</param>
public sealed record PreviewImage(int Width, int Height, int Stride, byte[] Bgra)
{
    /// <summary>Size in bytes this image occupies when laid out with <see cref="Stride"/>.</summary>
    public int ByteCount => Stride * Height;
}
