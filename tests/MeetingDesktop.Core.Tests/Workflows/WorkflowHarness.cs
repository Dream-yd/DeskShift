using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.Core.Tests.Workflows;

/// <summary>
/// Records every machine-touching call a workflow makes, in order.
/// </summary>
/// <remarks>
/// The step order is the safety mechanism - which steps may run before the UAC
/// prompt, what gets unwound when a later one fails - and none of it is visible
/// from the outside. Recording the calls is how it becomes assertable, and the
/// alternative is a test that kills the shell it is running in.
/// </remarks>
internal sealed class RecordingDesktopOperations : IDesktopOperations
{
    public Action<string>? OnCall { get; set; }
    public PlaceResult? Placement { get; set; }
    public List<(string Call, CancellationToken Token)> Tokens { get; } = [];
    public PlaceResult? ShellPlacement { get; set; }
    public Action<string, IReadOnlyList<IconPosition>?, CancellationToken>? OnWaitForCompatibleDesktop { get; set; }
    public List<(string Path, IReadOnlyList<IconPosition>? Expected)> ReadinessRequests { get; } = [];
    public void WaitForCompatibleDesktop(string targetPath, IReadOnlyList<IconPosition>? expectedIcons, CancellationToken cancellationToken)
    {
        Tokens.Add((nameof(WaitForCompatibleDesktop), cancellationToken));
        ReadinessRequests.Add((targetPath, expectedIcons));
        Record(nameof(WaitForCompatibleDesktop));
        OnWaitForCompatibleDesktop?.Invoke(targetPath, expectedIcons, cancellationToken);
    }
    public IReadOnlyList<IconPosition> ReadShellIcons(CancellationToken cancellationToken)
    {
        Tokens.Add((nameof(ReadShellIcons), cancellationToken));
        Record(nameof(ReadShellIcons));
        return [new("Alpha", 0, 0, "C:\\Original\\Alpha"), new("Beta", 75, 0, "C:\\Original\\Beta")];
    }
    public void SwitchLiveDesktop(string desktopPath, CancellationToken cancellationToken)
    {
        Tokens.Add((nameof(SwitchLiveDesktop), cancellationToken));
        _writtenDesktopPaths.Add(desktopPath);
        Record(nameof(SwitchLiveDesktop));
    }
    public PlaceResult PlaceShellIcons(IReadOnlyList<IconPosition> icons, CancellationToken cancellationToken)
    {
        Tokens.Add((nameof(PlaceShellIcons), cancellationToken));
        Record(nameof(PlaceShellIcons));
        return ShellPlacement ?? new PlaceResult(icons.Count, icons.Count, [], [], false, true);
    }

    private readonly List<string> _calls = [];
    private readonly List<WallpaperState> _applied = [];
    private readonly List<string> _writtenDesktopPaths = [];
    private readonly HashSet<string> _throwsOn = [];

    /// <summary>Every call, in the order it was made.</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>The wallpapers passed to <see cref="ApplyWallpaper"/>, in order.</summary>
    public IReadOnlyList<WallpaperState> AppliedWallpapers => _applied;

    /// <summary>Every path passed to <see cref="WriteDesktopPath"/>, in order.</summary>
    public IReadOnlyList<string> WrittenDesktopPaths => _writtenDesktopPaths;

    /// <summary>What <see cref="ReadWallpaper"/> reports.</summary>
    public WallpaperState Wallpaper { get; set; } = WallpaperState.Empty;

    /// <summary>What <see cref="ApplyWallpaper"/> reports. False means it did not take.</summary>
    public bool ApplyWallpaperSucceeds { get; set; } = true;
    public Func<WallpaperState, bool>? ApplyWallpaperResultFor { get; set; }

    /// <summary>What <see cref="ReadIconLayoutBlob"/> reports.</summary>
    public byte[]? IconLayoutBlob { get; set; }

    /// <summary>What <see cref="IsExplorerRunning"/> reports.</summary>
    public bool ExplorerRunning { get; set; } = true;

    /// <summary>Makes the named call throw, standing in for a step that failed.</summary>
    public void ThrowOn(string call) => _throwsOn.Add(call);

    /// <summary>The index of a call in <see cref="Calls"/>, or -1.</summary>
    public int IndexOf(string call) => _calls.IndexOf(call);

    private void Record(string call)
    {
        lock (_calls) _calls.Add(call);
        OnCall?.Invoke(call);

        if (_throwsOn.Contains(call))
        {
            throw new IOException($"'{call}' was made to fail by the test.");
        }
    }

    public IReadOnlyList<IconPosition> ReadIcons(CancellationToken cancellationToken)
    {
        Tokens.Add((nameof(ReadIcons), cancellationToken));
        Record(nameof(ReadIcons));
        return [new IconPosition("Alpha", 0, 0), new IconPosition("Beta", 75, 0)];
    }

    public PlaceResult PlaceIcons(IReadOnlyList<IconPosition> icons, CancellationToken cancellationToken)
    {
        Tokens.Add((nameof(PlaceIcons), cancellationToken));
        Record(nameof(PlaceIcons));
        return Placement ?? new PlaceResult(icons.Count, icons.Count, [], [], true, true);
    }

    public string? CapturePreview(string destinationPath)
    {
        Record(nameof(CapturePreview));
        File.WriteAllBytes(destinationPath, [0x89, 0x50, 0x4E, 0x47]);
        return destinationPath;
    }

    public WallpaperState ReadWallpaper()
    {
        Record(nameof(ReadWallpaper));
        return Wallpaper;
    }

    public bool ApplyWallpaper(WallpaperState state)
    {
        Record(nameof(ApplyWallpaper));
        _applied.Add(state);
        return ApplyWallpaperResultFor?.Invoke(state) ?? ApplyWallpaperSucceeds;
    }

    public void WriteDesktopPath(string desktopPath)
    {
        Record(nameof(WriteDesktopPath));
        _writtenDesktopPaths.Add(desktopPath);
    }

    public byte[]? ReadIconLayoutBlob()
    {
        Record(nameof(ReadIconLayoutBlob));
        return IconLayoutBlob;
    }

    public bool ExportIconLayoutBlob(byte[] blob, string destinationPath)
    {
        Record(nameof(ExportIconLayoutBlob));
        File.WriteAllBytes(destinationPath, blob);
        return true;
    }

    public byte[]? ImportIconLayoutBlob(string sourcePath)
    {
        Record(nameof(ImportIconLayoutBlob));
        return File.Exists(sourcePath) ? File.ReadAllBytes(sourcePath) : null;
    }

    public bool WriteIconLayoutBlob(byte[] blob)
    {
        Record(nameof(WriteIconLayoutBlob));
        return true;
    }

    public bool IsExplorerRunning()
    {
        Record(nameof(IsExplorerRunning));
        return ExplorerRunning;
    }

    public void StartExplorer() => Record(nameof(StartExplorer));

    public void StopExplorer() => Record(nameof(StopExplorer));

    public void RestartExplorer() => Record(nameof(RestartExplorer));
}

/// <summary>An elevation broker that never raises a prompt.</summary>
internal sealed class FakeElevationBroker : IElevationBroker
{
    private readonly List<string> _requests = [];
    public ElevatedOperationReport? LastReport { get; set; }
    public bool AutoReport { get; set; } = true;

    /// <summary>Every token this broker was asked to run, in order.</summary>
    public IReadOnlyList<string> Requests => _requests;

    /// <summary>What every request returns, unless <see cref="OutcomeFor"/> says otherwise.</summary>
    public ElevationOutcome Outcome { get; set; } = ElevationOutcome.Succeeded;

    /// <summary>Per-request outcome, for a test that needs the second one to differ.</summary>
    public Func<string, ElevationOutcome>? OutcomeFor { get; set; }

    /// <summary>
    /// Runs as the broker is invoked, standing in for "what is on disk at the
    /// moment the prompt is raised". Throwing here simulates a broker that
    /// could not launch the elevated process at all.
    /// </summary>
    public Action<string>? OnRun { get; set; }

    public ElevationOutcome Run(string argument)
    {
        _requests.Add(argument);
        ElevatedOperationReport? previous = LastReport;
        OnRun?.Invoke(argument);
        ElevationOutcome outcome = OutcomeFor?.Invoke(argument) ?? Outcome;
        if (AutoReport && outcome == ElevationOutcome.Succeeded && ReferenceEquals(previous, LastReport))
            LastReport = previous is null ? new(true, new(0, [])) : previous with { };
        return outcome;
    }
}

/// <summary>
/// A desktop location that resolves to a folder the test controls, instead of
/// whatever the machine running the test happens to have.
/// </summary>
internal sealed class FakeDesktopLocationService : DesktopLocationService
{
    public FakeDesktopLocationService(string resolvedPath)
        : base(new DesktopPathService()) => ResolvedPath = resolvedPath;

    public string ResolvedPath { get; set; }

    public override DesktopLocation Detect(string? meetingDesktopDirectory = null) =>
        new(
            DesktopLocationStatus.Agreed,
            ResolvedPath,
            [new DesktopPathCandidate(DesktopPathSource.KnownFolder, ResolvedPath, ResolvedPath, true)],
            false);
}

/// <summary>A scratch data directory, two workflows, and everything they were wired to.</summary>
internal sealed class WorkflowHarness : IDisposable
{
    private WorkflowHarness(string root)
    {
        Root = root;
        Paths = AppPaths.Resolve(["--data-dir", root]);
        Paths.EnsureCreated();

        OriginalDesktop = Directory.CreateDirectory(Path.Combine(root, "RealDesktop")).FullName;
        MeetingDesktop = Directory.CreateDirectory(Path.Combine(root, "MeetingDesktop")).FullName;

        Desktop = new RecordingDesktopOperations();
        Elevation = new FakeElevationBroker();
        Sessions = new SessionStore(Paths.SessionFilePath);
        Locations = new FakeDesktopLocationService(OriginalDesktop);
        Layouts = new LayoutStore(Paths.LayoutsDirectory);
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public string OriginalDesktop { get; }

    public string MeetingDesktop { get; }

    public RecordingDesktopOperations Desktop { get; }

    public FakeElevationBroker Elevation { get; }

    public SessionStore Sessions { get; }

    public FakeDesktopLocationService Locations { get; }

    public LayoutStore Layouts { get; }

    public static WorkflowHarness Create() =>
        new(Path.Combine(Path.GetTempPath(), "MeetingDesktopTests", Guid.NewGuid().ToString("N")));

    public PrepareWorkflow PrepareWorkflow() =>
        new(Paths, Layouts, Sessions, Locations, Elevation, Desktop);

    public RestoreWorkflow RestoreWorkflow() =>
        new(Paths, Layouts, Sessions, Elevation, Desktop);

    /// <summary>Writes a real PNG into the scratch directory and returns its path.</summary>
    public string CreateMeetingWallpaper()
    {
        string path = Path.Combine(Root, "meeting-wallpaper.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    /// <summary>Saves a real layout snapshot containing two icons, and returns its path.</summary>
    public string CreateLayoutSnapshot() =>
        Layouts.SaveAutomatic(
            new LayoutSnapshot
            {
                CreatedUtc = DateTimeOffset.UnixEpoch,
                IsAutomatic = true,
                Topology = MonitorTopologyReader.Capture(),
                Icons = [new IconPosition("Alpha", 0, 0), new IconPosition("Beta", 75, 0)],
            },
            DateTimeOffset.Now);

    /// <summary>Records a session pointing at the scratch folders.</summary>
    public SessionInfo SaveSession(bool wallpaperChanged = false, string? snapshotPath = null)
    {
        var session = new SessionInfo
        {
            OriginalDesktopPath = OriginalDesktop,
            MeetingDesktopPathAtSwap = MeetingDesktop,
            SwappedUtc = DateTimeOffset.Now,
            AutoLayoutSnapshotPath = snapshotPath,
            // No snapshot represents an adopted legacy session, not a lost capture.
            CapturedIconCount = snapshotPath is null ? 0 : 2,
            TopologyAtSwap = MonitorTopologyReader.Capture(),
            WallpaperAtSwap = new WallpaperState(@"C:\before.png", "10", "0"),
            WallpaperChanged = wallpaperChanged,
        };

        Sessions.Save(session);
        return session;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
