using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeetingDesktop.App.Services;
using MeetingDesktop.Core.Models;
using Xunit.Abstractions;

namespace MeetingDesktop.App.Tests;

public sealed class DesktopBitmapCaptureTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(13, 7, 8)]
    [InlineData(3840, 2160, 0)]
    [InlineData(720, 1280, 4)]
    public void PreviewConversionPreservesPixelsAndBoundsManagedAllocation(int width, int height, int padding)
    {
        int stride = width * 4 + padding;
        var data = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                data[i] = (byte)(x * 7 + y * 3);
                data[i + 1] = (byte)(x / 7 + y * 5);
                data[i + 2] = (byte)(x * 3 + y / 9);
                data[i + 3] = (byte)(x + y);
            }
        var preview = new PreviewImage(width, height, stride, data);
        var monitor = new MonitorInfo("Synthetic", -width, 0, width, height, true);
        var reference = LiquidGlassCoordinator.PrepareFrame(Reference(preview), monitor);
        _ = DesktopBitmap.FromPreviewForGlass(new PreviewImage(1, 1, 4, new byte[4]));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var bitmap = DesktopBitmap.FromPreviewForGlass(preview);
        var actual = LiquidGlassCoordinator.PrepareFrame(bitmap, monitor);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(reference.Image.PixelWidth, actual.Image.PixelWidth);
        Assert.Equal(reference.Image.PixelHeight, actual.Image.PixelHeight);
        Assert.Equal(reference.Pixels, actual.Pixels);
        Assert.True(bitmap.IsFrozen);
        Assert.True(allocated < (long)actual.Pixels.Length + 1_000_000,
            $"Allocated {allocated:N0} bytes in {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void FourKConversionComparisonReportsManagedAllocationAndTime()
    {
        var preview = new PreviewImage(3840, 2160, 3840 * 4, new byte[3840 * 2160 * 4]);
        var monitor = new MonitorInfo("Synthetic", 0, 0, 3840, 2160, true);
        void Original() => LiquidGlassCoordinator.PrepareFrame(Reference(preview), monitor);
        void Optimized() => LiquidGlassCoordinator.PrepareFrame(DesktopBitmap.FromPreviewForGlass(preview), monitor);
        Original(); Optimized();
        (long bytes, double ms) Measure(Action action)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) action();
            watch.Stop();
            return ((GC.GetAllocatedBytesForCurrentThread() - start) / 5, watch.Elapsed.TotalMilliseconds / 5);
        }
        var oldResult = Measure(Original);
        var newResult = Measure(Optimized);
        output.WriteLine($"4K synthetic conversion + frame preparation, 5 iterations, bytes/frame and ms/frame: original {oldResult.bytes:N0}, {oldResult.ms:F2}; optimized {newResult.bytes:N0}, {newResult.ms:F2}");
        Assert.True(newResult.bytes < oldResult.bytes - 30_000_000);
    }

    [Fact]
    public void GlassPreviewRejectsInvalidBuffers()
    {
        Assert.Throws<ArgumentException>(() => DesktopBitmap.FromPreviewForGlass(new PreviewImage(2, 2, 8, new byte[15])));
        Assert.Throws<ArgumentException>(() => DesktopBitmap.FromPreviewForGlass(new PreviewImage(2, 2, 7, new byte[16])));
    }
    [Fact]
    public async Task BackgroundConversionIsFrozenOrientedOpaqueAndReflectsEachNewFrame()
    {
        byte[] first = [10, 20, 30, 0, 40, 50, 60, 7]; // bottom row, then top row
        var monitor = new MonitorInfo("Synthetic", 0, 0, 1, 2, true);
        var frame = await Task.Run(() => LiquidGlassCoordinator.PrepareFrame(
            DesktopBitmap.FromPreviewForGlass(new PreviewImage(1, 2, 4, first)), monitor));
        Assert.True(frame.Image.IsFrozen);
        Assert.Equal(new byte[] { 40, 50, 60, 255, 10, 20, 30, 255 }, frame.Pixels);
        Assert.Equal(new byte[] { 10, 20, 30, 0, 40, 50, 60, 7 }, first);
        first[4] = 99;
        var next = await Task.Run(() => LiquidGlassCoordinator.PrepareFrame(
            DesktopBitmap.FromPreviewForGlass(new PreviewImage(1, 2, 4, first)), monitor));
        Assert.Equal(99, next.Pixels[0]);
        Assert.Equal(40, frame.Pixels[0]);
    }
    private static BitmapSource Reference(PreviewImage preview)
    {
        int stride = preview.Width * 4;
        byte[] pixels = new byte[stride * preview.Height];
        for (int row = 0; row < preview.Height; row++)
        {
            Buffer.BlockCopy(preview.Bgra, (preview.Height - row - 1) * preview.Stride, pixels, row * stride, stride);
            for (int col = 0; col < preview.Width; col++) pixels[row * stride + col * 4 + 3] = 255;
        }
        var result = BitmapSource.Create(preview.Width, preview.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}




