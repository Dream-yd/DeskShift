using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Services;

internal sealed record SwitchStageTiming(string Name, double Milliseconds);
internal sealed record SwitchTimingResult(bool Returning, bool Succeeded, double TotalMilliseconds,
    double? ReadyMilliseconds, IReadOnlyList<SwitchStageTiming> Stages);

/// <summary>Bounded, in-memory timings. No paths, icon names or progress arguments are retained.</summary>
internal sealed class SwitchTimingHistory
{
    private readonly object _sync = new();
    private readonly Queue<SwitchTimingResult> _results = new();
    internal void Add(SwitchTimingResult result)
    {
        lock (_sync)
        {
            _results.Enqueue(result);
            while (_results.Count > 20) _results.Dequeue();
        }
    }
    internal SwitchTimingResult[] Snapshot() { lock (_sync) return _results.ToArray(); }
}

internal sealed class SwitchTimingTrace(bool returning, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly List<SwitchStageTiming> _stages = [];
    private readonly long _started = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private double _cursor;
    private double? _ready;
    private string _stage = "Starting";
    private SwitchTimingResult? _finished;
    internal bool IsDesktopReady { get { lock (_sync) return _ready is not null; } }

    internal void Record(ProgressKey key)
    {
        lock (_sync)
        {
            if (_finished is not null || _ready is not null || key == ProgressKey.Done || _stage == key.ToString()) return;
            Advance(key.ToString());
        }
    }
    internal void DesktopReady()
    {
        lock (_sync)
        {
            if (_finished is not null || _ready is not null) return;
            Advance("Finishing");
            _ready = _cursor;
        }
    }
    internal SwitchTimingResult Finish(bool succeeded)
    {
        lock (_sync)
        {
            if (_finished is not null) return _finished;
            Advance("Finished");
            return _finished = new(returning, succeeded, _cursor, _ready, _stages.ToArray());
        }
    }
    private void Advance(string next)
    {
        double elapsed = _clock.GetElapsedTime(_started).TotalMilliseconds;
        _stages.Add(new(_stage, elapsed - _cursor));
        _cursor = elapsed;
        _stage = next;
    }
}

internal sealed class TimedSwitchProgress(SwitchTimingTrace? trace, IProgress<ProgressReport> ui) : IProgress<ProgressReport>
{
    public void Report(ProgressReport value)
    {
        trace?.Record(value.Key);
        ui.Report(value);
    }
}
