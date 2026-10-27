using System.Security.Principal;
using MeetingDesktop.Core.Interop;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MeetingDesktop.Core;

/// <summary>One fixed public-desktop operation, including partial progress on failure.</summary>
public sealed record ElevatedOperationReport(bool Succeeded, ShortcutMoveResult Result,
    int RemainingCount = 0, string? FailedPath = null);

public static class ElevatedOperations
{
    public const string MoveShortcutsAside = "--elevated-move-shortcuts-aside";
    public const string RestoreShortcuts = "--elevated-restore-shortcuts";
    internal const string ReportSwitch = "--report-pipe";

    internal static bool IsOperation(string? argument) => argument is MoveShortcutsAside or RestoreShortcuts;

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args is null || args.Length == 0 || !IsOperation(args[0])) return false;
        if (args.Length == 1)
        {
            // Preserve the original single-token helper contract.
            exitCode = Execute(args[0]).Succeeded ? 0 : 1;
            return true;
        }
        if (args.Length != 4 || args[1] != ReportSwitch ||
            !Guid.TryParseExact(args[2], "N", out _) ||
            !int.TryParse(args[3], out int parentId) || parentId <= 0) return false;

        exitCode = 1;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var pipe = new NamedPipeClientStream(".", ElevatedReportChannel.Prefix + args[2],
                PipeDirection.InOut, PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Anonymous);
            pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            if (!ElevatedReportChannel.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actual) || actual != parentId ||
                !ElevatedReportChannel.IsSameProcessUser(parentId))
                return true;
            byte[] ready = new byte[1];
            pipe.ReadExactlyAsync(ready, timeout.Token).AsTask().GetAwaiter().GetResult();
            if (ready[0] != 1) return true;
            // No paths or actions are received over the pipe. Authenticate before touching files.
            ElevatedOperationReport report = Execute(args[0]);
            ElevatedReportChannel.WriteAsync(pipe, report, timeout.Token).GetAwaiter().GetResult();
            exitCode = report.Succeeded ? 0 : 1;
        }
        catch (Exception) { /* Silent, fail closed. */ }
        return true;
    }

    public static ProcessStartInfo BuildRelaunchInfo(string argument)
    {
        if (!IsOperation(argument)) throw new ArgumentException("Unknown elevated operation.", nameof(argument));
        return new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable."),
            Arguments = argument,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
    }

    internal static ElevatedOperationReport Execute(string operation)
    {
        try
        {
            return new(true, operation == MoveShortcutsAside
                ? PublicDesktopService.MoveShortcutsAsideDetailed()
                : PublicDesktopService.RestoreShortcutsDetailed());
        }
        catch (PublicDesktopMoveException ex)
        {
            return new(false, new(ex.MovedCount, ex.CompletedConflicts), ex.RemainingCount, ex.FailedPath);
        }
        catch (Exception) { return new(false, new(0, [])); }
    }
}

/// <summary>
/// Per-request, per-user report channel. Both ends check the kernel-reported peer PID.
/// Different-account UAC is deliberately rejected before the operation. No file report,
/// caller-supplied filesystem paths, or privileged commands are accepted.
/// </summary>
public sealed class ElevatedReportChannel : IDisposable
{
    internal const string Prefix = "MeetingDesktop.Report.";
    internal const int MaximumBytes = 1024 * 1024;
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream pipe;

    public ElevatedReportChannel()
    {
        pipe = new NamedPipeServerStream(Prefix + id, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public ProcessStartInfo BuildRelaunchInfo(string argument)
    {
        var info = ElevatedOperations.BuildRelaunchInfo(argument);
        info.Arguments += $" {ElevatedOperations.ReportSwitch} {id} {Environment.ProcessId}";
        return info;
    }

    public async Task<ElevatedOperationReport> ReceiveAsync(int helperProcessId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint actual) || actual != helperProcessId)
            throw new IOException("Unexpected report sender.");
        await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
        return await ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Checks the account SID, not the token owner which changes under UAC.</summary>
    internal static bool IsSameProcessUser(int processId)
    {
        nint process = NativeMethods.OpenProcess(0x1000, false, (uint)processId);
        if (process == 0) return false;
        nint token = 0;
        try
        {
            if (!AdvApi32Methods.OpenProcessToken(process, AdvApi32Methods.TokenQuery, out token)) return false;
            using var peer = new WindowsIdentity(token);
            using var current = WindowsIdentity.GetCurrent();
            return current.User is { } currentSid && peer.User is { } peerSid && currentSid.Equals(peerSid);
        }
        catch (System.Security.SecurityException) { return false; }
        finally
        {
            if (token != 0) NativeMethods.CloseHandle(token);
            NativeMethods.CloseHandle(process);
        }
    }

    internal static async Task WriteAsync(Stream stream, ElevatedOperationReport report, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(report);
        if (payload.Length > MaximumBytes) throw new IOException("Report too large.");
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }

    internal static async Task<ElevatedOperationReport> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumBytes) throw new IOException("Invalid report size.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        var report = JsonSerializer.Deserialize<ElevatedOperationReport>(payload)
            ?? throw new IOException("Missing report.");
        if (report.Result is null || report.Result.MovedCount < 0 || report.RemainingCount < 0 ||
            report.Result.Conflicts is null || report.Result.Conflicts.Any(c => c is null ||
                c.SourcePath is null || c.RequestedPath is null || c.PreservedPath is null))
            throw new IOException("Invalid report.");
        return report;
    }

    public void Dispose() => pipe.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
