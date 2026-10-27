namespace MeetingDesktop.App.Services;

internal readonly record struct GlassSample(int Width, int Height, byte[] Bgra);

/// <summary>A cached backdrop material. Text and controls are composited above this image.</summary>
internal static class LiquidGlassRasterizer
{
    public static GlassSample Render(byte[] source, int sourceWidth, int sourceHeight,
        double left, double top, double width, double height, double radius, bool dark, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (sourceWidth <= 0 || sourceHeight <= 0 || source.Length < (long)sourceWidth * sourceHeight * 4 ||
            !double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height) ||
            width <= 0 || height <= 0 || !double.IsFinite(radius)) throw new ArgumentException("Invalid desktop material bounds.");

        double scale = Math.Min(1, Math.Min(768 / width, 512 / height));
        int columns = Math.Max(1, (int)Math.Ceiling(width * scale));
        int rows = Math.Max(1, (int)Math.Ceiling(height * scale));
        var output = new byte[columns * rows * 4];
        double corner = Math.Clamp(radius, 1, Math.Min(width, height) / 2);
        double bezel = Math.Clamp(Math.Min(width, height) / 7, 5, 26);
        double thickness = Math.Min(22, bezel * 1.2);
        // Quartic lens profile and Snell refraction, adapted from the MIT liquid-glass-react skill.
        var offsets = new double[256];
        for (int i = 0; i < offsets.Length; i++)
        {
            double t = (i + 0.5) / offsets.Length;
            double a = 1 - t;
            double lensHeight = Math.Sqrt(Math.Max(0.0001, 1 - Math.Pow(a, 4)));
            double slope = 2 * a * a * a / lensHeight;
            double incident = Math.Atan(slope);
            double transmitted = Math.Asin(Math.Sin(incident) / 1.5);
            offsets[i] = Math.Tan(incident - transmitted) * lensHeight * thickness;
        }

        for (int y = 0; y < rows; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < columns; x++)
            {
                double localX = (x + 0.5) * width / columns;
                double localY = (y + 0.5) * height / rows;
                double px = localX - width / 2, py = localY - height / 2;
                double qx = Math.Abs(px) - (width / 2 - corner);
                double qy = Math.Abs(py) - (height / 2 - corner);
                double edgeX = Math.Max(qx, 0), edgeY = Math.Max(qy, 0);
                double edgeLength = Math.Sqrt(edgeX * edgeX + edgeY * edgeY);
                double distance = edgeLength + Math.Min(Math.Max(qx, qy), 0) - corner;
                if (distance > 0) continue;
                double inside = -distance;
                double nx, ny;
                if (edgeLength > 0.0001) { nx = Math.Sign(px) * edgeX / edgeLength; ny = Math.Sign(py) * edgeY / edgeLength; }
                else if (qx > qy) { nx = Math.Sign(px); ny = 0; }
                else { nx = 0; ny = Math.Sign(py); }
                double offset = inside < bezel ? offsets[Math.Clamp((int)(inside / bezel * 255), 0, 255)] : 0;
                double light = Math.Pow(Math.Abs(nx * -0.707 + ny * -0.707), 1.6);
                double specular = Math.Exp(-inside / 1.3) * (0.32 + 0.68 * light) * 115;
                // Keep the refractive rim clear, with a frosted core under text.
                // Content begins beyond the rim and must work over any wallpaper.
                double tint = (dark ? 0.87 : 0.97) * (0.32 + 0.68 * Math.Clamp(inside / Math.Min(bezel, 10), 0, 1));
                int destination = (y * columns + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    double dispersion = channel == 0 ? 0.94 : channel == 2 ? 1.06 : 1;
                    double sampleX = left + localX - nx * offset * dispersion;
                    double sampleY = top + localY - ny * offset * dispersion;
                    double frost = Math.Clamp(inside / bezel, 0, 1) * 2;
                    double value = Read(sampleX, sampleY, channel) * 0.6 +
                        (Read(sampleX - frost, sampleY, channel) + Read(sampleX + frost, sampleY, channel) +
                         Read(sampleX, sampleY - frost, channel) + Read(sampleX, sampleY + frost, channel)) * 0.1;
                    double tintColor = dark ? (channel == 0 ? 48 : channel == 1 ? 30 : 18) : 244;
                    output[destination + channel] = (byte)Math.Clamp(value * (1 - tint) + tintColor * tint + specular, 0, 255);
                }
                output[destination + 3] = 255;
            }
        }
        return new(columns, rows, output);

        byte Read(double x, double y, int channel)
        {
            int sx = (int)Math.Clamp(Math.Round(x), 0, sourceWidth - 1);
            int sy = (int)Math.Clamp(Math.Round(y), 0, sourceHeight - 1);
            return source[(sy * sourceWidth + sx) * 4 + channel];
        }
    }
}
