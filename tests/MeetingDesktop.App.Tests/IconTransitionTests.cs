using MeetingDesktop.App.Services;

namespace MeetingDesktop.App.Tests;

public sealed class IconTransitionTests
{
    [Fact]
    public void BusyExplorerCannotSkipTheFirstVisibleRevealFrames()
    {
        var layer = new Layer();
        using var state = new IconTransitionState(() => layer);
        state.Begin("return", 0);
        state.Hide("return");
        state.End("return", 1000);

        // A native style call or a busy compositor can stall the helper. The
        // first frame after that stall must still start near transparency.
        state.Tick(1140);
        Assert.InRange(layer.Alpha, 1, 32);
        Assert.True(state.Active);
        byte first = layer.Alpha;
        state.Tick(1500);
        Assert.InRange(layer.Alpha, first + 1, 64);
        Assert.True(state.Active);
        for (int now = 1516; now <= 1820; now += 16) state.Tick(now);
        Assert.Equal(255, layer.Alpha);
        Assert.False(state.Active);
    }

    [Fact]
    public void FinishedFadeDoesNotKeepWritingToExplorer()
    {
        var layer = new Layer();
        using var state = new IconTransitionState(() => layer);
        state.Begin("one", 0);
        Assert.True(state.Animating);
        state.Tick(100);
        Assert.False(state.Animating);
        int writes = layer.Writes;
        for (int now = 108; now < 1000; now += 8) state.Tick(now);
        Assert.Equal(writes, layer.Writes);
        Assert.Equal(0, layer.Alpha);
        Assert.True(state.Active);
        state.End("one", 1000);
        Assert.True(state.Animating);
        state.Tick(1080);
        Assert.InRange(layer.Alpha, 1, 254);
    }

    [Fact]
    public void CancellationWhileHiddenRestoresWithoutAnotherCommand()
    {
        bool cancelled = false;
        var layer = new Layer();
        using var state = new IconTransitionState(() => layer, () => cancelled);
        state.Begin("one", 0);
        state.Hide("one");
        cancelled = true;
        state.Tick(10);
        Assert.False(state.Active);
        Assert.Equal(255, layer.Alpha);
    }

    [Fact]
    public void PreparationFadesWhileWorkRunsAndHideDoesNotWaitForAnimation()
    {
        var layer = new Layer();
        using var state = new IconTransitionState(() => layer);
        state.Begin("one", 0);
        state.Tick(40);
        Assert.InRange(layer.Alpha, 1, 254);
        Assert.True(state.Hide("one"));
        Assert.Equal(0, layer.Alpha);
        state.Tick(1000);
        Assert.Equal(0, layer.Alpha);
        Assert.False(layer.Disposed);
        state.End("one", 1000);
        state.Tick(1080);
        Assert.InRange(layer.Alpha, 1, 254);
        for (int now = 1096; now <= 1400; now += 16) state.Tick(now);
        Assert.True(layer.Disposed);
    }

    [Fact]
    public void EarlyFailureAndWatchdogAlwaysReleaseTheLayer()
    {
        var layer = new Layer();
        using (var state = new IconTransitionState(() => layer))
        {
            state.Begin("one", 0);
            state.End("one", 10);
            for (int now = 26; now <= 266; now += 16) state.Tick(now);
            Assert.True(layer.Disposed);
        }
        layer = new Layer();
        using var timed = new IconTransitionState(() => layer);
        timed.Begin("two", 0);
        timed.Hide("two");
        timed.Tick(30001);
        Assert.True(layer.Disposed);
    }

    [Fact]
    public void PreviousFadeCannotRevealTheNextSwitch()
    {
        var layers = new List<Layer>();
        using var state = new IconTransitionState(() => { var l = new Layer(); layers.Add(l); return l; });
        state.Begin("one", 0);
        state.End("one", 10);
        state.Begin("two", 20);
        state.Hide("two");
        state.End("one", 30);
        state.Tick(250);
        Assert.Single(layers);
        Assert.Equal(0, layers[0].Alpha);
        Assert.False(layers[0].Disposed);
    }

    [Fact]
    public void NativeFailureRestoresAndDoesNotBlockSwitching()
    {
        var layer = new Layer();
        using var state = new IconTransitionState(() => layer);
        state.Begin("one", 0);
        layer.Fail = true;
        Assert.False(state.Hide("one"));
        Assert.True(layer.Disposed);
    }

    [Fact]
    public void UnsupportedDesktopDoesNotAcquireOrWait()
    {
        using var state = new IconTransitionState(() => null);
        Assert.False(state.Begin("one", 0));
        Assert.False(state.Hide("one"));
        state.End("one", 0);
        state.Tick(100);
    }

    [Fact]
    public void FailedRestorationRemainsOwnedUntilARetrySucceeds()
    {
        var layer = new Layer { FailRestore = true };
        using var state = new IconTransitionState(() => layer);
        state.Begin("one", 0);
        state.Hide("one");
        state.Dispose();
        Assert.True(state.Active);
        layer.FailRestore = false;
        state.Tick(20);
        Assert.False(state.Active);
        Assert.True(layer.Disposed);
    }

    [Fact]
    public void IndependentAbandonmentPreventsALateHide()
    {
        bool abandoned = false;
        var layer = new Layer();
        using var state = new IconTransitionState(() => layer, () => abandoned);
        state.Begin("one", 0);
        abandoned = true;
        Assert.False(state.Hide("one"));
        Assert.True(layer.Disposed);
        Assert.Equal(255, layer.Alpha);
    }

    private sealed class Layer : IIconOpacityLayer
    {
        public byte Alpha { get; private set; } = 255;
        public bool Disposed { get; private set; }
        public bool Fail { get; set; }
        public bool FailRestore { get; set; }
        public int Writes { get; private set; }
        public bool SetAlpha(byte alpha)
        {
            if (Fail) return false;
            if (Alpha != alpha) Writes++;
            Alpha = alpha;
            return true;
        }
        public bool Restore() { if (FailRestore) return false; Disposed = true; Alpha = 255; return true; }
        public void Dispose() => Restore();
    }
}
