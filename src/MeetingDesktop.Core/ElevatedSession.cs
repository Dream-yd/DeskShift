using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using MeetingDesktop.Core.Interop;
using Microsoft.Win32.SafeHandles;

namespace MeetingDesktop.Core;

/// <summary>Dispatch before GUI startup. A recognized but invalid invocation is handled with exit code 1.</summary>
public static class ElevatedSession
{
    public const string HelperSwitch = "--elevated-session";
    internal const string PipePrefix = "MeetingDesktop.Session.";
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args is null || !args.Contains(HelperSwitch, StringComparer.Ordinal)) return false;
        exitCode = 1;
        // Reject every caller-supplied path, command, extra argument and noncanonical number before opening IPC.
        if (args.Length != 4 || args[0] != HelperSwitch ||
            !Guid.TryParseExact(args[1], "N", out Guid id) || id == Guid.Empty ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId) || parentId <= 0 ||
            !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out long created) ||
            created <= 0 || created > DateTime.MaxValue.Ticks) return true;

        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (!MatchesParent(parent, created) || !IsProcessElevated(Environment.ProcessId)) return true;
            using var pipe = new NamedPipeClientStream(".", PipePrefix + args[1], PipeDirection.InOut,
                // CurrentUserOnly on a Windows *client* compares token OWNER, which changes under UAC.
                // The GUI server restricts its ACL; here authenticate its kernel PID and account USER SID.
                PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Anonymous);
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            if (!ElevatedReportChannel.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint peer) ||
                peer != parentId || !MatchesParent(parent, created)) return true;

            // Only this real helper entry point installs a process-exit watchdog. Unit tests use the protocol
            // with fake executors, never this exit action or PublicDesktopService.
            var lifetime = new SessionLifetime(() => !parent.HasExited,
                () => IsPipeConnected(pipe.SafePipeHandle), () => Environment.Exit(1));
            using var watchdog = new Timer(_ => lifetime.Check(), null, 250, 250);
            try
            {
                SessionProtocol.ServeAsync(pipe, ElevatedOperations.Execute, lifetime.OperationState,
                    timeout.Token, lifetime.HandshakeComplete).GetAwaiter().GetResult();
                exitCode = 0;
            }
            finally
            {
                // Wait for in-flight callbacks before releasing either handle.
                watchdog.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (IsTransportFailure(ex) || ex is ArgumentException or System.Security.SecurityException)
        { /* Fail closed without starting the GUI or touching files. */ }
        return true;
    }

    internal static bool MatchesParent(Process parent, long created) => !parent.HasExited &&
        parent.StartTime.ToUniversalTime().Ticks == created && ElevatedReportChannel.IsSameProcessUser(parent.Id);

    internal static bool IsTransportFailure(Exception ex) => ex is IOException or Win32Exception or
        InvalidOperationException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException;

    internal static bool IsProcessElevated(int processId)
    {
        nint process = NativeMethods.OpenProcess(0x1000, false, (uint)processId);
        if (process == 0) return false;
        nint token = 0;
        try
        {
            return AdvApi32Methods.OpenProcessToken(process, AdvApi32Methods.TokenQuery, out token) &&
                GetTokenInformation(token, 20 /* TokenElevation */, out int elevated, sizeof(int), out int size) &&
                size == sizeof(int) && elevated == 1;
        }
        finally
        {
            if (token != 0) NativeMethods.CloseHandle(token);
            NativeMethods.CloseHandle(process);
        }
    }

    internal static bool IsPipeConnected(SafePipeHandle pipe) =>
        PeekNamedPipe(pipe, 0, 0, 0, out _, 0);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int informationClass,
        out int information, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, nint buffer, uint bufferSize,
        nint bytesRead, out uint available, nint bytesLeft);
}

internal enum SessionCommand : byte { Hello = 0xA1, Ping = 2, Move = 3, Restore = 4, Close = 5 }

internal static class SessionProtocol
{
    // Hello encodes protocol version 1. Commands have exactly one byte; reports reuse the bounded report bridge.
    internal static async Task ServeAsync(Stream stream, Func<string, ElevatedOperationReport> execute,
        Action<bool>? operationState = null, CancellationToken token = default, Action? handshakeComplete = null)
    {
        if (await ReadCommandAsync(stream, token).ConfigureAwait(false) != SessionCommand.Hello)
            throw new IOException("Session handshake required.");
        await WriteCommandAsync(stream, SessionCommand.Hello, token).ConfigureAwait(false);
        handshakeComplete?.Invoke();
        // The startup token only bounds the handshake. Idle sessions live until Close/disconnect/parent exit.
        while (true)
        {
            SessionCommand command = await ReadCommandAsync(stream).ConfigureAwait(false);
            switch (command)
            {
                case SessionCommand.Ping:
                case SessionCommand.Close:
                    await WriteCommandAsync(stream, command).ConfigureAwait(false);
                    if (command == SessionCommand.Close) return;
                    break;
                case SessionCommand.Move:
                case SessionCommand.Restore:
                    operationState?.Invoke(true);
                    try
                    {
                        ElevatedOperationReport report = execute(command == SessionCommand.Move
                            ? ElevatedOperations.MoveShortcutsAside : ElevatedOperations.RestoreShortcuts);
                        await ElevatedReportChannel.WriteAsync(stream, report, default).ConfigureAwait(false);
                    }
                    finally { operationState?.Invoke(false); }
                    break;
                default: throw new IOException("Unexpected session command.");
            }
        }
    }

    internal static async Task WriteCommandAsync(Stream stream, SessionCommand command, CancellationToken token = default)
    {
        if (!Enum.IsDefined(command)) throw new IOException("Unknown session command.");
        await stream.WriteAsync(new byte[] { 1, 0, 0, 0, (byte)command }, token).ConfigureAwait(false);
    }

    internal static async Task<SessionCommand> ReadCommandAsync(Stream stream, CancellationToken token = default)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadInt32LittleEndian(header) != 1) throw new IOException("Invalid command size.");
        byte[] payload = new byte[1];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        var command = (SessionCommand)payload[0];
        if (!Enum.IsDefined(command)) throw new IOException("Unknown session command.");
        return command;
    }
}

/// <summary>Independent watchdog: even a stuck synchronous file operation must not leave an admin worker alive.</summary>
internal sealed class SessionLifetime(Func<bool> parentAlive, Func<bool> connected, Action terminate,
    Func<long>? clock = null)
{
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private long deadline = (clock?.Invoke() ?? Environment.TickCount64) + (long)ElevatedSession.ConnectTimeout.TotalMilliseconds;
    internal void HandshakeComplete() => Volatile.Write(ref deadline, long.MaxValue);
    internal void OperationState(bool active) => Volatile.Write(ref deadline,
        active ? now() + (long)ElevatedSession.OperationTimeout.TotalMilliseconds : long.MaxValue);
    internal void Check()
    {
        try
        {
            if (now() < Volatile.Read(ref deadline) && parentAlive() && connected()) return;
        }
        catch (Exception ex) when (ElevatedSession.IsTransportFailure(ex)) { }
        terminate();
    }
}

/// <summary>The normal-privilege app owns this single-use pipe and the exact process launched with runas.</summary>
internal sealed class ElevatedSessionClient : IDisposable
{
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream pipe;
    private readonly object stopGate = new();
    private Process? helper;
    private int stopped;
    internal event EventHandler? Closed;

    internal ElevatedSessionClient()
    {
        pipe = new NamedPipeServerStream(ElevatedSession.PipePrefix + id, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    internal bool IsAlive
    {
        get
        {
            try { return Volatile.Read(ref stopped) == 0 && helper is { HasExited: false } && pipe.IsConnected; }
            catch (InvalidOperationException) { return false; }
        }
    }

    internal ProcessStartInfo BuildRelaunchInfo()
    {
        using var parent = Process.GetCurrentProcess();
        var info = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable."),
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        };
        info.ArgumentList.Add(ElevatedSession.HelperSwitch);
        info.ArgumentList.Add(id);
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        return info;
    }

    internal async Task OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Process.Start can remain in the OS consent dialog. Cancellation is applied immediately on return;
        // we do not detach that launch task and lose the handle to a late-arriving elevated helper.
        helper = Process.Start(BuildRelaunchInfo()) ?? throw new IOException("Helper did not start.");
        helper.Exited += HelperExited;
        helper.EnableRaisingEvents = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ElevatedSession.ConnectTimeout);
        try { await AuthenticateAsync(helper, timeout.Token).ConfigureAwait(false); }
        catch { Dispose(); throw; }
    }

    internal async Task AuthenticateAsync(Process expectedHelper, CancellationToken token)
    {
        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint actual) || actual != expectedHelper.Id ||
            expectedHelper.HasExited || !ElevatedReportChannel.IsSameProcessUser(expectedHelper.Id) ||
            !ElevatedSession.IsProcessElevated(expectedHelper.Id))
            throw new IOException("Untrusted session helper.");
        await SessionProtocol.WriteCommandAsync(pipe, SessionCommand.Hello, token).ConfigureAwait(false);
        if (await SessionProtocol.ReadCommandAsync(pipe, token).ConfigureAwait(false) != SessionCommand.Hello)
            throw new IOException("Invalid session handshake.");
        await SessionProtocol.WriteCommandAsync(pipe, SessionCommand.Ping, token).ConfigureAwait(false);
        if (await SessionProtocol.ReadCommandAsync(pipe, token).ConfigureAwait(false) != SessionCommand.Ping)
            throw new IOException("Session helper did not respond.");
    }

    internal ElevatedOperationReport Run(string argument)
    {
        if (!ElevatedOperations.IsOperation(argument)) throw new ArgumentException("Unknown elevated operation.", nameof(argument));
        using var timeout = new CancellationTokenSource(ElevatedSession.OperationTimeout + TimeSpan.FromSeconds(5));
        try
        {
            SessionProtocol.WriteCommandAsync(pipe, argument == ElevatedOperations.MoveShortcutsAside
                ? SessionCommand.Move : SessionCommand.Restore, timeout.Token).GetAwaiter().GetResult();
            return ElevatedReportChannel.ReadAsync(pipe, timeout.Token).GetAwaiter().GetResult();
        }
        catch { Dispose(); throw; }
    }

    private void HelperExited(object? sender, EventArgs e) =>
        // Never block Process.WaitForExit's own event-drain on broker cleanup.
        ThreadPool.QueueUserWorkItem(_ => Closed?.Invoke(this, EventArgs.Empty));

    public void Dispose()
    {
        // All concurrent callers wait for the same completion, including a Run racing Revoke.
        lock (stopGate)
        {
            if (stopped != 0) return;
            Volatile.Write(ref stopped, 1);
            pipe.Dispose();
            if (helper is null) return;
            try
            {
                helper.Exited -= HelperExited;
                if (!helper.WaitForExit(1000))
                {
                    try { helper.Kill(); }
                    catch (Win32Exception) { /* Medium-token terminate permission is not assumed. */ }
                    catch (InvalidOperationException) { }
                    // A timeout is not completion. The helper watchdog closes it; never race a rollback.
                    helper.WaitForExit();
                }
            }
            finally { helper.Dispose(); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
