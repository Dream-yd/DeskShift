using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

public sealed class ShortcutCountWorkflowTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(null)]
    public async Task PrepareReturnsBrokerShortcutCountInsteadOfCapturedIconCount(int? shortcutCount)
    {
        using var harness = WorkflowHarness.Create();
        harness.Elevation.LastReport = Report(shortcutCount);

        var result = await harness.PrepareWorkflow().RunAsync(new(harness.MeetingDesktop, false));

        Assert.Equal(2, result.IconCount);
        Assert.Equal(shortcutCount ?? 0, result.ShortcutsMoved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(null)]
    public async Task RestoreReturnsBrokerShortcutCountInsteadOfAlwaysZero(int? shortcutCount)
    {
        using var harness = WorkflowHarness.Create();
        harness.SaveSession(snapshotPath: harness.CreateLayoutSnapshot());
        harness.Elevation.LastReport = Report(shortcutCount);

        var result = await harness.RestoreWorkflow().RunAsync();

        Assert.Equal(shortcutCount ?? 0, result.ShortcutsRestored);
    }

    private static ElevatedOperationReport? Report(int? shortcutCount) => shortcutCount is { } count
        ? new(true, new ShortcutMoveResult(count, [])) : null;
}
