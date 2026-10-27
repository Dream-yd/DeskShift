using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Input;
using MeetingDesktop.App.Localization;
using MeetingDesktop.App.Services;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.App.ViewModels;

/// <summary>
/// The Diagnostics page: a plain-text record of everything this app detected
/// about the machine it is running on, for the user to paste into a bug report.
/// </summary>
/// <remarks>
/// <para>
/// Every source the report reads is read-only, so opening this page cannot
/// change the state it is describing. That matters because the page is reached
/// precisely when something looks wrong.
/// </para>
/// <para>
/// It deliberately does <em>not</em> read the desktop icon list. Icon captions
/// are the user's own file names - and on a machine that has just been swapped,
/// the file names a presenter saved - while this page exists to be pasted
/// somewhere public. Everything here is machine configuration: paths, handles,
/// identifiers, geometry.
/// </para>
/// <para>
/// Section headings come from the resource files, so a Chinese user reads a
/// Chinese report. The key half of each line - <c>originalDesktopPath</c>,
/// <c>hwnd</c>, <c>status</c> - stays a stable ASCII identifier mirroring the
/// data model, because a maintainer greps a report for one spelling of a field,
/// not for four translations of it.
/// </para>
/// </remarks>
public sealed class DiagnosticsViewModel : ObservableObject, IPageViewModel
{
    /// <summary>How many times the report is offered to the clipboard.</summary>
    private const int ClipboardAttempts = 3;

    /// <summary>Base wait between clipboard attempts, multiplied by the attempt number.</summary>
    private const int ClipboardRetryDelayMs = 60;

    /// <summary>Timestamp shape for the report. Invariant, so the same machine reports the same way twice.</summary>
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss zzz";

    private readonly AppPaths _paths;
    private readonly SessionStore _sessions;
    private readonly DesktopLocationService _locations;
    private readonly MainViewModel _shell;

    private readonly AsyncRelayCommand _copyCommand;
    private readonly RelayCommand _refreshCommand;

    private string _reportText = string.Empty;
    private string _rawReportText = string.Empty;
    private bool _redactPaths = true;
    private readonly RelayCommand _exportCommand;

    public bool RedactPaths
    {
        get => _redactPaths;
        set
        {
            if (SetProperty(ref _redactPaths, value)) UpdatePreview();
        }
    }

    public ICommand ExportCommand => _exportCommand;

    private void UpdatePreview()
    {
        // Redact entire path-bearing values, including arbitrary configured drives,
        // UNC shares, environment-variable paths and paths in exception messages.
        ReportText = RedactPaths
            ? Regex.Replace(_rawReportText, @"(?im)(?:[a-z]:[\\/]|\\\\|%[a-z_][a-z0-9_]*%[\\/])[^\r\n]*", "[redacted path]")
            : _rawReportText;
        _copyCommand.RaiseCanExecuteChanged();
        _exportCommand.RaiseCanExecuteChanged();
    }

    private void Export()
    {
        string preview = ReportText;
        var dialog = new SaveFileDialog
        {
            Title = LocalizationManager.Instance["DiagnosticExportTitle"],
            Filter = LocalizationManager.Instance["DiagnosticExportFilter"],
            FileName = $"MeetingDesktop-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            OverwritePrompt = true
        };
        try
        {
            if (dialog.ShowDialog() != true) return;
            // Text only: never enumerate state assets or attach desktop captures.
            File.WriteAllText(dialog.FileName, preview, new UTF8Encoding(false));
            _shell.StatusText = LocalizationManager.Instance["DiagnosticExportSaved"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            _shell.StatusText = LocalizationManager.Instance["DiagnosticExportFailed"];
        }
    }

    /// <summary>
    /// Which report build is the current one. See <see cref="RefreshAsync"/>.
    /// </summary>
    private int _generation;

    public DiagnosticsViewModel(
        AppPaths paths,
        SessionStore sessions,
        DesktopLocationService locations,
        MainViewModel shell)
    {
        _paths = paths;
        _sessions = sessions;
        _locations = locations;
        _shell = shell;

        // Async even though the copy itself is instant: the retries have to
        // wait between attempts, and waiting on the UI thread for a clipboard
        // another process is holding is exactly the kind of stall this page is
        // supposed to help diagnose.
        _copyCommand = new AsyncRelayCommand(CopyAsync, () => _reportText.Length > 0);
        _refreshCommand = new RelayCommand(() => _ = RefreshAsync());
        _exportCommand = new RelayCommand(Export, () => ReportText.Length > 0);
    }

    /// <summary>The report as plain text. Empty until the first build finishes.</summary>
    public string ReportText
    {
        get => _reportText;
        private set => SetProperty(ref _reportText, value);
    }

    public ICommand CopyCommand => _copyCommand;

    public ICommand RefreshCommand => _refreshCommand;

    /// <summary>
    /// Rebuilds the report. The machine can have changed since the last look -
    /// explorer restarted, the desktop was swapped, a display was plugged in -
    /// and a stale report is worse than none.
    /// </summary>
    public void OnActivated() => _ = RefreshAsync();

    /// <summary>
    /// Rebuilds the report in the newly selected language.
    /// </summary>
    /// <remarks>
    /// The headings are baked into the string when it is composed, so nothing
    /// bound to <see cref="ReportText"/> can pick up a language change on its
    /// own.
    /// </remarks>
    public void RefreshLocalizedText()
    {
        // Off-screen reports are rebuilt on activation. Preference autosaves
        // must not launch background registry and session reads on every edit.
        if (ReferenceEquals(_shell.CurrentView, this)) _ = RefreshAsync();
    }

    public void RaiseRaiseCanExecuteChanged()
    {
        _copyCommand.RaiseCanExecuteChanged();
        _refreshCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Builds the report off the UI thread and publishes it when it is still
    /// the newest one asked for.
    /// </summary>
    /// <remarks>
    /// The window enumeration sends <c>WM_GETTEXT</c> to windows owned by other
    /// processes, and that call has no timeout: one hung window on the machine
    /// would freeze this app's window for as long as it stayed hung. The
    /// registry reads behind the desktop detection are cheap but are also not
    /// this thread's business.
    /// </remarks>
    private async Task RefreshAsync()
    {
        int generation = ++_generation;

        string text = await Task.Run(ComposeReport).ConfigureAwait(true);

        // Opening the page and switching language can both be in flight; only
        // the last one describes what the user is looking at.
        if (generation != _generation)
        {
            return;
        }

        _rawReportText = text;
        UpdatePreview();

        // Text arriving for the first time is what makes Copy meaningful.
        _copyCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Puts the report on the clipboard.
    /// </summary>
    /// <remarks>
    /// The clipboard is a machine-wide resource with a single owner, so
    /// <see cref="Clipboard.SetText(string)"/> throws <see cref="COMException"/>
    /// while another process holds it open. Those holds last for milliseconds -
    /// a clipboard manager reading what it was just handed - so retrying beats
    /// telling the user to press the button again and hoping.
    /// </remarks>
    private async Task CopyAsync()
    {
        string text = _reportText;
        if (text.Length == 0)
        {
            return;
        }

        for (int attempt = 1; attempt <= ClipboardAttempts; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                _shell.StatusText = LocalizationManager.Instance["DiagnosticsCopied"];
                return;
            }
            catch (COMException)
            {
                if (attempt == ClipboardAttempts)
                {
                    break;
                }

                // Waiting rather than retrying immediately: the holder has to
                // finish what it is doing before a retry can succeed.
                await Task.Delay(ClipboardRetryDelayMs * attempt).ConfigureAwait(true);
            }
        }

        _shell.StatusText = LocalizationManager.Instance["ErrorUnexpected"];
    }

    // ---------------------------------------------------------------
    // Report
    // ---------------------------------------------------------------

    /// <summary>Builds the whole report. Safe to call from any thread.</summary>
    private string ComposeReport()
    {
        var builder = new StringBuilder(2048);

        try
        {
            AppendEnvironment(builder);
            Section(builder, LocalizationManager.Instance["TimingTitle"]);
            builder.AppendLine(SwitchTimingText.Format(_shell.SwitchTimings.Snapshot()));
            AppendPaths(builder);
            AppendDesktopLocation(builder);
            AppendSession(builder);
            AppendWindows(builder);
            AppendDisplayTopology(builder);
        }
        catch (Exception ex)
        {
            // This page exists to explain a failure. Falling over with one of
            // its own would leave the user with an empty box and nothing to
            // report, so whatever is readable is kept and the failure is
            // appended as one more fact.
            builder.AppendLine()
                   .Append(LocalizationManager.Instance["ErrorUnexpected"])
                   .Append(" (").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(')')
                   .AppendLine();
        }

        return builder.ToString();
    }

    private void AppendEnvironment(StringBuilder builder)
    {
        Assembly assembly = typeof(DiagnosticsViewModel).Assembly;

        builder.AppendLine(LocalizationManager.Instance["AppTitle"]);
        builder.AppendLine(LocalizationManager.Instance["DiagnosticsTitle"]);

        Field(
            builder,
            "generated",
            DateTimeOffset.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        Field(
            builder,
            "version",
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString());
        Field(builder, "os", RuntimeInformation.OSDescription);
        Field(builder, "osVersion", Environment.OSVersion.VersionString);
        Field(builder, "elevated", Bool(IsElevated()));
        Field(builder, "process64Bit", Bool(Environment.Is64BitProcess));
        Field(builder, "os64Bit", Bool(Environment.Is64BitOperatingSystem));
        Field(builder, "desktopViewVerification", "required");
        builder.AppendLine(LocalizationManager.Instance["DiagnosticsLaunchHint"]);
    }

    private void AppendPaths(StringBuilder builder)
    {
        Section(builder, LocalizationManager.Instance["DiagnosticsPaths"]);

        Field(builder, LocalizationManager.Instance["DiagnosticsDataFolder"], _paths.DataDirectory);
        Field(builder, LocalizationManager.Instance["SettingsPortable"], Bool(_paths.IsPortable));
    }

    private void AppendDesktopLocation(StringBuilder builder)
    {
        DesktopLocation location = _locations.Detect(_shell.CurrentSettings.MeetingDesktopPath);

        Section(builder, LocalizationManager.Instance["DiagnosticsDetectedDesktop"]);

        Field(builder, "status", location.Status.ToString());
        Field(builder, "resolvedPath", location.ResolvedPath);
        Field(builder, LocalizationManager.Instance["DiagnosticsOneDrive"], Bool(location.IsOneDriveRedirected));

        // Only meaningful when more than one source had something to say.
        // "All sources agree" on a machine where three of the four could not be
        // read at all would be a claim the report cannot support.
        if (location.Status is DesktopLocationStatus.Agreed or DesktopLocationStatus.Conflicting)
        {
            builder.AppendLine(location.Status == DesktopLocationStatus.Conflicting
                ? LocalizationManager.Instance["DiagnosticsOutOfSync"]
                : LocalizationManager.Instance["DiagnosticsInSync"]);
        }

        Section(builder, LocalizationManager.Instance["DiagnosticsCandidates"]);

        IReadOnlyList<DesktopPathCandidate> candidates = location.Candidates;

        for (int index = 0; index < candidates.Count; index++)
        {
            DesktopPathCandidate candidate = candidates[index];

            // Blank line between rows, never after the last one: a trailing
            // gap would stack up with the one the next heading opens with.
            if (index > 0)
            {
                builder.AppendLine();
            }

            builder.Append('[')
                   .Append(SourceName(candidate.Source))
                   .Append("] ")
                   .AppendLine(LocalizationManager.Instance[
                       candidate.Exists ? "DiagnosticsExists" : "DiagnosticsMissing"]);

            Field(builder, "rawValue", candidate.RawValue);
            Field(builder, "expandedPath", candidate.ExpandedPath);
            Field(builder, "note", candidate.Note);
        }
    }

    private void AppendSession(StringBuilder builder)
    {
        Section(builder, LocalizationManager.Instance["DiagnosticsSession"]);

        Field(builder, "recordExists", Bool(_sessions.Exists));

        SessionInfo? session = _sessions.Load();
        if (session is null)
        {
            // No file, or one this build refuses to trust. Either way there is
            // nothing further to say, and five lines of "not available" would
            // bury that.
            return;
        }

        Field(builder, "originalDesktopPath", session.OriginalDesktopPath);
        Field(builder, "meetingDesktopPathAtSwap", session.MeetingDesktopPathAtSwap);
        Field(builder, "swapped", FormatTimestamp(session.SwappedUtc));
        Field(builder, "capturedIconCount", session.CapturedIconCount.ToString(CultureInfo.InvariantCulture));
        Field(builder, "topologyAtSwap", session.TopologyAtSwap.Signature);
    }

    private void AppendWindows(StringBuilder builder)
    {
        Section(builder, LocalizationManager.Instance["DiagnosticsWindows"]);

        IReadOnlyList<DiagnosticEntry> entries = DesktopGridView.Diagnostics();
        if (entries.Count == 0)
        {
            builder.AppendLine(NotAvailable);
            return;
        }

        // The same shape the core uses when it fails to find the listview, so
        // a report from here and a message from there line up.
        foreach (DiagnosticEntry entry in entries)
        {
            builder.Append("hwnd=0x").Append(entry.Hwnd.ToString("X", CultureInfo.InvariantCulture))
                   .Append(" pid=").Append(entry.ProcessId.ToString(CultureInfo.InvariantCulture))
                   .Append(" class=").Append(entry.ClassName)
                   .Append(" title='").Append(entry.Title).Append('\'')
                   .Append(" underDesktopHost=").Append(Bool(entry.UnderDesktopHost))
                   .AppendLine();
        }
    }

    private void AppendDisplayTopology(StringBuilder builder)
    {
        MonitorTopology topology = MonitorTopologyReader.Capture();

        Section(builder, "Monitors");

        if (topology.Monitors.Count == 0)
        {
            builder.AppendLine(NotAvailable);
            return;
        }

        Field(builder, "signature", topology.Signature);

        foreach (MonitorInfo monitor in topology.Monitors)
        {
            builder.Append(monitor.DeviceName)
                   .Append(' ').Append(monitor.X.ToString(CultureInfo.InvariantCulture))
                   .Append(',').Append(monitor.Y.ToString(CultureInfo.InvariantCulture))
                   .Append(' ').Append(monitor.Width.ToString(CultureInfo.InvariantCulture))
                   .Append('x').Append(monitor.Height.ToString(CultureInfo.InvariantCulture))
                   .Append(" primary=").Append(Bool(monitor.IsPrimary))
                   .AppendLine();
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static string NotAvailable => LocalizationManager.Instance["CommonNotAvailable"];

    /// <summary>True when this process holds an administrator token.</summary>
    /// <remarks>
    /// Several failures this app can hit look identical from the outside and
    /// only make sense next to the integrity level: explorer running higher
    /// than this process silently drops every <c>LVM_*</c> message it is sent,
    /// so a report that says nothing about elevation cannot distinguish "the
    /// message was ignored" from "the message was never sent".
    /// </remarks>
    private static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A token that cannot be asked is not an administrator one for the
            // purposes of this report; losing the line would be worse than
            // answering it approximately.
            return false;
        }
    }

    private static string Bool(bool value) =>
        LocalizationManager.Instance[value ? "CommonYes" : "CommonNo"];

    private static string SourceName(DesktopPathSource source)
    {
        string key = source switch
        {
            DesktopPathSource.KnownFolder => "DiagnosticsSourceKnownFolder",
            DesktopPathSource.UserShellFolders => "DiagnosticsSourceUserShellFolders",
            DesktopPathSource.ShellFolders => "DiagnosticsSourceShellFolders",
            DesktopPathSource.UserProfile => "DiagnosticsSourceUserProfile",

            // AppRecorded is never produced by the detection service; if a
            // future source arrives before its string does, the enum name is a
            // better row label than a missing-translation marker.
            _ => string.Empty,
        };

        return key.Length == 0 ? source.ToString() : LocalizationManager.Instance[key];
    }

    private static void Section(StringBuilder builder, string heading) =>
        builder.AppendLine().Append("--- ").Append(heading).AppendLine(" ---");

    private static void Field(StringBuilder builder, string name, string? value) =>
        builder.Append(name)
               .Append(": ")
               .AppendLine(string.IsNullOrWhiteSpace(value) ? NotAvailable : value);

    /// <summary>
    /// Local time with its offset, then the same instant in UTC.
    /// </summary>
    /// <remarks>
    /// Invariant on purpose. Neither shape here is ambiguous, but the user's
    /// <see cref="CultureInfo.CurrentCulture"/> would render 09/18 and 18/09 for
    /// the same day depending on where the report was made, and a maintainer
    /// comparing two reports should not have to work out which is which.
    /// </remarks>
    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture) +
        " (" +
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) +
        ")";
}
