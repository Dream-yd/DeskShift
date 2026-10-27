using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class AutomaticSnapshotPolicyTests
{
    [Fact]
    public void ChangedSnapshotSaveHasPublicContract()
    {
        var method = typeof(LayoutStore).GetMethod("SaveAutomaticIfChanged",
            [typeof(LayoutSnapshot), typeof(string), typeof(DateTimeOffset)]);
        Assert.NotNull(method);
        Assert.Equal(typeof(string), method.ReturnType);
    }

    [Fact]
    public void AutomaticRetentionHasPublicContract()
    {
        var method = typeof(LayoutStore).GetMethod("PruneAutomatic",
            [typeof(int), typeof(DateTimeOffset), typeof(IReadOnlyCollection<string>)]);
        Assert.NotNull(method);
        Assert.Equal(typeof(int), method.ReturnType);
    }

    [Fact]
    public void SnapshotHasOptionalSourceProvenance()
    {
        var property = typeof(LayoutSnapshot).GetProperty("SourceDesktopPath");
        Assert.NotNull(property);
        Assert.Equal(typeof(string), property.PropertyType);
        Assert.Null(property.GetValue(new LayoutSnapshot()));
    }
}
