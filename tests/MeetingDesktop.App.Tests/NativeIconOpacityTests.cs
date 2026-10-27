using System.Runtime.InteropServices;
using MeetingDesktop.App.Services;

namespace MeetingDesktop.App.Tests;

public sealed class NativeIconOpacityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenTransitionRepairsOpacityResetDuringFolderRebind(bool removeLayeredStyle) => StaTest.Run(() =>
    {
        using var window = CreateWindow();
        int originalStyle = GetWindowLong(window.Handle, -20);
        using var state = new IconTransitionState(() => NativeIconOpacity.TryAcquire(window.Handle));
        Assert.True(state.Begin("return", 0));
        Assert.True(state.Hide("return"));
        if (removeLayeredStyle)
            SetWindowLong(window.Handle, -20, GetWindowLong(window.Handle, -20) & ~0x80000);
        else
            Assert.True(SetLayeredWindowAttributes(window.Handle, 0, 255, 2));

        state.Tick(16);
        AssertAttributes(window.Handle, 0, 0, 2);
        Assert.True(state.Active);
        state.End("return", 20);
        state.Tick(100);
        Assert.True(GetLayeredWindowAttributes(window.Handle, out _, out byte alpha, out _));
        Assert.InRange(alpha, 1, 254);
        state.Tick(180);
        Assert.False(state.Active);
        Assert.Equal(originalStyle, GetWindowLong(window.Handle, -20));
    });

    [Fact]
    public void RestoresOwnedWindowStyleAfterFade() => StaTest.Run(() =>
    {
        using var window = CreateWindow();
        int before = GetWindowLong(window.Handle, -20);
        using (var layer = NativeIconOpacity.TryAcquire(window.Handle))
        {
            Assert.NotNull(layer);
            Assert.True(layer.SetAlpha(0));
            Assert.True(GetLayeredWindowAttributes(window.Handle, out _, out byte alpha, out _));
            Assert.Equal(0, alpha);
            Assert.True(layer.SetAlpha(128));
            Assert.True(GetLayeredWindowAttributes(window.Handle, out _, out alpha, out _));
            Assert.Equal(128, alpha);
        }
        Assert.Equal(before, GetWindowLong(window.Handle, -20));
    });

    [Fact]
    public void PreservesExistingColorKeyOpacityAndLayeredStyle() => StaTest.Run(() =>
    {
        using var window = CreateWindow();
        SetWindowLong(window.Handle, -20, GetWindowLong(window.Handle, -20) | 0x80000);
        SetLayeredWindowAttributes(window.Handle, 0x123456, 99, 3);
        using (var layer = NativeIconOpacity.TryAcquire(window.Handle))
        {
            Assert.NotNull(layer);
            Assert.True(layer.SetAlpha(128));
            Assert.True(GetLayeredWindowAttributes(window.Handle, out uint key, out byte faded, out uint options));
            Assert.Equal(0x123456u, key);
            Assert.Equal(49, faded);
            Assert.Equal(3u, options);
        }
        Assert.True(GetLayeredWindowAttributes(window.Handle, out uint color, out byte alpha, out uint flags));
        Assert.Equal(0x123456u, color);
        Assert.Equal(99, alpha);
        Assert.Equal(3u, flags);
        Assert.NotEqual(0, GetWindowLong(window.Handle, -20) & 0x80000);
    });

    [Fact]
    public void DoesNotTakeOverUnreadableLayeredAttributes() => StaTest.Run(() =>
    {
        using var window = CreateWindow();
        SetWindowLong(window.Handle, -20, GetWindowLong(window.Handle, -20) | 0x80000);
        Assert.False(GetLayeredWindowAttributes(window.Handle, out _, out _, out _));
        Assert.Null(NativeIconOpacity.TryAcquire(window.Handle));
    });

    [Theory]
    [InlineData(99, 1u)]
    [InlineData(99, 2u)]
    [InlineData(99, 3u)]
    [InlineData(0, 2u)]
    [InlineData(255, 2u)]
    public void RestoresAttributeSnapshotForAllSupportedFlags(byte initialAlpha, uint initialFlags) => StaTest.Run(() =>
    {
        using var window = CreateWindow();
        SetWindowLong(window.Handle, -20, GetWindowLong(window.Handle, -20) | 0x80000);
        Assert.True(SetLayeredWindowAttributes(window.Handle, 0x123456, initialAlpha, initialFlags));
        Assert.True(GetLayeredWindowAttributes(window.Handle, out uint color, out byte alpha, out uint flags));
        int style = GetWindowLong(window.Handle, -20);
        using var layer = NativeIconOpacity.TryAcquire(window.Handle);
        Assert.NotNull(layer);
        foreach (byte fraction in new byte[] { 255, 128, 0, 255 })
        {
            Assert.True(layer.SetAlpha(fraction));
            int baseline = (flags & 2) != 0 ? alpha : 255;
            AssertAttributes(window.Handle, color, (byte)(baseline * fraction / 255), flags | 2);
        }
        SetWindowLong(window.Handle, -20, style | 0x20);
        bool restored = layer.Restore();
        GetLayeredWindowAttributes(window.Handle, out uint restoredColor, out byte restoredAlpha, out uint restoredFlags);
        Assert.True(restored, $"Original={color:X}/{alpha}/{flags}; restored={restoredColor:X}/{restoredAlpha}/{restoredFlags}");
        AssertAttributes(window.Handle, color, alpha, flags);
        Assert.Equal(style | 0x20, GetWindowLong(window.Handle, -20));
        Assert.True(layer.Restore());
        Assert.False(layer.SetAlpha(0));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherLeaseCannotSnapshotAnInProgressFade(bool initiallyLayered) => StaTest.Run(() =>
    {
        using var window = CreateWindow();
        if (initiallyLayered)
        {
            SetWindowLong(window.Handle, -20, GetWindowLong(window.Handle, -20) | 0x80000);
            Assert.True(SetLayeredWindowAttributes(window.Handle, 0x123456, 99, 3));
        }
        using var first = NativeIconOpacity.TryAcquire(window.Handle);
        Assert.NotNull(first);
        Assert.True(first.SetAlpha(0));
        using var second = NativeIconOpacity.TryAcquire(window.Handle);
        Assert.Null(second);
        Assert.True(first.Restore());
        using var next = NativeIconOpacity.TryAcquire(window.Handle);
        Assert.NotNull(next);
        Assert.True(next.Restore());
        if (initiallyLayered) AssertAttributes(window.Handle, 0x123456, 99, 3);
        else Assert.Equal(0, GetWindowLong(window.Handle, -20) & 0x80000);
    });

    [Fact]
    public void DisposalPreservesUnrelatedStylesAndIsIdempotent() => StaTest.Run(() =>
    {
        using var window = CreateWindow();
        var layer = NativeIconOpacity.TryAcquire(window.Handle);
        Assert.NotNull(layer);
        SetWindowLong(window.Handle, -20, GetWindowLong(window.Handle, -20) | 0x20);
        layer.Dispose();
        layer.Dispose();
        Assert.Equal(0x20, GetWindowLong(window.Handle, -20) & (0x80000 | 0x20));
        Assert.False(layer.SetAlpha(0));
    });

    [Fact]
    public void DestroyedWindowDoesNotReceiveCleanup() => StaTest.Run(() =>
    {
        using var window = CreateWindow();
        using var layer = NativeIconOpacity.TryAcquire(window.Handle);
        Assert.NotNull(layer);
        window.Dispose();
        Assert.False(layer.SetAlpha(0));
        layer.Dispose();
    });

    private static OwnedWindow CreateWindow() => new();

    private static void AssertAttributes(nint window, uint expectedColor, byte expectedAlpha, uint expectedFlags)
    {
        Assert.True(GetLayeredWindowAttributes(window, out uint color, out byte alpha, out uint flags));
        Assert.Equal(expectedColor, color);
        // With COLORKEY alone Windows ignores bAlpha on write and leaves the
        // previous byte on read. Only enabled channels describe visible state.
        if ((expectedFlags & 2) != 0) Assert.Equal(expectedAlpha, alpha);
        Assert.Equal(expectedFlags, flags);
    }

    private sealed class OwnedWindow : IDisposable
    {
        internal nint Handle { get; private set; } = CreateWindowEx(0, "STATIC", "Owned fade test",
            unchecked((int)0x80000000), 0, 0, 10, 10, 0, 0, 0, 0);
        public void Dispose() { if (Handle != 0) DestroyWindow(Handle); Handle = 0; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(int extended, string className, string title, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint color, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
}
