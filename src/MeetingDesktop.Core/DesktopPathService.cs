using System.Runtime.InteropServices;
using MeetingDesktop.Core.Interop;
using Microsoft.Win32;

namespace MeetingDesktop.Core;

/// <summary>
/// Reads and writes where Windows looks for the user's desktop folder.
/// </summary>
/// <remarks>
/// <para>
/// Three places record this path and they do not reliably agree, so nothing
/// here picks a winner: each reader reports what its own source said, or
/// <c>null</c> when that source had nothing, and the caller compares them.
/// Resolving the conflict belongs to the caller because the wrong answer is
/// indistinguishable from the user's files having vanished.
/// </para>
/// <para>
/// <see cref="ReadKnownFolder"/> is the source of truth. It goes through
/// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>, which
/// calls <c>SHGetKnownFolderPath</c> internally, so it is literally the
/// shell's own answer and there is nothing to P/Invoke here. The two registry
/// values are cross-checks: they record what explorer was <em>told</em>, which
/// is exactly what goes stale when the desktop is redirected - OneDrive does
/// this by default on many installs.
/// </para>
/// <para>
/// The readers swallow a missing value or a denied key and return <c>null</c>.
/// "This source had nothing" is a normal outcome the diagnostics page shows,
/// not an error worth failing the whole operation over.
/// </para>
/// </remarks>
public sealed class DesktopPathService
{
    /// <summary>The value name used under both Explorer keys.</summary>
    private const string DesktopValueName = "Desktop";

    /// <summary>The live value explorer uses to resolve the desktop folder.</summary>
    private const string UserShellFoldersKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    /// <summary>
    /// Microsoft documents this key as deprecated ("do not use this registry
    /// key"), but explorer still reads it and still writes it, so it is the
    /// value that shows what explorer last believed.
    /// </summary>
    private const string ShellFoldersKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders";

    /// <summary>Static holder type; there is no instance state to construct.</summary>
    public DesktopPathService()
    {
    }

    /// <summary>
    /// The <c>Desktop</c> value from <c>User Shell Folders</c>, exactly as
    /// stored.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="ReadUserShellFolders"/> so diagnostics can
    /// show the raw text next to the expansion of it. An install that stores
    /// <c>%USERPROFILE%\Desktop</c> is behaving correctly, and the raw value
    /// only looks wrong next to a fully expanded real path.
    /// </remarks>
    /// <returns>The stored text, or <c>null</c> if the value is absent or unreadable.</returns>
    public string? ReadUserShellFoldersRaw() =>
        ReadStringValue(UserShellFoldersKeyPath, expandEnvironmentNames: false);

    /// <summary>
    /// The <c>Desktop</c> value from <c>User Shell Folders</c>, with
    /// environment variables expanded.
    /// </summary>
    /// <remarks>
    /// The expansion is not cosmetic. The prototype compared the unexpanded
    /// value against a real path, so on any machine storing
    /// <c>%USERPROFILE%\Desktop</c> the two differed textually and the app
    /// concluded the desktop had moved when nothing had changed. Every value
    /// this class returns for comparison is expanded for that reason.
    /// </remarks>
    /// <returns>The expanded path, or <c>null</c> if the value is absent or unreadable.</returns>
    public string? ReadUserShellFolders() =>
        ReadStringValue(UserShellFoldersKeyPath, expandEnvironmentNames: true);

    /// <summary>
    /// The <c>Desktop</c> value from the deprecated <c>Shell Folders</c> key,
    /// with environment variables expanded.
    /// </summary>
    /// <remarks>
    /// Expanded even though this key is typed REG_SZ and therefore should not
    /// contain variables: tools that write it by hand do put
    /// <c>%USERPROFILE%\Desktop</c> in it anyway. Expanding a string with no
    /// variables is a no-op, so this costs nothing and keeps the value
    /// comparable with the ones from the other sources.
    /// </remarks>
    /// <returns>The expanded path, or <c>null</c> if the value is absent or unreadable.</returns>
    public string? ReadShellFoldersLegacy() =>
        ReadStringValue(ShellFoldersKeyPath, expandEnvironmentNames: true);

    /// <summary>
    /// The desktop folder as the shell itself reports it. This is the source of
    /// truth; the registry values above are cross-checks against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Environment.SpecialFolderOption.DoNotVerify"/> because a read
    /// must not create anything, and because the path is still the right answer
    /// while the folder is momentarily absent - a redirected desktop on a
    /// disconnected drive, for instance. Whether it exists on disk is a
    /// separate question the caller answers for itself.
    /// </para>
    /// <para>
    /// Path resolution and the active desktop view are separate observations.
    /// This API can resolve the new path while Explorer still presents the old
    /// folder. Registry readers report the stored values, and the workflows
    /// independently verify the active view's parsed items before proceeding.
    /// No particular cache lifetime is assumed.
    /// </para>
    /// </remarks>
    /// <returns>The known-folder path, or <c>null</c> when the shell has none.</returns>
    public string? ReadKnownFolder()
    {
        string path = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolderOption.DoNotVerify);

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>
    /// Points Windows at <paramref name="desktopPath"/>.
    /// </summary>
    /// <remarks>
    /// Both keys are written, and deliberately: <c>User Shell Folders</c> is
    /// where explorer resolves the folder from, and <c>Shell Folders</c> is the
    /// cache it falls back to. Writing only the first leaves a stale cache that
    /// some shell code paths still prefer, which reads as the change "not
    /// sticking" on some machines. Explorer caches these values in memory at
    /// startup, so the caller restarts it for the change to take effect.
    /// </remarks>
    /// <param name="desktopPath">The absolute folder path to record.</param>
    /// <exception cref="ArgumentException">
    /// The path is empty, whitespace, or not fully qualified. A relative path
    /// would be resolved against explorer's working directory rather than ours,
    /// which is not the folder the user was shown.
    /// </exception>
    /// <exception cref="InvalidOperationException">The key could not be opened for writing.</exception>
    public void Write(string desktopPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopPath);

        // IsPathFullyQualified rather than IsPathRooted: IsPathRooted accepts
        // "\Desktop" and "C:Desktop", both of which are relative to something
        // the caller does not control.
        if (!Path.IsPathFullyQualified(desktopPath))
        {
            throw new ArgumentException(
                $"The desktop path must be absolute. Received: '{desktopPath}'.",
                nameof(desktopPath));
        }

        // Update the Known Folder registration through the supported Shell API
        // as well as the registry cross-checks. API success still does not prove
        // that the active desktop view followed; the workflow verifies that.
        Guid id = ShellNative.DesktopId;
        Marshal.ThrowExceptionForHR(ShellNative.SHSetKnownFolderPath(ref id, 0, 0, desktopPath));
        WriteValue(UserShellFoldersKeyPath, desktopPath, RegistryValueKind.ExpandString);
        WriteValue(ShellFoldersKeyPath, desktopPath, RegistryValueKind.String);
    }

    private static void WriteValue(string keyPath, string value, RegistryValueKind kind)
    {
        // CreateSubKey, not OpenSubKey: it also creates the key if it is
        // missing, while SetValue creates the value. Either call succeeds on a
        // healthy machine and fails identically on a locked-down one.
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException(
                $@"Could not open or create HKCU\{keyPath} for writing.");

        key.SetValue(DesktopValueName, value, kind);
    }

    private static string? ReadStringValue(string keyPath, bool expandEnvironmentNames)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);

            // Read without the API's own expansion even when we are about to
            // expand, so the raw and expanded readers differ by exactly the
            // documented step rather than by whatever the registry helper
            // happens to do with REG_EXPAND_SZ.
            if (key?.GetValue(
                    DesktopValueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw
                || string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            // ExpandEnvironmentVariables leaves an unknown %VAR% untouched
            // rather than blanking it, so a typo stays visible in diagnostics
            // instead of turning into an empty path that looks like "unset".
            return expandEnvironmentNames
                ? Environment.ExpandEnvironmentVariables(raw)
                : raw;
        }
        catch (Exception ex) when (ex is
            System.Security.SecurityException or
            UnauthorizedAccessException or
            IOException)
        {
            return null;
        }
    }
}
