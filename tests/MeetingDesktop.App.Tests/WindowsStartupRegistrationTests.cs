using MeetingDesktop.App.Services;
using Microsoft.Win32;
using System.IO;

namespace MeetingDesktop.App.Tests;

public sealed class WindowsStartupRegistrationTests
{
    [Fact]
    public void StartupRegistrationHasAnInjectableStoreContract()
    {
        Assert.NotNull(typeof(DesktopQuickAccess).Assembly.GetType("MeetingDesktop.App.Services.WindowsStartupRegistration"));
        Assert.NotNull(typeof(DesktopQuickAccess).Assembly.GetType("MeetingDesktop.App.Services.IStartupRegistrationStore"));
    }

    private const string Executable = @"C:\Program Files\Meeting Desktop\MeetingDesktop.exe";
    private static readonly string DataDirectory = Path.Combine(Path.GetTempPath(), "MeetingDesktopStartupTests", Guid.NewGuid().ToString("N"), "Meeting data");

    [Fact]
    public void CommandQuotesBothPathsAndStartsInTray() => Assert.Equal(
        "\"C:\\Program Files\\Meeting Desktop\\MeetingDesktop.exe\" --data-dir \"C:\\Users\\Test\\Meeting data\" --start-in-tray",
        WindowsStartupRegistration.BuildCommand(Executable, @"C:\Users\Test\Meeting data"));

    [Theory]
    [InlineData(@"C:\data\", "\"C:\\data\\\\\"")]
    [InlineData(@"C:\", "\"C:\\\\\"")]
    [InlineData(@"\\server\share\数据\", "\"\\\\server\\share\\数据\\\\\"")]
    public void TrailingBackslashesAreEscapedBeforeClosingQuote(string data, string quoted) =>
        Assert.Equal($"\"{Executable}\" --data-dir {quoted} --start-in-tray", WindowsStartupRegistration.BuildCommand(Executable, data));

    [Theory]
    [InlineData("MeetingDesktop.exe")]
    [InlineData(@"C:MeetingDesktop.exe")]
    [InlineData(@"\MeetingDesktop.exe")]
    [InlineData(@"C:\app.cmd")]
    [InlineData("C:\\bad\"name.exe")]
    [InlineData("C:\\bad\0name.exe")]
    [InlineData("")]
    public void UnsafeExecutableIsRejected(string executable) =>
        Assert.Throws<ArgumentException>(() => WindowsStartupRegistration.BuildCommand(executable, DataDirectory));

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("C:\\data\"injected")]
    [InlineData("C:\\data\0injected")]
    public void UnsafeDataPathIsRejected(string data) =>
        Assert.Throws<ArgumentException>(() => WindowsStartupRegistration.BuildCommand(Executable, data));

    [Fact]
    public void ConstructorIsReadOnlyAndEnableUpdateDisableAreIdempotent()
    {
        var store = new FakeStore();
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Mutations);
        Assert.Null(service.ReadActual());
        Assert.False(service.IsEnabled());
        service.SetEnabled(false);
        Assert.Equal(0, store.Mutations);
        service.SetEnabled(true);
        Assert.True(service.IsEnabled());
        Assert.Equal(WindowsStartupRegistration.BuildCommand(Executable, DataDirectory), service.ReadActual()?.Value);
        service.SetEnabled(true);
        Assert.Equal(1, store.Mutations);
        service.SetEnabled(false);
        service.SetEnabled(false);
        Assert.False(service.IsEnabled());
        Assert.Equal(2, store.Mutations);
        Assert.Equal("unrelated payload", store.Values["OtherApp"].Value);
    }

    [Fact]
    public void DisableRemovesRecognisedPreviousVersionAndRollsBackIfSavingFails()
    {
        const string oldExe = @"C:\OldRelease\MeetingDesktop.exe";
        var previous = new StartupRegistrationValue(WindowsStartupRegistration.BuildCommand(oldExe, DataDirectory), RegistryValueKind.String);
        var store = new FakeStore { Current = previous };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store, _ => true);
        Assert.False(service.IsEnabled());
        Assert.Throws<IOException>(() => service.SetEnabled(false, () => throw new IOException("save failed")));
        Assert.Equal(previous, store.Current);
        service.SetEnabled(false);
        Assert.Null(store.Current);
    }

    [Fact]
    public void DisablePreservesARecognisedExecutableWithDifferentDataDirectory()
    {
        var previous = new StartupRegistrationValue(
            WindowsStartupRegistration.BuildCommand(@"C:\OldRelease\MeetingDesktop.exe", @"C:\DifferentData"), RegistryValueKind.String);
        var store = new FakeStore { Current = previous };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store, _ => true);
        Assert.Throws<IOException>(() => service.SetEnabled(false));
        Assert.Equal(previous, store.Current);
    }

    [Fact]
    public void MigratesOnlyAnOwnedPreviousVersionAndRestoresOnFailure()
    {
        var previous = new StartupRegistrationValue(
            WindowsStartupRegistration.BuildCommand(@"C:\OldRelease\MeetingDesktop.exe", DataDirectory), RegistryValueKind.String);
        var store = new FakeStore { Current = previous, FailAfterMutation = true };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store, _ => true);
        Assert.Throws<IOException>(() => service.TryMigratePreviousVersion());
        Assert.Equal(previous, store.Current);
        Assert.True(service.TryMigratePreviousVersion());
        Assert.True(service.IsEnabled());
        Assert.False(service.TryMigratePreviousVersion());
        store.Current = new StartupRegistrationValue("unrecognised", RegistryValueKind.String);
        int mutations = store.Mutations;
        Assert.False(service.TryMigratePreviousVersion());
        Assert.Equal("unrecognised", store.Current.Value);
        Assert.Equal(mutations, store.Mutations);
    }

    [Fact]
    public void NeverMigratesAnUnverifiedPreviousExecutable()
    {
        var previous = new StartupRegistrationValue(
            WindowsStartupRegistration.BuildCommand(@"C:\Untrusted\MeetingDesktop.exe", DataDirectory), RegistryValueKind.String);
        var store = new FakeStore { Current = previous };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store, _ => false);
        Assert.False(service.TryMigratePreviousVersion());
        Assert.Throws<IOException>(() => service.SetEnabled(false));
        Assert.Equal(previous, store.Current);
        Assert.Equal(0, store.Mutations);
    }

    [Fact]
    public void OffersMigrationOnlyForARecognisedOlderEntryWithoutChangingIt()
    {
        var previous = new StartupRegistrationValue(
            WindowsStartupRegistration.BuildCommand(@"C:\OldRelease\MeetingDesktop.exe", DataDirectory), RegistryValueKind.String);
        var store = new FakeStore { Current = previous };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store, _ => true);
        Assert.True(service.CanMigratePreviousVersion());
        Assert.Equal(previous, store.Current);
        Assert.Equal(0, store.Mutations);
        store.Current = new StartupRegistrationValue("unknown", RegistryValueKind.String);
        Assert.False(service.CanMigratePreviousVersion());
        Assert.Equal(0, store.Mutations);
    }

    [Theory]
    [InlineData("wrong executable")]
    [InlineData("\"C:\\other.exe\" --start-in-tray")]
    public void DisablePreservesUnrecognizedPayloadAndEnableExplicitlyUpdatesIt(string payload)
    {
        var store = new FakeStore { Current = new(payload, RegistryValueKind.ExpandString) };
        var original = store.Current;
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        Assert.False(service.IsEnabled());
        Assert.Throws<IOException>(() => service.SetEnabled(false));
        Assert.Equal(original, store.Current);
        Assert.Equal(0, store.Mutations);
        service.SetEnabled(true);
        Assert.True(service.IsEnabled());
        Assert.Equal(RegistryValueKind.String, store.Current?.Kind);
        Assert.Equal("unrelated payload", store.Values["OtherApp"].Value);
    }

    [Fact]
    public void EnabledRequiresExactOrdinalCommandAndStringKind()
    {
        string command = WindowsStartupRegistration.BuildCommand(Executable, DataDirectory);
        var store = new FakeStore();
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        foreach (var value in new[]
        {
            new StartupRegistrationValue(command.ToUpperInvariant(), RegistryValueKind.String),
            new StartupRegistrationValue(command + " --extra", RegistryValueKind.String),
            new StartupRegistrationValue(command, RegistryValueKind.ExpandString),
            new StartupRegistrationValue(42, RegistryValueKind.DWord),
        })
        {
            store.Current = value;
            Assert.False(service.IsEnabled());
            Assert.Throws<IOException>(() => service.SetEnabled(false));
            Assert.Equal(value, store.Current);
        }
        store.Current = new(command, RegistryValueKind.String);
        Assert.True(service.IsEnabled());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedEnableRestoresAbsentOrExistingWrongPayload(bool existing)
    {
        var original = existing ? new StartupRegistrationValue(new byte[] { 1, 2, 3 }, RegistryValueKind.Binary) : null;
        var store = new FakeStore { Current = original, FailAfterMutation = true };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        Assert.Throws<IOException>(() => service.SetEnabled(true));
        Assert.Equal(original, store.Current);
        Assert.Equal("unrelated payload", store.Values["OtherApp"].Value);
    }

    [Fact]
    public void FailedDisableRestoresExactPreviousCommand()
    {
        var original = new StartupRegistrationValue(WindowsStartupRegistration.BuildCommand(Executable, DataDirectory), RegistryValueKind.String);
        var store = new FakeStore { Current = original, FailAfterMutation = true };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        Assert.Throws<IOException>(() => service.SetEnabled(false));
        Assert.Equal(original, store.Current);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void SilentWriteFailureIsDetectedAndPreviousValueRestored()
    {
        var original = new StartupRegistrationValue("old", RegistryValueKind.ExpandString);
        var store = new FakeStore { Current = original, IgnoreNextMutation = true };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        Assert.Throws<IOException>(() => service.SetEnabled(true));
        Assert.Equal(original, store.Current);
    }

    [Fact]
    public void RollbackFailureReportsBothErrorsWithoutRetryingForever()
    {
        var store = new FakeStore { AlwaysFail = true };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        var failure = Assert.Throws<AggregateException>(() => service.SetEnabled(true));
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(2, store.Mutations);
    }

    [Fact]
    public void SilentRollbackFailureIsReportedInsteadOfClaimingRestoration()
    {
        var store = new FakeStore { FailAfterMutation = true, IgnoreRollback = true };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        var failure = Assert.Throws<AggregateException>(() => service.SetEnabled(true));
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(2, store.Mutations);
    }

    [Fact]
    public void FailingSettingsPersistenceRestoresThePreviousMismatchedPayloadAndKind()
    {
        var previous = new StartupRegistrationValue(@"%LOCALAPPDATA%\previous.exe --old", RegistryValueKind.ExpandString);
        var store = new FakeStore { Current = previous };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        var saveFailure = new IOException("settings.json save failed");
        Assert.Same(saveFailure, Assert.Throws<IOException>(() => service.SetEnabled(true, () => throw saveFailure)));
        Assert.Equal(previous, store.Current);
        Assert.Equal(2, store.Mutations);
        Assert.Equal("unrelated payload", store.Values["OtherApp"].Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PersistRunsOnceAfterNativeChangeHasBeenVerified(bool enabled)
    {
        var store = new FakeStore();
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        if (!enabled) service.SetEnabled(true);
        int calls = 0;
        service.SetEnabled(enabled, () =>
        {
            calls++;
            Assert.Equal(enabled, service.IsEnabled());
            Assert.True(store.Reads >= 2);
        });
        Assert.Equal(1, calls);
        Assert.Equal(enabled, service.IsEnabled());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailingPersistenceRestoresAbsenceOrExactCommand(bool enabled)
    {
        var store = new FakeStore();
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        if (!enabled) service.SetEnabled(true);
        var previous = store.Current;
        var failure = new IOException("save failed");
        Assert.Same(failure, Assert.Throws<IOException>(() => service.SetEnabled(enabled, () => throw failure)));
        Assert.Equal(previous, store.Current);
    }

    [Theory]
    [InlineData(RegistryValueKind.Binary)]
    [InlineData(RegistryValueKind.MultiString)]
    [InlineData(RegistryValueKind.DWord)]
    public void FailingPersistenceRestoresNonStringPayloadAndKind(RegistryValueKind kind)
    {
        object payload = kind switch
        {
            RegistryValueKind.Binary => new byte[] { 2, 4, 6 },
            RegistryValueKind.MultiString => new[] { "prior", "payload" },
            _ => 123,
        };
        var previous = new StartupRegistrationValue(payload, kind);
        var store = new FakeStore { Current = previous };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        Assert.Throws<IOException>(() => service.SetEnabled(true, () => throw new IOException("save failed")));
        Assert.Equal(previous, store.Current);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoNativeChangeStillPersistsButDoesNotRollbackOverConcurrentValue(bool enabled)
    {
        var store = new FakeStore();
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        if (enabled) service.SetEnabled(true);
        int mutations = store.Mutations, calls = 0;
        service.SetEnabled(enabled, () => calls++);
        Assert.Equal(1, calls);
        var concurrent = new StartupRegistrationValue("another writer", RegistryValueKind.String);
        var failure = new IOException("save failed");
        Assert.Same(failure, Assert.Throws<IOException>(() => service.SetEnabled(enabled, () =>
        {
            store.Current = concurrent;
            throw failure;
        })));
        Assert.Equal(concurrent, store.Current);
        Assert.Equal(mutations, store.Mutations);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ConcurrentReplacementAfterOurWriteIsPreservedAndReportedAsUncertain(bool enabled, bool delete)
    {
        var store = new FakeStore { Current = new("previous", RegistryValueKind.ExpandString) };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        if (!enabled) service.SetEnabled(true);
        int mutations = store.Mutations;
        var concurrent = delete && enabled ? null : new StartupRegistrationValue("concurrent payload", RegistryValueKind.ExpandString);
        var saveFailure = new IOException("save failed");
        var failure = Assert.Throws<AggregateException>(() => service.SetEnabled(enabled, () =>
        {
            store.Current = concurrent;
            throw saveFailure;
        }));
        Assert.Same(saveFailure, failure.InnerExceptions[0]);
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(concurrent, store.Current);
        Assert.Equal(mutations + 1, store.Mutations);
    }

    [Fact]
    public void FailedPersistenceReportsRollbackWriteFailure()
    {
        var store = new FakeStore();
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        var saveFailure = new IOException("save failed");
        var failure = Assert.Throws<AggregateException>(() => service.SetEnabled(true, () =>
        {
            store.FailAfterMutation = true;
            throw saveFailure;
        }));
        Assert.Same(saveFailure, failure.InnerExceptions[0]);
        Assert.Equal(2, failure.InnerExceptions.Count);
    }

    [Fact]
    public void FailedPersistenceReportsUnverifiedRollback()
    {
        var store = new FakeStore();
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        var failure = Assert.Throws<AggregateException>(() => service.SetEnabled(true, () =>
        {
            store.IgnoreNextMutation = true;
            throw new IOException("save failed");
        }));
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void NativeFailureDoesNotPersistAndPreservesConcurrentReplacement()
    {
        var concurrent = new StartupRegistrationValue("another writer", RegistryValueKind.String);
        var store = new FakeStore();
        store.AfterMutation = () => { store.Current = concurrent; throw new IOException("write failed"); };
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        int persists = 0;
        Assert.Throws<AggregateException>(() => service.SetEnabled(true, () => persists++));
        Assert.Equal(0, persists);
        Assert.Equal(concurrent, store.Current);
        Assert.Equal(1, store.Mutations);
    }

    [Fact]
    public void NullPersistIsRejectedBeforeReadingOrMutatingTheStore()
    {
        var store = new FakeStore();
        var service = new WindowsStartupRegistration(Executable, DataDirectory, store);
        Assert.Throws<ArgumentNullException>(() => service.SetEnabled(true, null!));
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Mutations);
    }

    private sealed class FakeStore : IStartupRegistrationStore
    {
        public Dictionary<string, StartupRegistrationValue> Values { get; } = new()
        {
            ["OtherApp"] = new("unrelated payload", RegistryValueKind.String),
        };
        public StartupRegistrationValue? Current
        {
            get => Values.GetValueOrDefault("MeetingDesktop");
            set { if (value is null) Values.Remove("MeetingDesktop"); else Values["MeetingDesktop"] = value; }
        }
        public int Reads { get; private set; }
        public int Mutations { get; private set; }
        public bool FailAfterMutation { get; set; }
        public bool IgnoreNextMutation { get; set; }
        public bool AlwaysFail { get; set; }
        public bool IgnoreRollback { get; set; }
        public Action? AfterMutation { get; set; }
        public StartupRegistrationValue? Read() { Reads++; return Current; }
        public void Write(StartupRegistrationValue value) => Mutate(value);
        public void Delete() => Mutate(null);

        private void Mutate(StartupRegistrationValue? value)
        {
            Mutations++;
            if (IgnoreNextMutation) { IgnoreNextMutation = false; return; }
            Current = value;
            AfterMutation?.Invoke();
            if (AlwaysFail || FailAfterMutation)
            {
                FailAfterMutation = false;
                IgnoreNextMutation = IgnoreRollback;
                throw new IOException("Injected store failure");
            }
        }
    }
}
