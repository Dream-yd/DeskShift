using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class AutomaticSnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MeetingDesktopSnapshotTests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = [];
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private string Source => Path.Combine(_root, "source");
    private LayoutStore Store => new(Path.Combine(_root, "layouts"));

    private LayoutSnapshot Snapshot(int days = 0) => new()
    {
        CreatedUtc = Now.AddDays(-days),
        IsAutomatic = true,
        SourceDesktopPath = Source,
        Topology = new([new("internal", 0, 0, 1920, 1080, true), new("external", -2560, -100, 2560, 1440, false)]),
        Icons = [new("Alpha", 10, 20, "file:alpha", true), new("Recycle", -50, 30, "shell:recycle", false)],
    };

    [Fact]
    public void IdenticalCaptureReusesFullPathAcrossStoreInstancesWithoutRewriting()
    {
        var first = Snapshot();
        string path = Store.SaveAutomaticIfChanged(first, Source, Now);
        byte[] bytes = File.ReadAllBytes(path);
        DateTime writeTime = File.GetLastWriteTimeUtc(path);
        var reordered = first with
        {
            Name = "another name", CreatedUtc = Now.AddYears(-1), IsAutomatic = false,
            Icons = first.Icons.Reverse().ToArray(),
            Topology = new(first.Topology.Monitors.Reverse().ToArray()),
        };

        string reused = Store.SaveAutomaticIfChanged(reordered, Source.ToUpperInvariant() + Path.DirectorySeparatorChar, Now.AddMinutes(5));

        Assert.Equal(path, reused);
        Assert.True(Path.IsPathFullyQualified(reused));
        Assert.Equal(bytes, File.ReadAllBytes(reused));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(reused));
        Assert.Single(Store.List());
    }

    [Fact]
    public void NewCaptureStampsProvenanceAndAutomaticMetadataWithoutMutatingInput()
    {
        var original = Snapshot(100) with { SourceDesktopPath = "old", IsAutomatic = false };
        string path = Store.SaveAutomaticIfChanged(original, Source, Now);
        var saved = Store.Load(Path.GetFileNameWithoutExtension(path));
        Assert.NotNull(saved);
        Assert.Equal(Source, saved.SourceDesktopPath);
        Assert.Equal(Now, saved.CreatedUtc);
        Assert.True(saved.IsAutomatic);
        Assert.Equal("old", original.SourceDesktopPath);
        Assert.False(original.IsAutomatic);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("identity")]
    [InlineData("identity-null")]
    [InlineData("name")]
    [InlineData("filesystem")]
    [InlineData("filesystem-null")]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("icon-count")]
    [InlineData("icon-duplicate")]
    [InlineData("monitor-name")]
    [InlineData("monitor-x")]
    [InlineData("monitor-y")]
    [InlineData("monitor-width")]
    [InlineData("monitor-height")]
    [InlineData("monitor-primary")]
    [InlineData("monitor-count")]
    public void EveryIdentityPositionAndTopologyFieldParticipatesInComparison(string change)
    {
        var first = Snapshot();
        string path = Store.SaveAutomaticIfChanged(first, Source, Now);
        var icons = first.Icons.ToArray();
        var monitors = first.Topology.Monitors.ToArray();
        string source = Source;
        switch (change)
        {
            case "source": source += "-other"; break;
            case "identity": icons[0] = icons[0] with { Identity = "other" }; break;
            case "identity-null": icons[0] = icons[0] with { Identity = null }; break;
            case "name": icons[0] = icons[0] with { Name = "alpha" }; break;
            case "filesystem": icons[0] = icons[0] with { IsFileSystem = false }; break;
            case "filesystem-null": icons[0] = icons[0] with { IsFileSystem = null }; break;
            case "x": icons[0] = icons[0] with { X = 11 }; break;
            case "y": icons[0] = icons[0] with { Y = 21 }; break;
            case "icon-count": icons = [icons[0]]; break;
            case "icon-duplicate": icons = [icons[0], icons[0]]; break;
            case "monitor-name": monitors[0] = monitors[0] with { DeviceName = "renamed" }; break;
            case "monitor-x": monitors[0] = monitors[0] with { X = 1 }; break;
            case "monitor-y": monitors[0] = monitors[0] with { Y = 1 }; break;
            case "monitor-width": monitors[0] = monitors[0] with { Width = 1280 }; break;
            case "monitor-height": monitors[0] = monitors[0] with { Height = 720 }; break;
            case "monitor-primary":
                monitors[0] = monitors[0] with { IsPrimary = false };
                monitors[1] = monitors[1] with { IsPrimary = true };
                break;
            case "monitor-count": monitors = [monitors[0]]; break;
        }

        string changed = Store.SaveAutomaticIfChanged(first with { Icons = icons, Topology = new(monitors) }, source, Now);
        Assert.NotEqual(path, changed);
        Assert.Equal(2, Store.List().Count);
    }

    [Fact]
    public void ReturningToHistoricLayoutCreatesANewCaptureThenReusesIt()
    {
        var snapshot = Snapshot();
        var store = Store;
        string a = store.SaveAutomaticIfChanged(snapshot, Source, Now.AddMinutes(-2));
        string b = store.SaveAutomaticIfChanged(snapshot with { Icons = [] }, Source, Now.AddMinutes(-1));
        string returnedA = store.SaveAutomaticIfChanged(snapshot, Source, Now);
        Assert.NotEqual(a, returnedA);
        Assert.NotEqual(b, returnedA);
        Assert.Equal(3, store.List().Count);
        Assert.Equal(returnedA, store.SaveAutomaticIfChanged(snapshot, Source, Now.AddMinutes(1)));
    }

    [Fact]
    public void ReturningToHistoricMonitorSetupCreatesANewCapture()
    {
        var snapshot = Snapshot();
        var store = Store;
        string external = store.SaveAutomaticIfChanged(snapshot, Source, Now.AddMinutes(-2));
        store.SaveAutomaticIfChanged(snapshot with { Topology = new([snapshot.Topology.Monitors[0]]) }, Source, Now.AddMinutes(-1));
        string returnedExternal = store.SaveAutomaticIfChanged(snapshot, Source, Now);
        Assert.NotEqual(external, returnedExternal);
        Assert.Equal(3, store.List().Count);
    }

    [Fact]
    public void NewerCaptureForAnotherSourceDoesNotInterfereWithReuse()
    {
        var store = Store;
        string first = store.SaveAutomaticIfChanged(Snapshot(), Source, Now);
        store.SaveAutomaticIfChanged(Snapshot() with { Icons = [] }, Source + "-other", Now.AddMinutes(1));
        Assert.Equal(first, store.SaveAutomaticIfChanged(Snapshot(), Source, Now.AddMinutes(2)));
    }

    [Fact]
    public void EqualTimestampCollisionsStillCompareMostRecentlySavedCapture()
    {
        var store = Store;
        for (int index = 0; index < 12; index++)
        {
            var snapshot = Snapshot() with { Icons = [new("Moving", index, 0)] };
            string latest = store.SaveAutomaticIfChanged(snapshot, Source, Now);
            Assert.Equal(index + 1, store.List().Count);
            Assert.Equal(latest, store.SaveAutomaticIfChanged(snapshot, Source, Now));
        }
    }

    [Fact]
    public void ExistingSaveAutomaticStillCreatesEveryCapture()
    {
        var store = Store;
        var snapshot = Snapshot();
        string first = store.SaveAutomatic(snapshot, Now);
        string second = store.SaveAutomatic(snapshot, Now);
        Assert.NotEqual(first, second);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void StoreDirectoryTrailingSeparatorDoesNotDisableDeduplicationOrRetention()
    {
        var store = new LayoutStore(Path.Combine(_root, "layouts") + Path.DirectorySeparatorChar);
        string old = store.SaveAutomatic(Snapshot(100), Now.AddDays(-100));
        Assert.Equal(old, store.SaveAutomaticIfChanged(Snapshot(), Source, Now));
        string latest = store.SaveAutomatic(Snapshot(), Now);
        Assert.Equal(1, store.PruneAutomatic(7, Now, []));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(latest));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(" ")]
    [InlineData("wildcard*")]
    [InlineData("question?")]
    [InlineData("stream:other")]
    public void InvalidSourceIsRejectedWithoutWriting(string suffix)
    {
        string source = suffix is "relative" or " " ? suffix : Path.Combine(Source, suffix);
        var store = Store;
        Assert.Throws<ArgumentException>(() => store.SaveAutomaticIfChanged(Snapshot(), source, Now));
        Assert.Empty(store.List());
    }

    [Fact]
    public void LegacyJsonStillLoadsWithoutProvenance()
    {
        var store = Store;
        var json = JsonSerializer.SerializeToNode(Snapshot())!.AsObject();
        json.Remove("SourceDesktopPath");
        File.WriteAllText(Path.Combine(_root, "layouts", "legacy.json"), json.ToJsonString());
        Assert.Null(store.Load("legacy")!.SourceDesktopPath);
    }

    [Theory]
    [InlineData("manual-prefix")]
    [InlineData("manual-flag")]
    [InlineData("legacy")]
    [InlineData("relative-source")]
    [InlineData("blank-source")]
    [InlineData("corrupt")]
    [InlineData("new-schema")]
    [InlineData("old-schema")]
    [InlineData("missing-schema")]
    [InlineData("missing-time")]
    [InlineData("missing-icons")]
    [InlineData("null-icons")]
    [InlineData("null-icon")]
    [InlineData("missing-position")]
    [InlineData("null-topology")]
    [InlineData("missing-monitors")]
    [InlineData("empty-monitors")]
    [InlineData("missing-geometry")]
    [InlineData("null-monitor")]
    [InlineData("invalid-geometry")]
    [InlineData("invalid-source")]
    [InlineData("duplicate-fields")]
    public void UnprovenOrInvalidFilesAreNeitherReusedNorPruned(string kind)
    {
        var store = Store;
        var json = JsonSerializer.SerializeToNode(Snapshot(100))!.AsObject();
        string stem = kind == "manual-prefix" ? "manual" : "auto-unusable";
        switch (kind)
        {
            case "manual-flag": json["IsAutomatic"] = false; break;
            case "legacy": json.Remove("SourceDesktopPath"); break;
            case "relative-source": json["SourceDesktopPath"] = "relative"; break;
            case "blank-source": json["SourceDesktopPath"] = " "; break;
            case "new-schema": json["SchemaVersion"] = 999; break;
            case "old-schema": json["SchemaVersion"] = 0; break;
            case "missing-schema": json.Remove("SchemaVersion"); break;
            case "missing-time": json.Remove("CreatedUtc"); break;
            case "missing-icons": json.Remove("Icons"); break;
            case "null-icons": json["Icons"] = null; break;
            case "null-icon": json["Icons"]![0] = null; break;
            case "missing-position": json["Icons"]![0]!.AsObject().Remove("X"); break;
            case "null-topology": json["Topology"] = null; break;
            case "missing-monitors": json["Topology"]!.AsObject().Remove("Monitors"); break;
            case "empty-monitors": json["Topology"]!["Monitors"] = new JsonArray(); break;
            case "missing-geometry": json["Topology"]!["Monitors"]![0]!.AsObject().Remove("Width"); break;
            case "null-monitor": json["Topology"]!["Monitors"]![0] = null; break;
            case "invalid-geometry": json["Topology"]!["Monitors"]![0]!["Width"] = -1; break;
            case "invalid-source": json["SourceDesktopPath"] = Path.Combine(Source, "wildcard*"); break;
        }
        string path = Path.Combine(_root, "layouts", stem + ".json");
        string contents = kind == "corrupt" ? "{broken" : json.ToJsonString();
        if (kind == "duplicate-fields") contents = contents.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":999,\"SchemaVersion\":1");
        File.WriteAllText(path, contents);

        Assert.NotEqual(path, store.SaveAutomaticIfChanged(Snapshot(), Source, Now));
        Assert.Equal(0, store.PruneAutomatic(7, Now, []));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(31)]
    [InlineData(int.MaxValue)]
    public void DisabledOrUnsupportedRetentionDeletesNothing(int days)
    {
        var store = Store;
        store.SaveAutomatic(Snapshot(100), Now.AddDays(-100));
        store.SaveAutomatic(Snapshot(), Now);
        Assert.Equal(0, store.PruneAutomatic(days, Now, []));
        Assert.Equal(2, store.List().Count);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(15)]
    [InlineData(30)]
    public void RetentionUsesCaptureUtcWithStrictBoundaryAndKeepsFutureCaptures(int days)
    {
        var store = Store;
        var old = Snapshot() with { CreatedUtc = Now.AddDays(-days).AddTicks(-1) };
        string expired = store.SaveAutomatic(old, Now); // File name and mtime must not determine age.
        string boundary = store.SaveAutomatic(Snapshot(days), Now.AddDays(-100));
        string future = store.SaveAutomatic(Snapshot(-1), Now.AddDays(-101));
        Assert.Equal(1, store.PruneAutomatic(days, Now.ToOffset(TimeSpan.FromHours(8)), []));
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(boundary));
        Assert.True(File.Exists(future));
        Assert.Equal(0, store.PruneAutomatic(days, Now, []));
    }

    [Fact]
    public void RetentionProtectsActiveFullPathsAndLatestForEverySourceAndFullTopology()
    {
        var store = Store;
        var baseline = Snapshot(100);
        string protectedPath = store.SaveAutomatic(baseline, baseline.CreatedUtc);
        string expired = store.SaveAutomatic(baseline with { CreatedUtc = Now.AddDays(-90) }, Now.AddDays(-90));
        string latest = store.SaveAutomatic(baseline with
        {
            CreatedUtc = Now.AddDays(-80),
            Topology = new(baseline.Topology.Monitors.Reverse().ToArray()),
        }, Now.AddDays(-80));
        var preserved = new List<string> { protectedPath, latest };
        var internalOnly = new MonitorTopology([baseline.Topology.Monitors[0]]);
        var renamed = new MonitorTopology([baseline.Topology.Monitors[0] with { DeviceName = "other" }, baseline.Topology.Monitors[1]]);
        var newPrimary = new MonitorTopology([baseline.Topology.Monitors[0] with { IsPrimary = false }, baseline.Topology.Monitors[1] with { IsPrimary = true }]);
        var resized = new MonitorTopology([baseline.Topology.Monitors[0], baseline.Topology.Monitors[1] with { Width = 1280 }]);
        var moved = new MonitorTopology([baseline.Topology.Monitors[0], baseline.Topology.Monitors[1] with { Y = 200 }]);
        foreach (var topology in new[] { internalOnly, renamed, newPrimary, resized, moved })
            preserved.Add(store.SaveAutomatic(baseline with { Topology = topology }, baseline.CreatedUtc));
        preserved.Add(store.SaveAutomatic(baseline with { SourceDesktopPath = Source + "-other" }, baseline.CreatedUtc));

        string activeAlias = Path.Combine(Path.GetDirectoryName(protectedPath)!, ".", Path.GetFileName(protectedPath)).ToUpperInvariant();
        Assert.Equal(1, store.PruneAutomatic(7, Now, [activeAlias]));
        Assert.False(File.Exists(expired));
        Assert.All(preserved, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void TiedLatestCapturesHaveOneDeterministicSurvivor()
    {
        var store = Store;
        string first = store.SaveAutomatic(Snapshot(100), Now);
        string second = store.SaveAutomatic(Snapshot(100), Now);
        Assert.Equal(1, store.PruneAutomatic(7, Now, []));
        Assert.NotEqual(File.Exists(first), File.Exists(second));
    }

    [Fact]
    public void MissingStoreDirectoryIsHarmlessForRetention()
    {
        var store = Store;
        Directory.Delete(Path.Combine(_root, "layouts"));
        Assert.Equal(0, store.PruneAutomatic(7, Now, []));
    }

    [Fact(Skip = "File symlink creation requires an unavailable Windows privilege. Store and ancestor junction tests run without elevation.")]
    public void FileSymlinkIsNeitherReusedNorDeletedAndTargetIsUntouched()
    {
        var store = Store;
        string outside = Path.Combine(_root, "outside.json");
        string original = JsonSerializer.Serialize(Snapshot(100));
        File.WriteAllText(outside, original);
        string link = Path.Combine(_root, "layouts", "auto-link.json");
        File.CreateSymbolicLink(link, outside);
        try
        {
            string capture = store.SaveAutomaticIfChanged(Snapshot(), Source, Now);
            Assert.NotEqual(link, capture);
            Assert.Equal(0, store.PruneAutomatic(7, Now, []));
            Assert.True(File.Exists(link));
            Assert.Equal(original, File.ReadAllText(outside));
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void RetentionDoesNotRecurseIntoSubdirectories()
    {
        var store = Store;
        var nested = new LayoutStore(Path.Combine(_root, "layouts", "nested"));
        string old = nested.SaveAutomatic(Snapshot(100), Now.AddDays(-100));
        nested.SaveAutomatic(Snapshot(), Now);
        Assert.Equal(0, store.PruneAutomatic(7, Now, []));
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void UnreadableCandidateAndInvalidProtectedPathFailClosed()
    {
        var store = Store;
        string old = store.SaveAutomatic(Snapshot(100), Now.AddDays(-100));
        string latest = store.SaveAutomatic(Snapshot(), Now);
        Assert.Equal(0, store.PruneAutomatic(7, Now, ["not-an-absolute-path"]));
        using (var locked = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(latest, store.SaveAutomaticIfChanged(Snapshot(), Source, Now));
            Assert.Equal(0, store.PruneAutomatic(7, Now, []));
        }
        Assert.True(File.Exists(old));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetentionDoesNotTraverseStoreOrAncestorJunctions(bool ancestor)
    {
        string outside = Path.Combine(_root, "outside");
        string actual = ancestor ? Path.Combine(outside, "nested") : outside;
        var original = new LayoutStore(actual);
        original.SaveAutomatic(Snapshot(100), Now.AddDays(-100));
        original.SaveAutomatic(Snapshot(), Now);
        string junction = Path.Combine(_root, "junction");
        CreateJunction(junction, outside);
        var redirected = new LayoutStore(ancestor ? Path.Combine(junction, "nested") : junction);
        Assert.Equal(0, redirected.PruneAutomatic(7, Now, []));
        Assert.Equal(2, original.List().Count);
        Assert.Throws<IOException>(() => redirected.SaveAutomaticIfChanged(Snapshot(), Source, Now));
    }

    private void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(target);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        _junctions.Add(link);
    }

    public void Dispose()
    {
        // Only links and data under this test's unique temporary root can be removed.
        string root = Path.GetFullPath(_root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), root, StringComparison.OrdinalIgnoreCase);
        foreach (string junction in _junctions)
        {
            Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(junction), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(junction);
        }
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
