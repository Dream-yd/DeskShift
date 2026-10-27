using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Services;

internal static class DesktopBitmap
{
    public static BitmapSource FromPreview(PreviewImage preview)
    {
        if (preview.Width <= 0 || preview.Height <= 0 || preview.Stride < preview.Width * 4 ||
            preview.Bgra.Length < (long)preview.Stride * preview.Height) throw new ArgumentException("Invalid desktop image.");
        int stride = checked(preview.Width * 4);
        byte[] pixels = new byte[checked(stride * preview.Height)];
        for (int row = 0; row < preview.Height; row++)
        {
            Buffer.BlockCopy(preview.Bgra, (preview.Height - row - 1) * preview.Stride, pixels, row * stride, stride);
            for (int column = 0; column < preview.Width; column++) pixels[row * stride + column * 4 + 3] = 255;
        }
        var bitmap = BitmapSource.Create(preview.Width, preview.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource FromPreviewForGlass(PreviewImage preview)
    {
        if (preview.Width <= 0 || preview.Height <= 0 || preview.Stride < (long)preview.Width * 4 ||
            preview.Bgra.Length < (long)preview.Stride * preview.Height) throw new ArgumentException("Invalid desktop image.");
        // Bgr32 ignores GDI's undefined alpha byte. Copy bottom-up rows into
        // WPF's top-down backing store without another full-size managed array.
        var bitmap = new WriteableBitmap(preview.Width, preview.Height, 96, 96, PixelFormats.Bgr32, null);
        int rowBytes = checked(preview.Width * 4);
        bitmap.Lock();
        try
        {
            for (int row = 0; row < preview.Height; row++)
                Marshal.Copy(preview.Bgra, checked((preview.Height - row - 1) * preview.Stride),
                    IntPtr.Add(bitmap.BackBuffer, checked(row * bitmap.BackBufferStride)), rowBytes);
            bitmap.AddDirtyRect(new Int32Rect(0, 0, preview.Width, preview.Height));
        }
        finally { bitmap.Unlock(); }
        bitmap.Freeze();
        return bitmap;
    }
}
