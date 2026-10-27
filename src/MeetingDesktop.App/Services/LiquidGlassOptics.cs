using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeetingDesktop.App.Services;

internal sealed record GlassOpticalMaps(BitmapSource Displacement, BitmapSource Rim);

/// <summary>Geometry-only lookup tables shared by every live frame of a surface.</summary>
internal static class LiquidGlassOptics
{
    public static GlassOpticalMaps Create(double width, double height, double radius)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(radius) || width <= 0 || height <= 0)
            throw new ArgumentException("Invalid glass geometry.");
        double scale = Math.Min(1, Math.Min(768 / width, 512 / height));
        int columns = Math.Max(1, (int)Math.Ceiling(width * scale));
        int rows = Math.Max(1, (int)Math.Ceiling(height * scale));
        byte[] lens = new byte[columns * rows * 4], rim = new byte[lens.Length];
        double corner = Math.Clamp(radius, 1, Math.Min(width, height) / 2);
        double bezel = Math.Clamp(Math.Min(width, height) / 7, 5, 26);
        double thickness = Math.Min(22, bezel * 1.2);
        var offsets = new double[256];
        for (int i = 0; i < offsets.Length; i++)
        {
            double a = 1 - (i + .5) / offsets.Length;
            double lensHeight = Math.Sqrt(Math.Max(.0001, 1 - Math.Pow(a, 4)));
            double incident = Math.Atan(2 * a * a * a / lensHeight);
            offsets[i] = Math.Tan(incident - Math.Asin(Math.Sin(incident) / 1.5)) * lensHeight * thickness;
        }
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            double px = (x + .5) * width / columns - width / 2;
            double py = (y + .5) * height / rows - height / 2;
            double qx = Math.Abs(px) - (width / 2 - corner), qy = Math.Abs(py) - (height / 2 - corner);
            double ex = Math.Max(qx, 0), ey = Math.Max(qy, 0);
            double length = Math.Sqrt(ex * ex + ey * ey);
            double distance = length + Math.Min(Math.Max(qx, qy), 0) - corner;
            double inside = Math.Max(0, -distance);
            double nx = length > .0001 ? Math.Sign(px) * ex / length : qx > qy ? Math.Sign(px) : 0;
            double ny = length > .0001 ? Math.Sign(py) * ey / length : qx > qy ? 0 : Math.Sign(py);
            double offset = inside < bezel ? offsets[Math.Clamp((int)(inside / bezel * 255), 0, 255)] : 0;
            int index = (y * columns + x) * 4;
            lens[index + 2] = (byte)Math.Clamp(Math.Round(128 + nx * offset * 255 / 64), 0, 255);
            lens[index + 1] = (byte)Math.Clamp(Math.Round(128 + ny * offset * 255 / 64), 0, 255);
            lens[index] = (byte)(Math.Clamp(inside / bezel, 0, 1) * 255);
            lens[index + 3] = 255;
            rim[index] = (byte)((.32 + .68 * Math.Clamp(inside / Math.Min(bezel, 10), 0, 1)) * 255);
            double light = Math.Pow(Math.Abs(nx * -.707 + ny * -.707), 1.6);
            rim[index + 2] = (byte)(Math.Exp(-inside / 1.3) * (.32 + .68 * light) * 115);
            rim[index + 1] = (byte)(Math.Clamp(.5 - distance * scale, 0, 1) * 255);
            rim[index + 3] = 255;
        }
        return new(Bitmap(lens), Bitmap(rim));

        BitmapSource Bitmap(byte[] pixels)
        {
            var bitmap = BitmapSource.Create(columns, rows, 96, 96, PixelFormats.Bgra32, null, pixels, columns * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
