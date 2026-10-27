using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class LayoutRecoverySafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LayoutRecoverySafety", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("{\"Icons\":null}")]
    [InlineData("{\"Topology\":null}")]
    [InlineData("{\"Topology\":{\"Monitors\":null}}")]
    [InlineData("{\"Topology\":{\"Monitors\":[null]}}")]
    [InlineData("{\"Icons\":[null]}")]
    public void MalformedSnapshotDoesNotPreventHealthySnapshotLoading(string json)
    {
        var store = new LayoutStore(root);
        store.Save(new LayoutSnapshot { Name = "healthy" }, "healthy");
        File.WriteAllText(Path.Combine(root, "broken.json"), json);
        Assert.Null(store.Load("broken"));
        Assert.Equal("healthy", store.Load("healthy")?.Name);
        Assert.Equal(2, store.List().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletePreservesSnapshotWhenSessionReferencesItOrIsUnreadable(bool corrupt)
    {
        string layouts = Path.Combine(root, "Layouts");
        var store = new LayoutStore(layouts);
        string snapshot = store.Save(new LayoutSnapshot(), "recovery");
        string state = Path.Combine(root, "State");
        Directory.CreateDirectory(state);
        string session = Path.Combine(state, "session.json");
        File.WriteAllText(session, corrupt ? "{bad" : System.Text.Json.JsonSerializer.Serialize(
            new MeetingDesktop.Core.Models.SessionInfo { OriginalDesktopPath = root, MeetingDesktopPathAtSwap = root, AutoLayoutSnapshotPath = snapshot }));

        Assert.False(store.Delete("recovery"));
        Assert.True(File.Exists(snapshot));
        Assert.True(File.Exists(session));
    }
    [Theory]
    [InlineData("{\"SchemaVersion\":0,\"OriginalDesktopPath\":\"x\",\"MeetingDesktopPathAtSwap\":\"y\"}")]
    [InlineData("{\"OriginalDesktopPath\":null,\"MeetingDesktopPathAtSwap\":\"y\"}")]
    [InlineData("{\"OriginalDesktopPath\":\"x\",\"MeetingDesktopPathAtSwap\":\"y\",\"CapturedIconCount\":-1}")]
    public void StructurallyInvalidSessionFailsClosed(string json)
    {
        var layouts = new LayoutStore(Path.Combine(root, "Layouts"));
        string snapshot = layouts.Save(new LayoutSnapshot(), "recovery");
        string state = Path.Combine(root, "State");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "session.json"), json);
        Assert.Equal(LayoutDeleteResult.SessionUnreadable, layouts.DeleteWithReason("recovery"));
        Assert.True(File.Exists(snapshot));
    }

    [Fact]
    public void UnreferencedSnapshotCanBeDeletedWithActiveSession()
    {
        var store = new LayoutStore(Path.Combine(root, "Layouts"));
        string protectedPath = store.Save(new LayoutSnapshot(), "recovery");
        string other = store.Save(new LayoutSnapshot(), "other");
        var sessions = new MeetingDesktop.Core.Workflows.SessionStore(Path.Combine(root, "State", "session.json"));
        sessions.Save(new SessionInfo { OriginalDesktopPath = root, MeetingDesktopPathAtSwap = root, AutoLayoutSnapshotPath = protectedPath });
        Assert.Equal(LayoutDeleteResult.Deleted, store.DeleteWithReason("other"));
        Assert.False(File.Exists(other));
        Assert.True(File.Exists(protectedPath));
    }

    [Fact]
    public void LockedSessionFailsClosed()
    {
        var store = new LayoutStore(Path.Combine(root, "Layouts"));
        string snapshot = store.Save(new LayoutSnapshot(), "recovery");
        string state = Path.Combine(root, "State");
        Directory.CreateDirectory(state);
        string session = Path.Combine(state, "session.json");
        File.WriteAllText(session, "{}");
        using var locked = new FileStream(session, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(LayoutDeleteResult.SessionUnreadable, store.DeleteWithReason("recovery"));
        Assert.True(File.Exists(snapshot));
    }

    [Fact]
    public void SessionCannotBeSavedReferencingSnapshotDeletedBeforeSave()
    {
        var store = new LayoutStore(Path.Combine(root, "Layouts"));
        string snapshot = store.Save(new LayoutSnapshot(), "recovery");
        Assert.True(store.Delete("recovery"));
        var sessions = new MeetingDesktop.Core.Workflows.SessionStore(Path.Combine(root, "State", "session.json"));
        Assert.Throws<IOException>(() => sessions.Save(new SessionInfo
        {
            OriginalDesktopPath = root, MeetingDesktopPathAtSwap = root, AutoLayoutSnapshotPath = snapshot,
        }));
        Assert.False(File.Exists(sessions.FilePath));
    }

    [Fact]
    public void NewSessionCannotReferenceSnapshotMissingWithoutPriorDeletionInThisProcess()
    {
        string snapshot = Path.Combine(root, "Layouts", "missing.json");
        var sessions = new MeetingDesktop.Core.Workflows.SessionStore(Path.Combine(root, "State", "session.json"));
        Assert.Throws<IOException>(() => sessions.Save(new SessionInfo
        {
            OriginalDesktopPath = root, MeetingDesktopPathAtSwap = root, AutoLayoutSnapshotPath = snapshot,
        }));
        Assert.False(sessions.Exists);
    }

    [Fact]
    public void ExistingRecoveryRecordCanBeUpdatedAfterSnapshotIsLost()
    {
        var layouts = new LayoutStore(Path.Combine(root, "Layouts"));
        string snapshot = layouts.Save(new LayoutSnapshot(), "recovery");
        var sessions = new MeetingDesktop.Core.Workflows.SessionStore(Path.Combine(root, "State", "session.json"));
        var info = new SessionInfo { OriginalDesktopPath = root, MeetingDesktopPathAtSwap = root, AutoLayoutSnapshotPath = snapshot };
        sessions.Save(info);
        File.Delete(snapshot);
        sessions.Save(info with { CapturedIconCount = 5 });
        Assert.Equal(5, sessions.Load()!.CapturedIconCount);
        Assert.Equal(snapshot, sessions.Load()!.AutoLayoutSnapshotPath);
    }

    [Fact]
    public void FailedSessionWriteDoesNotForgetDeletedSnapshot()
    {
        var layouts = new LayoutStore(Path.Combine(root, "Layouts"));
        string snapshot = layouts.Save(new LayoutSnapshot(), "recovery");
        Assert.True(layouts.Delete("recovery"));
        layouts.Save(new LayoutSnapshot(), "recovery");
        string state = Path.Combine(root, "State");
        Directory.CreateDirectory(Path.Combine(state, "session.json.tmp"));
        var sessions = new MeetingDesktop.Core.Workflows.SessionStore(Path.Combine(state, "session.json"));
        var info = new SessionInfo { OriginalDesktopPath = root, MeetingDesktopPathAtSwap = root, AutoLayoutSnapshotPath = snapshot };
        Assert.Throws<UnauthorizedAccessException>(() => sessions.Save(info));
        Directory.Delete(Path.Combine(state, "session.json.tmp"));
        File.Delete(snapshot);
        Assert.Throws<IOException>(() => sessions.Save(info));
        Assert.False(sessions.Exists);
    }

    [Fact]
    public async Task SessionChangedBeforeDeleteAcquiresLockProtectsNewReference()
    {
        var layouts = new LayoutStore(Path.Combine(root, "Layouts"));
        string first = layouts.Save(new LayoutSnapshot(), "first");
        string target = layouts.Save(new LayoutSnapshot(), "target");
        var sessions = new MeetingDesktop.Core.Workflows.SessionStore(Path.Combine(root, "State", "session.json"));
        var info = new SessionInfo { OriginalDesktopPath = root, MeetingDesktopPathAtSwap = root, AutoLayoutSnapshotPath = first };
        sessions.Save(info);
        Task<LayoutDeleteResult> deletion;
        using var started = new ManualResetEventSlim();
        lock (MeetingDesktop.Core.Workflows.SessionStore.SnapshotSync)
        {
            deletion = Task.Run(() => { started.Set(); return layouts.DeleteWithReason("target"); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            sessions.Save(info with { AutoLayoutSnapshotPath = target });
        }
        Assert.Equal(LayoutDeleteResult.SessionReferenced, await deletion);
        Assert.True(File.Exists(target));
        Assert.Equal(target, sessions.Load()!.AutoLayoutSnapshotPath);
    }

    [Fact]
    public async Task ActualSessionSaveRacingDeletionNeverLeavesDanglingReference()
    {
        var layouts = new LayoutStore(Path.Combine(root, "Layouts"));
        var sessions = new MeetingDesktop.Core.Workflows.SessionStore(Path.Combine(root, "State", "session.json"));
        for (int i = 0; i < 40; i++)
        {
            sessions.Clear();
            string name = "recovery" + i;
            string snapshot = layouts.Save(new LayoutSnapshot(), name);
            var info = new SessionInfo { OriginalDesktopPath = root, MeetingDesktopPathAtSwap = root, AutoLayoutSnapshotPath = snapshot };
            using var start = new ManualResetEventSlim();
            Task<bool> save = Task.Run(() => { start.Wait(); try { sessions.Save(info); return true; } catch (IOException) { return false; } });
            Task<LayoutDeleteResult> delete = Task.Run(() => { start.Wait(); return layouts.DeleteWithReason(name); });
            start.Set();
            bool saved = await save;
            LayoutDeleteResult result = await delete;
            Assert.True(saved ? result == LayoutDeleteResult.SessionReferenced && File.Exists(snapshot) :
                result == LayoutDeleteResult.Deleted && !sessions.Exists);
        }
    }

    [Fact]
    public void SnapshotWithMissingIconNameIsIsolatedFromHealthySnapshot()
    {
        var layouts = new LayoutStore(root);
        layouts.Save(new LayoutSnapshot { Name = "healthy" }, "healthy");
        File.WriteAllText(Path.Combine(root, "broken.json"), "{\"Icons\":[{\"Name\":null,\"X\":0,\"Y\":0}]}" );
        Assert.Null(layouts.Load("broken"));
        Assert.NotNull(layouts.Load("healthy"));
        Assert.Equal(2, layouts.List().Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

