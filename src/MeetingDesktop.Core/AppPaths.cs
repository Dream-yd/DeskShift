using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

/// <summary>
/// Resolves the directories the app uses at runtime, and creates them on demand.
/// </summary>
/// <remarks>
/// <para>
/// The data directory is resolved from the first source that yields a usable
/// value:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <c>--data-dir &lt;path&gt;</c> or <c>--data-dir=&lt;path&gt;</c> on the
/// command line.
/// </description>
/// </item>
/// <item>
/// <description>The <c>MEETINGDESKTOP_DATA</c> environment variable.</description>
/// </item>
/// <item>
/// <description>
/// A <c>portable.txt</c> file next to the executable, which selects
/// <c>&lt;exe dir&gt;\Data</c>. Only the file's presence is read; its contents
/// are ignored, so a user can leave a note in it.
/// </description>
/// </item>
/// <item><description><c>%LOCALAPPDATA%\MeetingDesktop</c>.</description></item>
/// </list>
/// <para>
/// The default is deliberately <em>not</em> a folder next to the executable.
/// Installed under Program Files that folder is not writable, and the
/// redirected desktop would be read-only, so a presenter could not drop the
/// slides they are presenting into it.
/// </para>
/// <para>
/// <c>%LOCALAPPDATA%</c> is per-user, which matches the scope of the setting
/// being changed - the desktop path is an HKCU value, so a machine-wide
/// location would let one account's swap overwrite another's - and it is
/// always on a local fixed disk. The scripts this replaces put the live
/// desktop on a data drive; when that drive is removable or a network share
/// and happens to be absent at sign-in, the user's desktop silently becomes a
/// missing folder.
/// </para>
/// <para>
/// Instances are immutable and cheap to build. Resolve once during startup and
/// pass the instance around rather than calling <see cref="Resolve"/> from
/// each component, so that everything in a run agrees on one data directory:
/// a second resolution that disagreed would look for the session file in the
/// wrong place and report a swapped desktop as "not ours".
/// </para>
/// </remarks>
public sealed class AppPaths
{
    /// <summary>Command-line switch that overrides the data directory.</summary>
    private const string DataDirectorySwitch = "--data-dir";

    /// <summary>Environment variable that overrides the data directory.</summary>
    private const string DataDirectoryEnvironmentVariable = "MEETINGDESKTOP_DATA";

    /// <summary>Marker file next to the executable that requests a portable install.</summary>
    private const string PortableMarkerFileName = "portable.txt";

    /// <summary>
    /// Folder holding the redirected desktop, under the data directory. The
    /// name is what the user sees in the address bar while the swap is active,
    /// so it reads as a place rather than as an implementation detail.
    /// </summary>
    private const string MeetingDesktopFolderName = "Desktop";

    private const string StateFolderName = "State";

    private const string LayoutsFolderName = "Layouts";

    private const string RecoveryFolderName = "Recovery";

    private const string LogsFolderName = "Logs";

    private const string SessionFileName = "session.json";

    private const string SettingsFileName = "settings.json";

    private AppPaths(string dataDirectory, string meetingDesktopDirectory, bool isPortable)
    {
        DataDirectory = dataDirectory;
        MeetingDesktopDirectory = meetingDesktopDirectory;
        IsPortable = isPortable;

        StateDirectory = Path.Combine(dataDirectory, StateFolderName);
        LayoutsDirectory = Path.Combine(dataDirectory, LayoutsFolderName);
        RecoveryDirectory = Path.Combine(dataDirectory, RecoveryFolderName);
        LogsDirectory = Path.Combine(dataDirectory, LogsFolderName);

        SessionFilePath = Path.Combine(StateDirectory, SessionFileName);
        SettingsFilePath = Path.Combine(dataDirectory, SettingsFileName);
    }

    /// <summary>
    /// Resolves the paths for this run.
    /// </summary>
    /// <param name="commandLineArgs">
    /// Arguments the process was started with. Pass <c>e.Args</c> from the
    /// application entry point; the executable path that
    /// <see cref="Environment.GetCommandLineArgs"/> prepends is ignored, so
    /// passing that array verbatim is equally fine.
    /// </param>
    /// <returns>The resolved paths. Nothing on disk is touched.</returns>
    /// <exception cref="IOException">
    /// A supplied override (command line or environment variable) is not a
    /// usable directory path. Silently falling through to the default would
    /// strand the session file in a directory the next run would not look in.
    /// </exception>
    public static AppPaths Resolve(string[]? commandLineArgs = null)
    {
        string? fromCommandLine = ReadCommandLineOverride(commandLineArgs);
        if (!string.IsNullOrWhiteSpace(fromCommandLine))
        {
            string dataDirectory = NormalizeExplicitPath(fromCommandLine, DataDirectorySwitch);
            return new AppPaths(dataDirectory, Path.Combine(dataDirectory, MeetingDesktopFolderName), isPortable: false);
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            string dataDirectory = NormalizeExplicitPath(fromEnvironment, DataDirectoryEnvironmentVariable);
            return new AppPaths(dataDirectory, Path.Combine(dataDirectory, MeetingDesktopFolderName), isPortable: false);
        }

        if (HasPortableMarker())
        {
            // AppContext.BaseDirectory, not the current directory: the app can
            // be started from a shortcut whose working directory is anywhere.
            string dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
            return new AppPaths(dataDirectory, Path.Combine(dataDirectory, MeetingDesktopFolderName), isPortable: true);
        }

        return new AppPaths(
            DefaultDataDirectory,
            Path.Combine(DefaultDataDirectory, MeetingDesktopFolderName),
            isPortable: false);
    }

    /// <summary>The root folder this run keeps everything in.</summary>
    public string DataDirectory { get; }

    /// <summary>
    /// Default location of the folder the desktop is redirected to.
    /// </summary>
    /// <remarks>
    /// A default only: <c>AppSettings.MeetingDesktopPath</c> overrides it, and
    /// the user is expected to move it off the system drive.
    /// </remarks>
    public string MeetingDesktopDirectory { get; }

    /// <summary>Session bookkeeping that is meaningless once a swap is undone.</summary>
    public string StateDirectory { get; }

    /// <summary>Saved icon layouts the user keeps for later reuse.</summary>
    public string LayoutsDirectory { get; }

    /// <summary>Copies taken before a risky operation, so a bad restore can be walked back.</summary>
    public string RecoveryDirectory { get; }

    public string LogsDirectory { get; }

    /// <summary>Absolute path of the file holding the live <c>SessionInfo</c>.</summary>
    public string SessionFilePath { get; }

    /// <summary>
    /// Absolute path of the persisted <c>AppSettings</c> document.
    /// </summary>
    /// <remarks>
    /// Kept out of <see cref="StateDirectory"/> on purpose. State is disposable
    /// per-swap bookkeeping that a recovery step may clear; settings are
    /// durable preferences and must survive that.
    /// </remarks>
    public string SettingsFilePath { get; }

    /// <summary>
    /// The meeting-desktop folder this run will actually use, given the user's
    /// settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every caller goes through this.</b> The folder is named in two places
    /// - the user's settings, and <see cref="MeetingDesktopDirectory"/> - and on
    /// a fresh install only the second one exists, because nothing has been
    /// stored yet. Resolving it in one place is what keeps the swap and the
    /// detector looking at the same folder.
    /// </para>
    /// <para>
    /// Resolving it in some places and not others is not a cosmetic
    /// inconsistency, which is how this was found. The prepare step swapped to
    /// the default while the session detector was handed the raw null setting,
    /// read that as "no meeting folder is configured", and so could not
    /// recognise the app's own swap. It fell through to the legacy-shape test,
    /// saw a folder sitting inside the app data directory, and reported the
    /// swap as <see cref="SessionState.Adopted"/> - "the desktop looks
    /// swapped and we have no record of doing it" - on a machine holding the
    /// record of doing exactly that. The user's real desktop then read as
    /// unrecognised, and the only route back was the Restore page, whose
    /// wording had also been built around the wrong state.
    /// </para>
    /// </remarks>
    /// <param name="settings">The stored settings. A null value is the normal case.</param>
    /// <returns>An absolute folder path. Not checked for existence.</returns>
    public string ResolveMeetingDesktopDirectory(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return string.IsNullOrWhiteSpace(settings.MeetingDesktopPath)
            ? MeetingDesktopDirectory
            : settings.MeetingDesktopPath;
    }

    /// <summary>
    /// True when the data directory sits next to the executable because
    /// <c>portable.txt</c> asked for it.
    /// </summary>
    /// <remarks>
    /// An explicit <c>--data-dir</c> or <c>MEETINGDESKTOP_DATA</c> wins even
    /// when the marker file is present, and this is then false: with the data
    /// elsewhere, nothing about the run depends on the install folder, so a UI
    /// banner promising "all data stays with the app" would be lying.
    /// </remarks>
    public bool IsPortable { get; }

    /// <summary><c>%LOCALAPPDATA%\MeetingDesktop</c>, the fallback location.</summary>
    /// <exception cref="InvalidOperationException">
    /// Windows reported no local application data folder. This only happens for
    /// accounts with no profile - a service account, for instance - and there is
    /// no correct substitute: roaming AppData may be a network share, and the
    /// temp directory is purged.
    /// </exception>
    public static string DefaultDataDirectory
    {
        get
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                throw new InvalidOperationException(
                    "Windows reported no local application data folder, so the app data directory cannot be resolved.");
            }

            return Path.Combine(localAppData, "MeetingDesktop");
        }
    }

    /// <summary>
    /// Creates the data directory and the five folders inside it.
    /// </summary>
    /// <remarks>
    /// Safe to call on every start: the underlying create is a no-op for a
    /// directory that already exists, and re-running it also repairs a folder a
    /// user deleted, which is exactly when the app is about to need it.
    /// </remarks>
    /// <exception cref="IOException">A directory could not be created.</exception>
    public void EnsureCreated()
    {
        string[] directories =
        [
            DataDirectory,
            MeetingDesktopDirectory,
            StateDirectory,
            LayoutsDirectory,
            RecoveryDirectory,
            LogsDirectory,
        ];

        foreach (string directory in directories)
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or ArgumentException or System.Security.SecurityException)
            {
                // Name the folder: "access denied" without a path sends the user
                // to the wrong drive when a redirected desktop is involved.
                throw new IOException($"Could not create the application directory '{directory}'.", ex);
            }
        }
    }

    /// <summary>
    /// Reads <c>--data-dir &lt;path&gt;</c> or <c>--data-dir=&lt;path&gt;</c>.
    /// </summary>
    /// <returns>
    /// The raw value, or null when the switch is absent or has no value. A
    /// switch with nothing after it is treated as absent rather than as an
    /// error, because refusing to start over a stray flag helps nobody.
    /// </returns>
    private static string? ReadCommandLineOverride(string[]? commandLineArgs)
    {
        if (commandLineArgs is null)
        {
            return null;
        }

        // Ordinal-ignore-case: Windows users type --Data-Dir, and the platform
        // convention is that switches are case-insensitive.
        const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;
        string prefix = DataDirectorySwitch + "=";

        for (int i = 0; i < commandLineArgs.Length; i++)
        {
            string? argument = commandLineArgs[i];
            if (string.IsNullOrEmpty(argument))
            {
                continue;
            }

            if (argument.Equals(DataDirectorySwitch, Comparison))
            {
                return i + 1 < commandLineArgs.Length ? commandLineArgs[i + 1] : null;
            }

            if (argument.StartsWith(prefix, Comparison))
            {
                return argument[prefix.Length..];
            }
        }

        return null;
    }

    private static bool HasPortableMarker()
    {
        try
        {
            return File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarkerFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            // An unreadable install folder is not a portable install; the
            // per-user default still works, so carry on with it.
            return false;
        }
    }

    /// <summary>
    /// Turns a user-supplied value into an absolute, expanded path.
    /// </summary>
    /// <param name="raw">The value as written, possibly quoted or relative.</param>
    /// <param name="source">Where it came from, for the error message.</param>
    private static string NormalizeExplicitPath(string raw, string source)
    {
        // Quotes survive when a path is pasted into a shortcut's target field,
        // and .NET Core no longer rejects '"' in a path - it would quietly
        // become part of the folder name.
        string value = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"').Trim());

        try
        {
            // Absolute, because the value is compared against recorded paths
            // and handed to the registry, where a relative path would resolve
            // against another process's working directory.
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException($"The {source} value '{raw}' is not a usable directory path.", ex);
        }
    }
}
