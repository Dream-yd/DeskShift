using System.Runtime.InteropServices;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class LiveDesktopSwitcherTests
{
    [Fact]
    public void ReadyFastPathUsesEightyMillisecondsAcrossBothProofPhases()
    {
        using var harness = new Harness();

        Assert.Empty(harness.Run());

        Assert.Equal(3, harness.Shell.MarkerReads);
        Assert.Equal(3, harness.Shell.CleanupReads);
        // The old fixed cadence took four 80 ms pauses (320 ms).
        Assert.Equal(TimeSpan.FromMilliseconds(80), harness.Clock.Elapsed);
        Assert.Equal(new[] { 20d, 20d, 20d, 20d }, harness.Pauses.Select(delay => delay.TotalMilliseconds));
    }

    [Theory]
    [InlineData(false, "stale")]
    [InlineData(true, "stale")]
    [InlineData(false, "com")]
    [InlineData(true, "com")]
    [InlineData(false, "cast")]
    [InlineData(true, "cast")]
    [InlineData(false, "unavailable")]
    [InlineData(true, "unavailable")]
    [InlineData(false, "file")]
    [InlineData(true, "file")]
    public void InterruptedProofBacksOffAndRequiresThreeFreshSamples(bool cleanup, string interruption)
    {
        using var harness = new Harness();
        harness.Shell.Read = () =>
        {
            bool inCleanup = harness.Shell.CleanupReads > 0;
            int reads = cleanup ? harness.Shell.CleanupReads : harness.Shell.MarkerReads;
            if (inCleanup == cleanup && reads == 3)
                return interruption switch
                {
                    "com" => throw new COMException("View changed."),
                    "cast" => throw new InvalidCastException("View changed."),
                    "unavailable" => throw new DesktopViewUnavailableException("View changed."),
                    "file" => throw new FileNotFoundException("Item disappeared."),
                    _ => [new("old", 0, 0, Path.Combine(harness.Root, "old.txt"), true)]
                };
            return harness.Shell.Current();
        };

        Assert.Empty(harness.Run());

        Assert.Equal(cleanup ? 3 : 6, harness.Shell.MarkerReads);
        Assert.Equal(cleanup ? 6 : 3, harness.Shell.CleanupReads);
        double[] interruptedPhase = [20, 20, 80, 20, 20];
        double[] readyPhase = [20, 20];
        var expected = cleanup ? readyPhase.Concat(interruptedPhase) : interruptedPhase.Concat(readyPhase);
        Assert.Equal(expected, harness.Pauses.Select(delay => delay.TotalMilliseconds));
        Assert.Equal(TimeSpan.FromMilliseconds(200), harness.Clock.Elapsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedMatchingFingerprintRestartsStabilityAtTheShortCadence(bool cleanup)
    {
        using var harness = new Harness();
        harness.Shell.Read = () =>
        {
            var current = harness.Shell.Current();
            bool inCleanup = harness.Shell.CleanupReads > 0;
            int reads = cleanup ? harness.Shell.CleanupReads : harness.Shell.MarkerReads;
            // Public items are allowed in a fully matching view, but changing that
            // view after two samples must still restart the three-sample proof.
            return inCleanup == cleanup && reads <= 2
                ? current.Concat([new IconPosition("shared", 0, 0, Path.Combine(harness.Root, "Public", "shared.lnk"), true)]).ToArray()
                : current;
        };

        Assert.Empty(harness.Run());

        Assert.Equal(cleanup ? 3 : 5, harness.Shell.MarkerReads);
        Assert.Equal(cleanup ? 5 : 3, harness.Shell.CleanupReads);
        Assert.Equal(6, harness.Pauses.Count);
        Assert.All(harness.Pauses, delay => Assert.Equal(TimeSpan.FromMilliseconds(20), delay));
        Assert.Equal(TimeSpan.FromMilliseconds(120), harness.Clock.Elapsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PermanentlyStalePhaseBacksOffUntilTheSharedDeadlineWithoutSpinning(bool cleanup)
    {
        using var harness = new Harness();
        IReadOnlyList<IconPosition> binding = [];
        harness.Shell.Read = () =>
        {
            if (!cleanup) return [];
            if (harness.Shell.CleanupReads == 0) binding = harness.Shell.Current();
            return binding; // The deleted binding marker must disappear from the view, too.
        };

        Assert.Throws<DesktopViewVerificationException>(() => harness.Run());

        Assert.Equal(TimeSpan.FromSeconds(18), harness.Clock.Elapsed);
        Assert.Equal(cleanup ? 3 : 225, harness.Shell.MarkerReads);
        Assert.Equal(cleanup ? 225 : 0, harness.Shell.CleanupReads);
        var backoffs = harness.Pauses.Skip(cleanup ? 2 : 0).ToArray();
        Assert.Equal(225, backoffs.Length);
        Assert.All(backoffs[..^1], delay => Assert.Equal(TimeSpan.FromMilliseconds(80), delay));
        Assert.Equal(TimeSpan.FromMilliseconds(cleanup ? 40 : 80), backoffs[^1]);
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void ThirdMatchingCaptureAtOrAfterDeadlineCannotSucceed(bool cleanup, int overshootMilliseconds)
    {
        using var harness = new Harness();
        harness.Shell.Read = () =>
        {
            bool inCleanup = harness.Shell.CleanupReads > 0;
            int reads = cleanup ? harness.Shell.CleanupReads : harness.Shell.MarkerReads;
            if (inCleanup == cleanup && reads == 3)
                harness.Clock.Advance(TimeSpan.FromMilliseconds(18000 + overshootMilliseconds) - harness.Clock.Elapsed);
            return harness.Shell.Current();
        };

        Assert.Throws<DesktopViewVerificationException>(() => harness.Run());

        Assert.Equal(3, harness.Shell.MarkerReads);
        Assert.Equal(cleanup ? 3 : 0, harness.Shell.CleanupReads);
        Assert.Equal(cleanup ? 4 : 2, harness.Pauses.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(18000 + overshootMilliseconds), harness.Clock.Elapsed);
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PollDelayIsCappedByTheRemainingBudget(bool ready)
    {
        using var harness = new Harness();
        harness.Shell.OnBaseline = () => harness.Clock.Advance(TimeSpan.FromMilliseconds(17990));
        harness.Shell.Read = () => ready ? harness.Shell.Current() : [];

        Assert.Throws<DesktopViewVerificationException>(() => harness.Run());

        Assert.Equal(TimeSpan.FromMilliseconds(10), Assert.Single(harness.Pauses));
        Assert.Equal(TimeSpan.FromSeconds(18), harness.Clock.Elapsed);
        Assert.Equal(1, harness.Shell.MarkerReads);
        Assert.Equal(0, harness.Shell.CleanupReads);
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Fact]
    public void AlreadyReboundDesktopDoesNotForceAnotherShellRefresh()
    {
        using var harness = new Harness();
        int refreshes = 0;
        harness.Shell.OnRefresh = () => refreshes++;
        Assert.Empty(harness.Run());
        Assert.Equal(0, refreshes);
        Assert.Equal(3, harness.Shell.MarkerReads);
        Assert.Equal(3, harness.Shell.CleanupReads);
    }

    [Fact]
    public void StaleViewGetsOneRefreshThenStillRequiresBindingProof()
    {
        using var harness = new Harness();
        int refreshes = 0;
        harness.Shell.OnRefresh = () => refreshes++;
        harness.Shell.Read = () => refreshes == 0 ? [] : harness.Shell.Current();
        Assert.Empty(harness.Run());
        Assert.Equal(1, refreshes);
    }
    [Fact]
    public void ADeletedProofCannotMakeTwoEmptyDesktopsLookSuccessfullySwitched()
    {
        using var harness = new Harness();
        harness.Shell.AfterWrite = () => File.Delete(Assert.Single(Directory.GetFiles(harness.Target)));
        var failure = Assert.Throws<DesktopViewVerificationException>(() => harness.Run());
        Assert.Contains("marker", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Fact]
    public void MarkerAndCleanupShareOneBudgetInsteadOfExceedingTheHelperDeadline()
    {
        using var harness = new Harness();
        bool markerSeen = false;
        harness.Shell.Read = () =>
        {
            bool marker = Directory.GetFiles(harness.Target).Length != 0;
            if (marker && !markerSeen) { harness.Clock.Advance(TimeSpan.FromSeconds(10)); markerSeen = true; }
            if (!marker && markerSeen) { harness.Clock.Advance(TimeSpan.FromSeconds(10)); return harness.Shell.Previous; }
            return harness.Shell.Current();
        };
        Assert.Throws<DesktopViewVerificationException>(() => harness.Run());
        Assert.True(harness.Clock.Elapsed < TimeSpan.FromSeconds(21), "A second full phase budget exceeded the 20-second helper limit.");
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Fact]
    public void SlowButProgressingBindingCanUseTheSharedBudget()
    {
        using var harness = new Harness();
        harness.Shell.Read = () =>
        {
            if (harness.Clock.Elapsed < TimeSpan.FromSeconds(13)) return [];
            return harness.Shell.Current();
        };
        Assert.Empty(harness.Run());
        Assert.InRange(harness.Clock.Elapsed.TotalSeconds, 13, 18);
    }

    [Fact]
    public void StableProofAndCleanupAreBothRequired()
    {
        using var harness = new Harness();
        Assert.Empty(harness.Run());
        Assert.Equal(3, harness.Shell.MarkerReads);
        Assert.Equal(3, harness.Shell.CleanupReads);
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATemporarilyMissingViewIsRetriedBeforeTheSameDeadline(bool duringRefresh)
    {
        using var harness = new Harness();
        int failures = 0;
        if (duringRefresh)
        {
            harness.Shell.Read = () => failures < 3 ? [] : harness.Shell.Current();
            harness.Shell.OnRefresh = () =>
            {
                if (failures++ < 2) throw new DesktopViewUnavailableException("No view yet.");
            };
        }
        else
            harness.Shell.Read = () =>
            {
                if (failures++ < 2) throw new DesktopViewUnavailableException("View changed during enumeration.");
                return harness.Shell.Current();
            };
        Assert.Empty(harness.Run());
        Assert.True(failures >= 3);
    }

    [Fact]
    public void StaleOriginalContentsNeverPassEvenWhenTheRegisteredPathChanged()
    {
        using var harness = new Harness();
        harness.Shell.Read = () => [new("old", 0, 0, Path.Combine(harness.Root, "old.txt"), true)];
        Assert.Throws<DesktopViewVerificationException>(() => harness.Run());
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Fact]
    public void LateSuccessfulReadsCannotWinAfterTheDeadline()
    {
        using var harness = new Harness();
        harness.Shell.Read = () =>
        {
            harness.Clock.Advance(TimeSpan.FromSeconds(7));
            return harness.Shell.Current();
        };
        Assert.Throws<DesktopViewVerificationException>(() => harness.Run());
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Fact]
    public void ASlowBaselineCannotStartMutatingAfterItsBudgetIsGone()
    {
        using var harness = new Harness();
        harness.Shell.OnBaseline = () => harness.Clock.Advance(TimeSpan.FromSeconds(19));
        Assert.Throws<DesktopViewVerificationException>(() => harness.Run());
        Assert.False(harness.Shell.Written);
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Fact]
    public void TransientBaselineReadRetriesBeforeAnyMutation()
    {
        using var harness = new Harness();
        int reads = 0;
        harness.Shell.OnBaseline = () =>
        {
            Assert.False(harness.Shell.Written);
            Assert.Empty(Directory.GetFiles(harness.Target));
            if (reads++ < 2) throw new DesktopViewUnavailableException("Baseline view changed.");
        };
        Assert.Empty(harness.Run());
        Assert.Equal(3, reads);
    }

    [Fact]
    public void PermanentlyUnavailableBaselineExpiresWithoutWritingAnything()
    {
        using var harness = new Harness();
        harness.Shell.OnBaseline = () => throw new DesktopViewUnavailableException("Missing view.");
        Assert.Throws<DesktopViewVerificationException>(() => harness.Run());
        Assert.False(harness.Shell.Written);
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Fact]
    public void BindingProofMustBeAnActualFilesystemItem()
    {
        using var harness = new Harness();
        harness.Shell.Read = () => harness.Shell.Current().Select(icon => icon with { IsFileSystem = false }).ToArray();
        Assert.Throws<DesktopViewVerificationException>(() => harness.Run());
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Fact]
    public void UnrelatedFilesystemErrorsAreNotRetriedAndAlwaysReleaseTheMarker()
    {
        using var harness = new Harness();
        var error = new UnauthorizedAccessException("Access revoked.");
        harness.Shell.Read = () => throw error;
        Assert.Same(error, Assert.Throws<UnauthorizedAccessException>(() => harness.Run()));
        Assert.Empty(Directory.GetFiles(harness.Target));
    }

    [Fact]
    public void ResultIsTheLastVerifiedSnapshotWithoutAnExtraUnverifiedCapture()
    {
        using var harness = new Harness();
        var result = harness.Run();
        Assert.Same(harness.Shell.Previous, result);
        Assert.Equal(6, harness.Shell.MarkerReads + harness.Shell.CleanupReads);
    }

    private sealed class Harness : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "MeetingDesktopTests", Guid.NewGuid().ToString("N"));
        internal string Target { get; }
        internal Clock Clock { get; } = new();
        internal List<TimeSpan> Pauses { get; } = [];
        internal FakeShell Shell { get; }
        internal Harness()
        {
            Target = Directory.CreateDirectory(Path.Combine(Root, "Target")).FullName;
            Shell = new(Target, Path.Combine(Root, "Public"));
        }
        internal IReadOnlyList<IconPosition> Run() => new LiveDesktopSwitcher(Shell, Clock, delay =>
        {
            Assert.True(delay > TimeSpan.Zero, "Polling must yield for a positive interval.");
            Assert.True(Pauses.Count < 1000, "Polling failed to make bounded progress.");
            Pauses.Add(delay);
            Clock.Advance(delay);
        }).Run(Target);
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Clock : TimeProvider
    {
        internal TimeSpan Elapsed { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        internal void Advance(TimeSpan elapsed) => Elapsed += elapsed;
    }

    private sealed class FakeShell(string target, string publicPath) : ILiveDesktopShell
    {
        private bool redirected;
        internal Action? AfterWrite { get; set; }
        internal Action? OnBaseline { get; set; }
        internal bool Written => redirected;
        internal Action? OnRefresh { get; set; }
        internal Func<IReadOnlyList<IconPosition>>? Read { get; set; }
        internal IReadOnlyList<IconPosition> Previous { get; private set; } = [];
        internal int MarkerReads { get; private set; }
        internal int CleanupReads { get; private set; }
        public IReadOnlyList<IconPosition> Capture()
        {
            if (!redirected) { OnBaseline?.Invoke(); return []; }
            if (Directory.GetFiles(target).Length != 0) MarkerReads++; else CleanupReads++;
            Previous = Read?.Invoke() ?? Current();
            return Previous;
        }
        internal IReadOnlyList<IconPosition> Current() => Directory.GetFiles(target)
            .Select(path => new IconPosition(Path.GetFileName(path), 0, 0, path, true)).ToArray();
        public string ReadDesktopPath() => target;
        public string ReadPublicDesktopPath() => publicPath;
        public void WriteDesktopPath(string path) { redirected = true; AfterWrite?.Invoke(); }
        public void NotifySettings() { }
        public void NotifyDirectory(string path) { }
        public void Refresh() => OnRefresh?.Invoke();
    }
}
