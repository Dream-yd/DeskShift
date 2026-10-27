using System.IO;
using System.Collections;
using System.Diagnostics;
using Microsoft.Win32;

namespace MeetingDesktop.App.Services;

/// <summary>A raw Run value, retaining its registry kind for lossless rollback.</summary>
public sealed record StartupRegistrationValue(object Value, RegistryValueKind Kind);

/// <summary>Accesses only the application's own startup value. Null means absent.</summary>
public interface IStartupRegistrationStore
{
    StartupRegistrationValue? Read();
    void Write(StartupRegistrationValue value);
    void Delete();
}

/// <summary>Opt-in, current-user startup registration. Construction never touches the registry.</summary>
public sealed class WindowsStartupRegistration
{
    private readonly IStartupRegistrationStore _store;
    private readonly StartupRegistrationValue _expected;
    private readonly string _dataDirectory;
    private readonly Func<string, bool> _isPriorExecutable;

    public WindowsStartupRegistration(string executable, string dataDirectory)
        : this(executable, dataDirectory, new CurrentUserRunStore()) { }

    public WindowsStartupRegistration(string executable, string dataDirectory, IStartupRegistrationStore store)
        : this(executable, dataDirectory, store, IsRecognisedExecutable) { }

    internal WindowsStartupRegistration(string executable, string dataDirectory, IStartupRegistrationStore store,
        Func<string, bool> isPriorExecutable)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(isPriorExecutable);
        _expected = new(BuildCommand(executable, dataDirectory), RegistryValueKind.String);
        _dataDirectory = dataDirectory;
        _isPriorExecutable = isPriorExecutable;
        _store = store;
    }

    /// <summary>Reads the raw, unexpanded registration, independently of saved preferences.</summary>
    public StartupRegistrationValue? ReadActual() => _store.Read();

    /// <summary>True only for this executable, data directory and exact argument list as REG_SZ.</summary>
    public bool IsEnabled() => Matches(ReadActual());

    /// <summary>
    /// Enabling installs or updates this app's named value. Disabling removes a recognised
    /// command for this data directory, including a previous app version. Unrecognised
    /// payloads are preserved and reported. A failure restores the previous value and kind.
    /// </summary>
    public void SetEnabled(bool enabled) => SetEnabled(enabled, static () => { });

    /// <summary>
    /// Verifies the requested registration change before synchronously persisting settings.
    /// If either step fails, restores the exact prior payload and kind only while the live value
    /// still matches our write. Concurrent replacements are preserved and reported, together with
    /// the original failure, as an AggregateException so callers can display an uncertain state.
    /// </summary>
    public void SetEnabled(bool enabled, Action persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        StartupRegistrationValue? previous = ReadActual();
        if (!enabled && previous is not null && !Matches(previous) && !MatchesPreviousVersion(previous))
            throw new IOException("The Windows startup entry is not recognised; it was left unchanged.");
        if (enabled ? Matches(previous) : previous is null)
        {
            persist();
            return;
        }

        Change(previous, enabled, persist);
    }

    /// <summary>Migrates only a recognised old version for this data directory.</summary>
    public bool CanMigratePreviousVersion()
    {
        StartupRegistrationValue? previous = ReadActual();
        return previous is not null && !Matches(previous) && MatchesPreviousVersion(previous);
    }

    /// <summary>Updates the entry only in response to an explicit user action.</summary>
    public bool TryMigratePreviousVersion()
    {
        StartupRegistrationValue? previous = ReadActual();
        if (previous is null || Matches(previous) || !MatchesPreviousVersion(previous)) return false;
        Change(previous, true, static () => { });
        return true;
    }

    private void Change(StartupRegistrationValue? previous, bool enabled, Action persist)
    {
        StartupRegistrationValue? written = enabled ? _expected : null;
        try
        {
            if (!SameValue(ReadActual(), previous))
                throw new IOException("The Windows startup registration changed before it could be updated.");
            if (enabled) _store.Write(_expected);
            else _store.Delete();

            StartupRegistrationValue? actual = ReadActual();
            if (!SameValue(actual, written))
                throw new IOException("The Windows startup registration could not be verified.");

            persist();
        }
        catch (Exception failure)
        {
            try
            {
                StartupRegistrationValue? current = ReadActual();
                if (!SameValue(current, previous))
                {
                    // Optimistic ownership check immediately before restoration. The registry
                    // offers no compare-and-swap here; never overwrite an observed replacement.
                    if (!SameValue(current, written))
                        throw new IOException("The Windows startup registration changed concurrently; rollback was skipped to preserve that value.");
                    if (previous is null) _store.Delete();
                    else _store.Write(previous);
                }
                if (!SameValue(ReadActual(), previous))
                    throw new IOException("The previous Windows startup registration could not be verified after rollback.");
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("Windows startup registration failed and its previous value could not be restored.",
                    failure, rollbackFailure);
            }
            throw;
        }
    }

    /// <summary>Builds a direct executable command; no shell, environment expansion or current directory dependence.</summary>
    public static string BuildCommand(string executable, string dataDirectory)
    {
        ValidatePath(executable, nameof(executable));
        if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The startup executable must be a fully qualified .exe path.", nameof(executable));
        ValidatePath(dataDirectory, nameof(dataDirectory));
        return $"{Quote(executable)} --data-dir {Quote(dataDirectory)} --start-in-tray";
    }

    private bool Matches(StartupRegistrationValue? actual) => actual?.Kind == RegistryValueKind.String &&
        actual.Value is string command && string.Equals(command, (string)_expected.Value, StringComparison.Ordinal);

    private bool MatchesPreviousVersion(StartupRegistrationValue actual)
    {
        if (actual.Kind != RegistryValueKind.String || actual.Value is not string command ||
            command.Length < 3 || command[0] != '"') return false;
        int end = command.IndexOf('"', 1);
        if (end < 2) return false;
        string executable = command[1..end];
        if (!Path.IsPathFullyQualified(executable) ||
            !string.Equals(Path.GetFileName(executable), "MeetingDesktop.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try { return string.Equals(command, BuildCommand(executable, _dataDirectory), StringComparison.Ordinal) && _isPriorExecutable(executable); }
        catch (ArgumentException) { return false; }
    }

    private static bool IsRecognisedExecutable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(version.ProductName, "Meeting(Clean)-Desktop", StringComparison.Ordinal) &&
                string.Equals(version.CompanyName, "Meeting(Clean)-Desktop contributors", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
            System.Security.SecurityException or ArgumentException) { return false; }
    }

    private static bool SameValue(StartupRegistrationValue? actual, StartupRegistrationValue? expected) =>
        actual is null ? expected is null : expected is not null && actual.Kind == expected.Kind &&
            StructuralComparisons.StructuralEqualityComparer.Equals(actual.Value, expected.Value);

    private static void ValidatePath(string path, string parameter)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.Contains('"') || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A fully qualified path without embedded quotes or NUL is required.", parameter);
    }

    private static string Quote(string value)
    {
        // Quotes are rejected above. Only backslashes immediately before the closing quote
        // need doubling to survive Windows command-line argument parsing (including drive roots).
        int trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailing) + "\"";
    }

    private sealed class CurrentUserRunStore : IStartupRegistrationStore
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MeetingDesktop";

        public StartupRegistrationValue? Read()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is null ? null : new(value, key!.GetValueKind(ValueName));
        }

        public void Write(StartupRegistrationValue value)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(ValueName, value.Value, value.Kind);
        }

        public void Delete()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
