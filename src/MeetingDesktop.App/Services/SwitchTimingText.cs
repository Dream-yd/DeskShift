using System.Globalization;
using System.Text;
using MeetingDesktop.App.Localization;

namespace MeetingDesktop.App.Services;

internal static class SwitchTimingText
{
    internal static string Format(IReadOnlyList<SwitchTimingResult> results)
    {
        var loc = LocalizationManager.Instance;
        if (results.Count == 0) return loc["TimingEmpty"];
        var text = new StringBuilder();
        text.AppendLine(loc["TimingScope"]);
        foreach (bool returning in new[] { false, true })
        {
            double[] times = results.Where(r => r.Returning == returning && r.Succeeded)
                .Select(r => r.TotalMilliseconds).Order().ToArray();
            if (times.Length == 0) continue;
            double median = (times[(times.Length - 1) / 2] + times[times.Length / 2]) / 2;
            text.AppendLine(loc.Format("TimingSummary", Direction(returning), times.Length, Number(median), Number(times[^1])));
        }
        foreach (var result in results.Reverse())
            text.AppendLine(loc.Format("TimingRun", Direction(result.Returning),
                loc[result.Succeeded ? "TimingSucceeded" : "TimingFailed"], Number(result.TotalMilliseconds),
                result.ReadyMilliseconds is { } ready ? Number(ready) : loc["CommonNotAvailable"]));
        text.AppendLine(loc["TimingLatestStages"]);
        foreach (var stage in results[^1].Stages)
        {
            string label = loc[stage.Name switch { "Starting" => "TimingStarting", "Finishing" => "TimingFinishing", _ => "Progress" + stage.Name }];
            text.Append("  ").Append(label).Append(": ").Append(Number(stage.Milliseconds)).AppendLine(" ms");
        }
        return text.ToString();
    }

    private static string Direction(bool returning) => LocalizationManager.Instance[returning ? "TimingReturn" : "TimingPrepare"];
    private static string Number(double value) => value.ToString("0.0", CultureInfo.CurrentCulture);
}
