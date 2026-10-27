using MeetingDesktop.App.Services;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Tests;

public sealed class SwitchTimingTests
{
    [Fact]
    public void SummarySeparatesDirectionsAndExcludesFailuresFromStatistics()
    {
        var loc = MeetingDesktop.App.Localization.LocalizationManager.Instance;
        var result = SwitchTimingText.Format([
            new(false, true, 100, 80, []), new(false, true, 200, 180, []),
            new(false, false, 10000, null, []), new(true, true, 500, 400, [])]);
        string Number(double value) => value.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
        Assert.Contains(loc.Format("TimingSummary", loc["TimingPrepare"], 2, Number(150), Number(200)), result);
        Assert.Contains(loc.Format("TimingSummary", loc["TimingReturn"], 1, Number(500), Number(500)), result);
        Assert.Contains(loc["TimingFailed"], result);
    }

    [Fact]
    public void RecordsReadySeparatelyFromFinishingAndIgnoresLateProgress()
    {
        var clock = new Clock();
        var trace = new SwitchTimingTrace(false, clock);
        clock.Now = 10; trace.Record(ProgressKey.ReadingCurrentLayout);
        clock.Now = 30; trace.Record(ProgressKey.RefreshingDesktop);
        clock.Now = 90; trace.DesktopReady();
        clock.Now = 100; trace.Record(ProgressKey.Done);
        clock.Now = 130; var result = trace.Finish(true);
        clock.Now = 200; trace.Record(ProgressKey.ApplyingLayout); trace.DesktopReady();
        Assert.Same(result, trace.Finish(false));
        Assert.Equal(90, result.ReadyMilliseconds);
        Assert.Equal(130, result.TotalMilliseconds);
        Assert.Equal(new[] { 10d, 20d, 60d, 40d }, result.Stages.Select(s => s.Milliseconds));
        Assert.Equal(result.TotalMilliseconds, result.Stages.Sum(s => s.Milliseconds));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void RepeatedStageReportsDoNotRestartItsTimer()
    {
        var clock = new Clock();
        var trace = new SwitchTimingTrace(true, clock);
        trace.Record(ProgressKey.ApplyingLayout);
        clock.Now = 10; trace.Record(ProgressKey.ApplyingLayout);
        clock.Now = 30; var result = trace.Finish(false);
        Assert.Equal(30, result.Stages.Single(s => s.Name == nameof(ProgressKey.ApplyingLayout)).Milliseconds);
        Assert.Null(result.ReadyMilliseconds);
        Assert.False(result.Succeeded);
        Assert.True(result.Returning);
    }

    [Fact]
    public void ProgressIsTimedAtReportTimeBeforeUiDispatch()
    {
        var clock = new Clock();
        var trace = new SwitchTimingTrace(false, clock);
        var received = new List<ProgressReport>();
        var progress = new TimedSwitchProgress(trace, new Sink(received));
        clock.Now = 15; progress.Report(new(ProgressKey.RefreshingDesktop));
        clock.Now = 45; var result = trace.Finish(true);
        Assert.Single(received);
        Assert.Equal(15, result.Stages[0].Milliseconds);
        Assert.Equal(30, result.Stages[1].Milliseconds);
    }

    [Fact]
    public void HistoryIsBoundedAndSnapshotsCannotModifyIt()
    {
        var history = new SwitchTimingHistory();
        for (int i = 0; i < 25; i++)
        {
            var clock = new Clock(); var trace = new SwitchTimingTrace(false, clock);
            clock.Now = i; history.Add(trace.Finish(true));
        }
        var first = history.Snapshot();
        Assert.Equal(20, first.Length);
        Assert.Equal(5, first[0].TotalMilliseconds);
        first[0] = first[^1];
        Assert.Equal(5, history.Snapshot()[0].TotalMilliseconds);
    }

    private sealed class Clock : TimeProvider
    {
        public long Now { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }
    private sealed class Sink(List<ProgressReport> received) : IProgress<ProgressReport>
    { public void Report(ProgressReport value) => received.Add(value); }
}
