using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeetingDesktop.App.Services;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Tests;

public sealed class LiquidGlassLifecycleTests
{
    [Fact]
    public void UnavailableDesktopDoesNotSpinAtTheLiveFrameRate() => StaTest.Run(() =>
    {
        using var fixture = new Fixture(() => false);
        fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
        var deadline = DateTime.UtcNow.AddMilliseconds(550);
        Assert.True(PumpUntil(() => DateTime.UtcNow >= deadline));
        Assert.InRange(fixture.Captures, 1, 4);
    });

    [Fact]
    public void VisibleGlassContinuouslyPublishesFramesWithoutQueueingCaptures() => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
        Assert.True(PumpUntil(() => fixture.Coordinator.Frame is not null));
        int initial = fixture.Captures;
        var deadline = DateTime.UtcNow.AddMilliseconds(450);
        Assert.True(PumpUntil(() => fixture.Captures >= initial + 6 || DateTime.UtcNow >= deadline));
        Assert.True(fixture.Captures >= initial + 6, "The visible skin should update continuously, not once every four seconds.");
        Assert.Equal(1, fixture.MaximumConcurrentCaptures);
    });

    [Fact]
    public void ShellPanelsDoNotConcealTheGlobalGlassBackground() => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
        foreach (string role in new[] { "Panel", "Card", "Sunken" })
        {
            var brush = Assert.IsType<SolidColorBrush>(fixture.Window.Resources["Brush." + role]);
            Assert.InRange(brush.Color.A, (byte)0, (byte)100);
        }
        fixture.Coordinator.Apply(SkinPreference.Classic);
        Assert.False(fixture.Window.Resources.Contains("Brush.Panel"));
    });

    [Fact]
    public void AnUnreadableWallpaperFormatDoesNotFaultTheCosmeticRefresh() => StaTest.Run(() =>
    {
        using var fixture = new Fixture(() => throw new System.IO.FileFormatException("Invalid image"));
        fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
        Complete(fixture.Coordinator.RefreshAsync());
        Assert.Null(fixture.Coordinator.Frame);
    });
    [Fact]
    public void FadeKeepsTheTargetOpacityAsItsBaseAfterTheClockStops() => StaTest.Run(() =>
    {
        using var fixture = new Fixture { Visible = false, FadeDuration = TimeSpan.FromMilliseconds(80) };
        fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
        Assert.Equal(1d, fixture.Scene.GetAnimationBaseValue(UIElement.OpacityProperty));
        fixture.Scene.BeginAnimation(UIElement.OpacityProperty, null);
        Assert.Equal(1d, fixture.Scene.Opacity);
        fixture.Coordinator.Apply(SkinPreference.Classic);
        Assert.Equal(0d, fixture.Scene.GetAnimationBaseValue(UIElement.OpacityProperty));
    });
    [Fact]
    public void DisablingMaterialDetachesItsCoordinatorSubscription() => StaTest.Run(() =>
    {
        var window = new Window();
        using var coordinator = new LiquidGlassCoordinator(window, new Border());
        var border = new Border();
        LiquidGlassMaterial.SetCoordinator(border, coordinator);
        try
        {
            LiquidGlassMaterial.SetEnabled(border, true);
            border.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(1, Subscribers(coordinator));
            LiquidGlassMaterial.SetEnabled(border, false);
            Assert.Equal(0, Subscribers(coordinator));
        }
        finally
        {
            border.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            window.Close();
        }
    });

    [Fact]
    public void DisposingCoordinatorReleasesTheLocalBackdrop() => StaTest.Run(() =>
    {
        var window = new Window();
        var coordinator = new LiquidGlassCoordinator(window, new Border());
        try
        {
            window.Resources["Brush.DesktopBackdrop"] = new ImageBrush();
            coordinator.Dispose();
            Assert.False(window.Resources.Contains("Brush.DesktopBackdrop"));
        }
        finally { coordinator.Dispose(); window.Close(); }
    });

    [Fact]
    public void DisablingCoordinatorEventuallyReleasesTheLocalBackdrop() => StaTest.Run(() =>
    {
        var window = new Window();
        using var coordinator = new LiquidGlassCoordinator(window, new Border());
        try
        {
            coordinator.Apply(SkinPreference.LiquidGlass);
            window.Resources["Brush.DesktopBackdrop"] = new ImageBrush();
            coordinator.Apply(SkinPreference.Classic);
            Assert.True(PumpUntil(() => !window.Resources.Contains("Brush.DesktopBackdrop")));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenOrMinimizedWindowSuspendsAndClearsItsFrame(bool minimized) => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
        Assert.True(PumpUntil(() => fixture.Coordinator.Frame is not null));
        fixture.Window.Resources["Brush.DesktopBackdrop"] = new ImageBrush(fixture.Coordinator.Frame!.Image);
        if (minimized) fixture.State = WindowState.Minimized;
        else fixture.Visible = false;
        fixture.Coordinator.UpdateActivity();
        Assert.True(fixture.Coordinator.IsEnabled);
        Assert.False(fixture.Coordinator.IsActive);
        Assert.Null(fixture.Coordinator.Frame);
        Assert.False(fixture.Window.Resources.Contains("Brush.DesktopBackdrop"));
        int captures = fixture.Captures;
        Complete(fixture.Coordinator.RefreshAsync());
        Assert.Equal(captures, fixture.Captures);
    });

    [Theory]
    [InlineData("hide")]
    [InlineData("minimize")]
    [InlineData("disable")]
    [InlineData("dispose")]
    [InlineData("hide-before-event")]
    public void PendingCaptureCannotPublishAfterSuspensionOrDisposal(string change) => StaTest.Run(() =>
    {
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var fixture = new Fixture(() => { started.Set(); return gate.Wait(TimeSpan.FromSeconds(2)); });
        try
        {
            fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
            Assert.True(PumpUntil(() => started.IsSet));
            Task pending = fixture.Coordinator.RefreshAsync();
            switch (change)
            {
                case "hide": fixture.Visible = false; fixture.Coordinator.UpdateActivity(); break;
                case "minimize": fixture.State = WindowState.Minimized; fixture.Coordinator.UpdateActivity(); break;
                case "disable": fixture.Coordinator.Apply(SkinPreference.Classic); break;
                case "dispose": fixture.Coordinator.Dispose(); break;
                default: fixture.Visible = false; break; // Publication must recheck even before the window event arrives.
            }
            gate.Set();
            Complete(pending);
            Assert.Null(fixture.Coordinator.Frame);
            Assert.False(fixture.Coordinator.IsActive);
            Assert.Equal(1, fixture.Captures);
        }
        finally { gate.Set(); }
    });

    [Fact]
    public void ResumingWaitsForTheOldCaptureAndStartsOneFreshCapture() => StaTest.Run(() =>
    {
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        int calls = 0;
        using var fixture = new Fixture(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) { started.Set(); return gate.Wait(TimeSpan.FromSeconds(2)); }
            return true;
        });
        try
        {
            fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
            Assert.True(PumpUntil(() => started.IsSet));
            Task pending = fixture.Coordinator.RefreshAsync();
            fixture.Visible = false;
            fixture.Coordinator.UpdateActivity();
            fixture.Visible = true;
            fixture.Coordinator.UpdateActivity();
            gate.Set();
            Complete(pending);
            Assert.True(PumpUntil(() => fixture.Coordinator.Frame is not null));
            Assert.Equal(2, fixture.Captures);
            Assert.Equal(1, fixture.MaximumConcurrentCaptures);
            Assert.NotEqual(fixture.UiThread, fixture.CaptureThread);
            Assert.Equal(fixture.UiThread, fixture.PublishThread);
        }
        finally { gate.Set(); }
    });

    [Fact]
    public void DisableClearsFrameImmediatelyAndReleasesBackdropAfterFade() => StaTest.Run(() =>
    {
        using var fixture = new Fixture { Visible = false, FadeDuration = TimeSpan.FromMilliseconds(80) };
        fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
        fixture.Window.Resources["Brush.DesktopBackdrop"] = new ImageBrush();
        fixture.Coordinator.Apply(SkinPreference.Classic);
        Assert.Null(fixture.Coordinator.Frame);
        Assert.True(fixture.Window.Resources.Contains("Brush.DesktopBackdrop"));
        Assert.True(PumpUntil(() => !fixture.Window.Resources.Contains("Brush.DesktopBackdrop")));
    });

    [Fact]
    public void RapidRetoggleCannotReleaseTheNewBackdrop() => StaTest.Run(() =>
    {
        using var fixture = new Fixture { FadeDuration = TimeSpan.FromMilliseconds(80) };
        fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
        Assert.True(PumpUntil(() => fixture.Coordinator.Frame is not null));
        fixture.Window.Resources["Brush.DesktopBackdrop"] = new ImageBrush();
        fixture.Coordinator.Apply(SkinPreference.Classic);
        fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
        var replacement = new ImageBrush();
        fixture.Window.Resources["Brush.DesktopBackdrop"] = replacement;
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(180);
        Assert.True(PumpUntil(() => DateTime.UtcNow >= deadline));
        Assert.Same(replacement, fixture.Window.Resources["Brush.DesktopBackdrop"]);
        Assert.True(fixture.Coordinator.IsActive);
    });

    [Fact]
    public void RepeatedEnableAndLoadNeverDuplicatesSubscriptions() => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        var border = new Border();
        LiquidGlassMaterial.SetCoordinator(border, fixture.Coordinator);
        try
        {
            for (int i = 0; i < 5; i++)
            {
                LiquidGlassMaterial.SetEnabled(border, true);
                border.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                border.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(2, Subscribers(fixture.Coordinator)); // Fixture observes publication too.
                LiquidGlassMaterial.SetEnabled(border, false);
                Assert.Equal(1, Subscribers(fixture.Coordinator));
            }
        }
        finally { LiquidGlassMaterial.SetEnabled(border, false); }
    });

    [Fact]
    public void ChangingTheInheritedCoordinatorDetachesThePreviousOne() => StaTest.Run(() =>
    {
        using var first = new Fixture();
        using var second = new Fixture();
        var border = new Border();
        LiquidGlassMaterial.SetCoordinator(border, first.Coordinator);
        LiquidGlassMaterial.SetEnabled(border, true);
        try
        {
            border.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            LiquidGlassMaterial.SetCoordinator(border, second.Coordinator);
            Assert.Equal(1, Subscribers(first.Coordinator));
            Assert.Equal(2, Subscribers(second.Coordinator));
            LiquidGlassMaterial.SetEnabled(border, false);
            Assert.Equal(1, Subscribers(second.Coordinator));
        }
        finally { LiquidGlassMaterial.SetEnabled(border, false); }
    });

    [Fact]
    public void DisablingMaterialRestoresItsBackgroundAndCannotRepublish() => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        var border = new Border { CornerRadius = new CornerRadius(12) };
        border.Measure(new Size(100, 70));
        border.Arrange(new Rect(0, 0, 100, 70));
        LiquidGlassMaterial.SetCoordinator(border, fixture.Coordinator);
        LiquidGlassMaterial.SetEnabled(border, true);
        try
        {
            border.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
            Assert.True(PumpUntil(() => border.Background is VisualBrush));
            LiquidGlassMaterial.SetEnabled(border, false);
            Assert.Equal(DependencyProperty.UnsetValue, border.ReadLocalValue(Border.BackgroundProperty));
            Complete(fixture.Coordinator.RefreshAsync());
            Assert.Equal(DependencyProperty.UnsetValue, border.ReadLocalValue(Border.BackgroundProperty));
        }
        finally { LiquidGlassMaterial.SetEnabled(border, false); }
    });

    [Fact]
    public void CleanupPreservesAnOwnersReplacementBackground() => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        var border = new Border();
        border.Measure(new Size(100, 70));
        border.Arrange(new Rect(0, 0, 100, 70));
        LiquidGlassMaterial.SetCoordinator(border, fixture.Coordinator);
        LiquidGlassMaterial.SetEnabled(border, true);
        try
        {
            border.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
            Assert.True(PumpUntil(() => border.Background is VisualBrush));
            border.Background = Brushes.OrangeRed;
            LiquidGlassMaterial.SetEnabled(border, false);
            Assert.Same(Brushes.OrangeRed, border.Background);
        }
        finally { LiquidGlassMaterial.SetEnabled(border, false); }
    });

    [Fact]
    public void DisposedCoordinatorIgnoresQueuedApplyAndCannotRestart() => StaTest.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
        fixture.Coordinator.Dispose();
        fixture.Coordinator.Apply(SkinPreference.LiquidGlass);
        fixture.Coordinator.UpdateActivity();
        Assert.False(fixture.Coordinator.IsEnabled);
        Assert.False(fixture.Coordinator.IsActive);
        Assert.Null(fixture.Coordinator.Frame);
        Assert.Empty(fixture.Window.Resources.MergedDictionaries);
        Assert.Equal(0, Subscribers(fixture.Coordinator));
    });

    private static void Complete(Task task)
    {
        Assert.True(PumpUntil(() => task.IsCompleted));
        task.GetAwaiter().GetResult();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SynchronizationContext? _previousContext = SynchronizationContext.Current;
        private int _captures, _concurrent;
        public Window Window { get; } = new(); // Never shown; injected state avoids any HWND.
        public Border Scene { get; } = new() { Opacity = 0 };
        public LiquidGlassCoordinator Coordinator { get; }
        public bool Visible { get; set; } = true;
        public WindowState State { get; set; }
        public TimeSpan FadeDuration { get; set; }
        public int UiThread { get; } = Environment.CurrentManagedThreadId;
        public int CaptureThread { get; private set; }
        public int PublishThread { get; private set; }
        public int Captures => Volatile.Read(ref _captures);
        public int MaximumConcurrentCaptures { get; private set; }

        public Fixture(Func<bool>? captureGate = null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            BitmapSource image = LiquidGlassHeadlessTests.Image(160, 120);
            var monitor = new MonitorInfo("Synthetic", 0, 0, 160, 120, true);
            Coordinator = new(Window, Scene, new LiquidGlassPlatform(
                () => Visible, () => State, () => false, () => FadeDuration, () => monitor,
                _ =>
                {
                    Interlocked.Increment(ref _captures);
                    int concurrent = Interlocked.Increment(ref _concurrent);
                    MaximumConcurrentCaptures = Math.Max(MaximumConcurrentCaptures, concurrent);
                    CaptureThread = Environment.CurrentManagedThreadId;
                    try { return (captureGate?.Invoke() == false ? null : image, false); }
                    finally { Interlocked.Decrement(ref _concurrent); }
                }, (surface, _) => new Rect(0, 0, surface.ActualWidth, surface.ActualHeight)));
            Coordinator.Changed += (_, _) => { if (Coordinator.Frame is not null) PublishThread = Environment.CurrentManagedThreadId; };
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Window.Close();
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }
    }

    private static int Subscribers(LiquidGlassCoordinator coordinator) =>
        ((Delegate?)typeof(LiquidGlassCoordinator).GetField("Changed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(coordinator))?.GetInvocationList().Length ?? 0;

    private static bool PumpUntil(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        return condition();
    }
}

// These tests use only in-memory bitmaps and CPU rendering. They create no Window or other visual control.
public sealed class LiquidGlassHeadlessTests
{
    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(2160, 3840)]
    [InlineData(4000, 1000)]
    [InlineData(1000, 4000)]
    [InlineData(320, 240)]
    [InlineData(2000, 2000)]
    public async Task FramePreparationBoundsBothDimensionsAndPreservesAspect(int width, int height)
    {
        BitmapSource source = Image(width, height);
        var monitor = new MonitorInfo("Synthetic", -width, 0, width, height, true);
        DesktopGlassFrame frame = await Task.Run(() => LiquidGlassCoordinator.PrepareFrame(source, monitor));
        Assert.InRange(frame.Image.PixelWidth, 1, Math.Min(width, 1920));
        Assert.InRange(frame.Image.PixelHeight, 1, Math.Min(height, 1080));
        Assert.InRange(Math.Abs(frame.Image.PixelWidth - frame.Image.PixelHeight * (double)width / height), 0, 1.01);
        Assert.Equal(frame.Image.PixelWidth * frame.Image.PixelHeight * 4, frame.Pixels.Length);
        Assert.True(frame.Image.IsFrozen);
        Assert.IsNotType<TransformedBitmap>(frame.Image);
        Assert.Equal(PixelFormats.Bgra32, frame.Image.Format);
        Assert.Equal(96d, frame.Image.DpiX);
        Assert.Equal(96d, frame.Image.DpiY);
        Assert.Same(monitor, frame.Monitor);
    }

    [Fact]
    public void FramePreparationConvertsPixelsAndNormalizesDpi()
    {
        byte[] pixels = Enumerable.Repeat((byte)127, 64 * 32 * 3).ToArray();
        var source = BitmapSource.Create(64, 32, 300, 300, PixelFormats.Bgr24, null, pixels, 64 * 3);
        source.Freeze();
        var frame = LiquidGlassCoordinator.PrepareFrame(source, new("Synthetic", 0, 0, 64, 32, true));
        Assert.Equal(new byte[] { 127, 127, 127, 255 }, frame.Pixels[..4]);
        Assert.Equal(96d, frame.Image.DpiX);
        Assert.Equal(96d, frame.Image.DpiY);
        Assert.All(pixels, value => Assert.Equal((byte)127, value));
    }

    [Fact]
    public void CancelledPreparationDoesNotProduceAFrame()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => LiquidGlassCoordinator.PrepareFrame(
            Image(32, 32), new("Synthetic", 0, 0, 32, 32, true), cancellation.Token));
    }

    internal static BitmapSource Image(int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        source.Freeze();
        return source;
    }
}
