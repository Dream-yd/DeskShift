using MeetingDesktop.App.Services;

namespace MeetingDesktop.App.Tests;

public sealed class LiquidGlassRasterizerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ContentRemainsReadableOverBlackOrWhiteDesktopImages(bool dark, bool white)
    {
        byte[] source = Enumerable.Repeat(white ? (byte)255 : (byte)0, 80 * 80 * 4).ToArray();
        var image = LiquidGlassRasterizer.Render(source, 80, 80, 0, 0, 80, 80, 16, dark);
        int center = (40 * image.Width + 40) * 4;
        double background = Luminance(image.Bgra[center + 2], image.Bgra[center + 1], image.Bgra[center]);
        double text = dark ? Luminance(171, 187, 208) : Luminance(92, 108, 128);
        Assert.True((Math.Max(background, text) + .05) / (Math.Min(background, text) + .05) >= 4.5);

        static double Luminance(byte r, byte g, byte b)
        {
            static double Linear(byte channel) { double v = channel / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Linear(r) + .7152 * Linear(g) + .0722 * Linear(b);
        }
    }
    [Fact]
    public void RefractsTheRimWithoutMutatingTheDesktopSource()
    {
        byte[] source = Stripes(160, 120);
        byte[] original = source.ToArray();
        var material = LiquidGlassRasterizer.Render(source, 160, 120, 20, 20, 100, 70, 14, false);
        Assert.Equal(original, source);
        Assert.Equal(100, material.Width);
        Assert.Equal(70, material.Height);
        Assert.Equal(0, material.Bgra[3]);
        int center = (35 * material.Width + 50) * 4;
        Assert.Equal(255, material.Bgra[center + 3]);
        Assert.True(material.Bgra.Where((_, index) => index % 4 != 3).Distinct().Count() > 12);
        Assert.NotEqual(source.Skip((55 * 160 + 20) * 4).Take(4), material.Bgra.Skip(35 * 100 * 4).Take(4));
    }

    [Fact]
    public void BoundsTheMaterialAndHandlesNegativeDesktopCoordinates()
    {
        var material = LiquidGlassRasterizer.Render(Stripes(80, 60), 80, 60, -400, -200, 2000, 1200, 18, true);
        Assert.InRange(material.Width, 1, 768);
        Assert.InRange(material.Height, 1, 512);
        Assert.Equal(material.Width * material.Height * 4, material.Bgra.Length);
    }

    [Fact]
    public void ACancelledOrInvalidRenderCannotPublishAPartialImage()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => LiquidGlassRasterizer.Render(Stripes(80, 60), 80, 60, 0, 0, 50, 40, 12, true, cancelled.Token));
        Assert.Throws<ArgumentException>(() => LiquidGlassRasterizer.Render([], 80, 60, 0, 0, 50, 40, 12, true));
    }

    private static byte[] Stripes(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int index = (y * width + x) * 4;
            pixels[index] = (byte)((x * 9) % 256);
            pixels[index + 1] = (byte)((y * 7) % 256);
            pixels[index + 2] = (byte)((x / 4 % 2) * 255);
            pixels[index + 3] = 255;
        }
        return pixels;
    }
}
