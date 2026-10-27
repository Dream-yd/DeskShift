using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MeetingDesktop.Core.Interop;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

/// <summary>
/// Stops and starts explorer.exe, and compares this process's integrity level
/// with explorer's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the integrity comparison exists.</b> Every message this app sends to
/// the desktop icon listview - <c>LVM_GETITEMPOSITION</c>,
/// <c>LVM_SETITEMPOSITION32</c> - is numbered above WM_USER (0x0400). User
/// Interface Privilege Isolation filters that range when the sender and the
/// target window do not share an integrity level, and it fails
/// <em>silently</em>: <c>SendMessageTimeout</c> returns 0, no Win32 error is
/// set, nothing is logged, and the icon does not move. The only symptom is a
/// layout that is most of the way applied, which points at the placement
/// algorithm rather than at privileges, so it gets debugged in the wrong
/// place. A desktop left in that state also stays degraded for the rest of the
/// session; signing out and back in is the only reliable recovery.
/// </para>
/// <para>
/// The rule that avoids all of it: <b>never start explorer from an elevated
/// process</b>. A process launched by an elevated parent inherits the elevated
/// token, so "just run the app as administrator once" leaves a shell this
/// process can never drive. Call
/// <see cref="CompareIntegrityWithExplorer"/> after starting explorer and let
/// the caller abort with an explanation, rather than proceeding to place icons
/// into a void.
/// </para>
/// <para>
/// Everything here is synchronous and blocking. The shell operations have no
/// cancellable form, so callers run them off the UI thread.
/// </para>
/// </remarks>
public static class ExplorerService
{
    /// <summary>
    /// Process name without the extension, which is the form
    /// <c>Process.GetProcessesByName</c> expects.
    /// </summary>
    private const string ExplorerProcessName = "explorer";

    /// <summary>
    /// Image to launch. Not path-qualified: the loader resolves it, so a shell
    /// installed somewhere unusual still starts.
    /// </summary>
    private const string ExplorerExecutable = "explorer.exe";

    /// <summary>ERROR_ACCESS_DENIED.</summary>
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// How long a launched explorer has to show up as a process.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. A cold start on a loaded machine - profile loaded,
    /// shell extensions initialised - regularly takes several seconds, and
    /// declaring failure early would send the caller into an error path for a
    /// shell that is in fact coming up.
    /// </remarks>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a terminated explorer has to disappear.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Poll interval while waiting for explorer to appear.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// True when at least one explorer.exe process exists.
    /// </summary>
    /// <remarks>
    /// Existence is not readiness. The desktop listview appears a second or
    /// two after the process does, so anything that needs to talk to the
    /// desktop goes through <see cref="DesktopGridView"/>, which retries,
    /// rather than treating this as a green light.
    /// </remarks>
    public static bool IsRunning()
    {
        Process[] processes = Process.GetProcessesByName(ExplorerProcessName);

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            DisposeAll(processes);
        }
    }

    /// <summary>
    /// Terminates every explorer.exe process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a terminate, not a polite close, and that is inherent to
    /// restarting the shell rather than an oversight here: <b>any File
    /// Explorer windows the user has open are closed with no save prompt and
    /// no chance to cancel</b>. A close request would be the alternative, and
    /// it is not usable - a modal dialog or a window that has stopped pumping
    /// its queue swallows it, and the caller is then left with a half-performed
    /// desktop swap instead of an error it can report. Callers own warning the
    /// user.
    /// </para>
    /// <para>
    /// Does nothing when explorer is not running.
    /// </para>
    /// </remarks>
    /// <exception cref="DesktopInteropException">
    /// <see cref="InteropFailureKind.AccessDenied"/> when a process cannot be
    /// terminated, which in practice means explorer is running at a higher
    /// integrity level than this process - the state documented on this class.
    /// </exception>
    public static void StopAll()
    {
        Process[] processes = Process.GetProcessesByName(ExplorerProcessName);

        try
        {
            foreach (Process process in processes)
            {
                Terminate(process);
                WaitUntilGone(process);
            }
        }
        finally
        {
            DisposeAll(processes);
        }
    }

    /// <summary>
    /// Starts explorer.exe and waits for it to appear as a process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this with no shell running. Launching explorer.exe while the shell
    /// is already up does not produce a second shell - it opens a File
    /// Explorer window on top of the desktop - so the intended entry point is
    /// <see cref="StopThenStart"/>, or <see cref="StopAll"/> first.
    /// </para>
    /// <para>
    /// <c>UseShellExecute = false</c> because with ShellExecute the request can
    /// be handed to an already-running shell and <c>Process.Start</c> returns
    /// null instead of a process, leaving nothing to observe. The returned
    /// process is disposed immediately and polled for by name instead: the
    /// launched image can hand off and exit, so its own lifetime says nothing
    /// about whether a shell is up.
    /// </para>
    /// <para>
    /// Returning means the process exists, not that the desktop is ready.
    /// </para>
    /// </remarks>
    /// <exception cref="DesktopInteropException">
    /// <see cref="InteropFailureKind.ExplorerNotRunning"/> when explorer could
    /// not be launched or did not appear within the timeout.
    /// </exception>
    public static void Start()
    {
        var startInfo = new ProcessStartInfo(ExplorerExecutable)
        {
            UseShellExecute = false,
        };

        try
        {
            using Process? launched = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            throw new DesktopInteropException(
                InteropFailureKind.ExplorerNotRunning,
                $"Could not launch {ExplorerExecutable} (Win32 error {ex.NativeErrorCode}).",
                win32ErrorCode: ex.NativeErrorCode,
                innerException: ex);
        }

        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < StartTimeout)
        {
            if (IsRunning())
            {
                return;
            }

            Thread.Sleep(PollInterval);
        }

        throw new DesktopInteropException(
            InteropFailureKind.ExplorerNotRunning,
            $"{ExplorerExecutable} did not appear as a process within {StartTimeout.TotalSeconds:0} seconds of being launched.");
    }

    /// <summary>
    /// Restarts the shell: terminates every explorer.exe process, then starts
    /// one.
    /// </summary>
    /// <remarks>
    /// The desktop listview does not exist the moment this returns - explorer
    /// needs a moment to build the desktop - so callers must go through
    /// <see cref="DesktopGridView"/>'s retry rather than assuming the view is
    /// there.
    /// </remarks>
    public static void StopThenStart()
    {
        StopAll();
        Start();
    }

    /// <summary>
    /// Compares this process's token integrity level with that of the explorer
    /// process that owns the desktop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PrivilegeComparison.Unknown"/> is returned rather than an
    /// exception when either side cannot be read. The caller's decision is
    /// "abort or continue", and an unreadable token is a reason to stop and
    /// report, not a reason to crash - throwing would turn a check that exists
    /// to make a failure legible into one more failure mode.
    /// </para>
    /// <para>
    /// The comparison is against the desktop's own explorer process, not
    /// "the first explorer.exe found": an elevated File Explorer window behind
    /// "Run as administrator" is a second explorer process at a different
    /// integrity level, and measuring that one answers a question nobody
    /// asked.
    /// </para>
    /// </remarks>
    public static PrivilegeComparison CompareIntegrityWithExplorer()
    {
        if (!TryGetDesktopExplorerProcessId(out uint explorerProcessId) ||
            !TryReadProcessIntegrityRid(explorerProcessId, out int explorerRid) ||
            !TryReadProcessIntegrityRid((uint)Environment.ProcessId, out int ownRid))
        {
            return PrivilegeComparison.Unknown;
        }

        if (ownRid == explorerRid)
        {
            return PrivilegeComparison.Same;
        }

        return ownRid > explorerRid
            ? PrivilegeComparison.ExplorerLower
            : PrivilegeComparison.ExplorerHigher;
    }

    /// <summary>
    /// Kills one explorer process, treating an already-exited one as success.
    /// </summary>
    /// <remarks>
    /// Terminating a process that has already exited fails with
    /// ERROR_ACCESS_DENIED rather than quietly doing nothing, so "already gone"
    /// has to be told apart from "refused". Without that, a shell that exited
    /// on its own during the enumeration is reported as an access-denied
    /// failure and the caller aborts a swap that was fine.
    /// </remarks>
    private static void Terminate(Process process)
    {
        uint processId = (uint)process.Id;

        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            if (SafeHasExited(process))
            {
                return;
            }

            Win32Exception? failure = ex as Win32Exception;

            throw new DesktopInteropException(
                InteropFailureKind.AccessDenied,
                $"Terminating explorer pid {processId} failed " +
                (failure is null ? $"({ex.GetType().Name})." : $"(Win32 error {failure.NativeErrorCode}).") +
                " The usual cause is explorer running at a higher integrity level than this process, " +
                "which is also the state that silently blocks listview messages.",
                win32ErrorCode: failure?.NativeErrorCode,
                processId: processId,
                innerException: ex);
        }
    }

    /// <summary>
    /// Waits for a terminated process to disappear, without ever blocking
    /// indefinitely.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. A terminate request is accepted immediately, so a
    /// process that outlives this window is one that cannot be stopped at all,
    /// and waiting longer would only stall the caller. The next step
    /// re-enumerates anyway.
    /// </remarks>
    private static void WaitUntilGone(Process process)
    {
        try
        {
            process.WaitForExit((int)StopTimeout.TotalMilliseconds);
        }
        catch (InvalidOperationException)
        {
            // No process behind the object, so there is nothing to wait for.
        }
    }

    /// <summary>
    /// Whether a process object reports that it has exited.
    /// </summary>
    /// <remarks>
    /// An object with no process behind it counts as gone; a process whose
    /// state cannot be read counts as alive, so the refusal is reported rather
    /// than swallowed.
    /// </remarks>
    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the explorer process that owns the desktop icon listview.
    /// </summary>
    /// <remarks>
    /// Falls back to any explorer process when the listview has not been built
    /// yet - which is the normal state for a second or two after
    /// <see cref="Start"/>, and a comparison is better than Unknown there,
    /// since every explorer process in the session shares one token integrity
    /// level.
    /// </remarks>
    private static bool TryGetDesktopExplorerProcessId(out uint processId)
    {
        processId = 0;

        DiagnosticEntry? desktopView = DesktopGridView.Diagnostics()
            .FirstOrDefault(entry => entry.UnderDesktopHost && entry.ClassName == "SysListView32");

        if (desktopView is not null)
        {
            processId = desktopView.ProcessId;
            return true;
        }

        Process[] processes = Process.GetProcessesByName(ExplorerProcessName);

        try
        {
            if (processes.Length == 0)
            {
                return false;
            }

            processId = (uint)processes[0].Id;
            return true;
        }
        finally
        {
            DisposeAll(processes);
        }
    }

    /// <summary>
    /// Reads a process token's integrity level as a raw RID.
    /// </summary>
    /// <returns>False when the process or its token cannot be opened.</returns>
    private static bool TryReadProcessIntegrityRid(uint processId, out int rid)
    {
        rid = 0;

        // PROCESS_QUERY_INFORMATION is all OpenProcessToken needs. Asking for
        // more would fail exactly where this check matters most - against a
        // process at a higher integrity level - and turn a measurable answer
        // into Unknown.
        nint process = NativeMethods.OpenProcess(
            NativeMethods.ProcessAccess.QueryInformation,
            inheritHandle: false,
            processId);

        if (process == nint.Zero)
        {
            return false;
        }

        try
        {
            return TryReadTokenIntegrityRid(process, out rid);
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>
    /// Reads the last sub-authority of the token's integrity SID.
    /// </summary>
    /// <remarks>
    /// The integrity level is that sub-authority's value: 0x1000 low, 0x2000
    /// medium, 0x3000 high, 0x4000 system, 0x5000 protected. The values are
    /// ordered, so the levels can be compared as plain integers instead of
    /// against a table that would go stale the next time Windows adds one.
    /// </remarks>
    private static bool TryReadTokenIntegrityRid(nint process, out int rid)
    {
        rid = 0;

        if (!AdvApi32Methods.OpenProcessToken(process, AdvApi32Methods.TokenQuery, out nint token))
        {
            return false;
        }

        try
        {
            // Sizing call. It is expected to fail with ERROR_INSUFFICIENT_BUFFER
            // and only the length it reports is used, so the return value is
            // deliberately ignored.
            AdvApi32Methods.GetTokenInformation(
                token,
                AdvApi32Methods.TokenIntegrityLevel,
                nint.Zero,
                0,
                out int size);

            if (size <= 0)
            {
                return false;
            }

            var buffer = new byte[size];
            GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            try
            {
                if (!AdvApi32Methods.GetTokenInformation(
                        token,
                        AdvApi32Methods.TokenIntegrityLevel,
                        pinned.AddrOfPinnedObject(),
                        size,
                        out _))
                {
                    return false;
                }

                // The buffer holds a TOKEN_MANDATORY_LABEL, which opens with a
                // SID_AND_ATTRIBUTES, so the Sid pointer is the first
                // pointer-sized field.
                nint sid = Marshal.ReadIntPtr(pinned.AddrOfPinnedObject());
                if (sid == nint.Zero)
                {
                    return false;
                }

                nint countPointer = AdvApi32Methods.GetSidSubAuthorityCount(sid);
                if (countPointer == nint.Zero)
                {
                    return false;
                }

                byte count = Marshal.ReadByte(countPointer);
                if (count == 0)
                {
                    return false;
                }

                nint ridPointer = AdvApi32Methods.GetSidSubAuthority(sid, (uint)(count - 1));
                if (ridPointer == nint.Zero)
                {
                    return false;
                }

                rid = Marshal.ReadInt32(ridPointer);
                return true;
            }
            finally
            {
                pinned.Free();
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    /// <summary>
    /// Releases the handles the process objects hold.
    /// </summary>
    private static void DisposeAll(Process[] processes)
    {
        foreach (Process process in processes)
        {
            process.Dispose();
        }
    }
}

/// <summary>
/// How this process's integrity level compares with explorer's.
/// </summary>
/// <remarks>
/// The axis is the one that decides whether cross-process listview messages
/// survive User Interface Privilege Isolation. See the remarks on
/// <see cref="ExplorerService"/> for why that matters.
/// </remarks>
public enum PrivilegeComparison
{
    /// <summary>
    /// One of the two tokens could not be read, so the levels cannot be
    /// compared. Callers should treat this as "cannot prove it is safe" rather
    /// than as a pass.
    /// </summary>
    Unknown,

    /// <summary>
    /// Both processes run at the same integrity level. Listview messages are
    /// not filtered.
    /// </summary>
    Same,

    /// <summary>
    /// explorer sits above this process - normally because explorer was started
    /// by an elevated process. UIPI drops every message above WM_USER with no
    /// error and no return value to inspect, and the desktop stays degraded for
    /// the rest of the session.
    /// </summary>
    ExplorerHigher,

    /// <summary>
    /// explorer sits below this process, which means this process is the
    /// elevated one. Messages pass in this direction, but the elevated process
    /// is what will start the next explorer instance, so this is the state to
    /// back out of rather than a clean bill of health.
    /// </summary>
    ExplorerLower,
}
