using MeetingDesktop.Core;

namespace MeetingDesktop.Core.Tests;

/// <summary>
/// Tests for the icon grid pitch derivation.
/// </summary>
/// <remarks>
/// All coordinates here are synthetic. Real desktop icon names and the
/// measurements taken from a specific machine must never end up in the test
/// fixtures - they are personal data and they make the test meaningless on
/// any other machine.
/// </remarks>
public class GridMetricsTests
{
    [Fact]
    public void DerivePitch_ReturnsZero_WhenThereIsNothingToMeasure()
    {
        Assert.Equal(0, GridMetrics.DerivePitchFromObservation(Array.Empty<int>()));
        Assert.Equal(0, GridMetrics.DerivePitchFromObservation(new[] { 100 }));
    }

    [Fact]
    public void DerivePitch_ReturnsZero_WhenEveryValueIsTheSame()
    {
        Assert.Equal(0, GridMetrics.DerivePitchFromObservation(new[] { 40, 40, 40 }));
    }

    [Fact]
    public void DerivePitch_FindsTheLatticeSpacing()
    {
        int pitch = GridMetrics.DerivePitchFromObservation(new[] { 0, 75, 150, 225, 300 });

        Assert.Equal(75, pitch);
    }

    [Fact]
    public void DerivePitch_IsUnaffectedByInputOrderOrDuplicates()
    {
        int pitch = GridMetrics.DerivePitchFromObservation(new[] { 300, 0, 150, 75, 150, 75 });

        Assert.Equal(75, pitch);
    }

    /// <summary>
    /// The regression this whole method exists for.
    /// </summary>
    /// <remarks>
    /// The prototype took the <em>minimum</em> positive gap. "Align icons to
    /// grid" can be turned off, so a user who has dragged one icon a little
    /// off the lattice introduces a tiny gap - 12 here - and the minimum then
    /// reports 12 as the pitch. A parking lattice built on a 12 px pitch puts
    /// parked icons on top of each other, and the two-phase move cascades.
    ///
    /// With one off-grid value among five on-grid ones the mode is still 75,
    /// so the answer is correct. If this test ever fails, someone has
    /// reintroduced min().
    /// </remarks>
    [Fact]
    public void DerivePitch_IgnoresASingleOffGridIcon_WhereMinimumWouldNot()
    {
        int[] withOneStrayIcon = { 0, 12, 75, 150, 225, 300 };

        int pitch = GridMetrics.DerivePitchFromObservation(withOneStrayIcon);

        Assert.Equal(75, pitch);
        Assert.NotEqual(12, pitch);
    }

    [Fact]
    public void DerivePitch_HandlesIconsSpacedTwoCellsApart()
    {
        // Every icon two cells apart: the only observable gap is twice the pitch.
        int pitch = GridMetrics.DerivePitchFromObservation(new[] { 0, 150, 300, 450 });

        Assert.Equal(150, pitch);
    }

    [Fact]
    public void DerivePitch_IgnoresImplausiblyLargeGaps()
    {
        // A gap wider than any real icon cell carries no information about
        // the pitch and must not become the answer.
        int pitch = GridMetrics.DerivePitchFromObservation(new[] { 0, 75, 75 + 4000 });

        Assert.Equal(75, pitch);
    }

    [Fact]
    public void ResolveColumnPitch_FallsBackToTheHistoricalDefault_WhenNothingIsObservable()
    {
        // No listview handle, no registry override in the test host, no
        // observations - the last resort is the 96-DPI default.
        int pitch = GridMetrics.ResolveColumnPitch(Array.Empty<int>());

        Assert.Equal(75, pitch);
    }
}
