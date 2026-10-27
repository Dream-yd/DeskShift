using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeetingDesktop.App.Services;

namespace MeetingDesktop.App.Tests;

public sealed class LiquidGlassLiveRenderingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ActualShaderKeepsTextReadableAcrossTheEntireShell(bool dark, bool white) => StaTest.Run(() =>
    {
        var material = new LiquidGlassBrush();
        material.Update(Frame(white), new Rect(0, 0, 120, 80), new Size(120, 80), 12, dark, global: true);
        byte[] pixel = Center(material.Brush);
        double text = dark ? Luminance(220, 230, 243) : Luminance(63, 76, 95);
        double background = Luminance(pixel[2], pixel[1], pixel[0]);
        Assert.True((Math.Max(background, text) + .05) / (Math.Min(background, text) + .05) >= 4.5,
            $"Rendered background BGR={pixel[0]},{pixel[1]},{pixel[2]}");
    });

    [Fact]
    public void LiveFramesUpdateTheSameBrushAndActuallyChangeRenderedPixels() => StaTest.Run(() =>
    {
        var material = new LiquidGlassBrush();
        Assert.True(material.UsesRefraction);
        var brush = material.Brush;
        material.Update(Frame(false), new Rect(0, 0, 120, 80), new Size(120, 80), 12, true);
        byte[] before = Center(brush);
        material.Update(Frame(true), new Rect(0, 0, 120, 80), new Size(120, 80), 12, true);
        byte[] after = Center(brush);
        Assert.Same(brush, material.Brush);
        Assert.InRange(after[0] - before[0], 25, 45);
        Assert.Equal(255, after[3]);
    });

    [Fact]
    public void OpticalMapsAreBoundedFrozenAndDoNotPremultiplyDisplacementChannels()
    {
        GlassOpticalMaps maps = LiquidGlassOptics.Create(3840, 2160, 24);
        Assert.InRange(maps.Displacement.PixelWidth, 1, 768);
        Assert.InRange(maps.Displacement.PixelHeight, 1, 512);
        Assert.True(maps.Displacement.IsFrozen);
        Assert.True(maps.Rim.IsFrozen);
        var pixels = new byte[maps.Displacement.PixelWidth * maps.Displacement.PixelHeight * 4];
        maps.Displacement.CopyPixels(pixels, maps.Displacement.PixelWidth * 4, 0);
        Assert.All(pixels.Where((_, index) => index % 4 == 3), value => Assert.Equal(255, value));
        Assert.Throws<ArgumentException>(() => LiquidGlassOptics.Create(double.NaN, 100, 10));
    }

    private static DesktopGlassFrame Frame(bool white)
    {
        byte[] pixels = new byte[120 * 80 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = pixels[index + 1] = pixels[index + 2] = white ? (byte)255 : (byte)0;
            pixels[index + 3] = 255;
        }
        var bitmap = BitmapSource.Create(120, 80, 96, 96, PixelFormats.Bgra32, null, pixels, 120 * 4);
        bitmap.Freeze();
        return new(bitmap, pixels, new("Synthetic", 0, 0, 120, 80, true));
    }

    private static byte[] Center(Brush brush)
    {
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen()) context.DrawRectangle(brush, null, new Rect(0, 0, 120, 80));
        var bitmap = new RenderTargetBitmap(120, 80, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(60, 40, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static double Luminance(byte r, byte g, byte b)
    {
        static double Linear(byte channel) { double v = channel / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Linear(r) + .7152 * Linear(g) + .0722 * Linear(b);
    }
}
