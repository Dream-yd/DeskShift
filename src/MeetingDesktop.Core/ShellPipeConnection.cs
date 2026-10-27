using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MeetingDesktop.Core;

/// <summary>A private, same-user channel to the exact normal-privilege child we launched.</summary>
internal sealed class ShellPipeConnection : IShellWorkerConnection
{
    private const string Switch = "--desktop-shell-session";
    private const string Prefix = "MeetingDesktop.Shell.";
    private readonly NamedPipeServerStream pipe;
    private Process? helper;

    private ShellPipeConnection(string id) => pipe = new NamedPipeServerStream(Prefix + id,
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public bool IsAlive => helper is { HasExited: false } && pipe.IsConnected;

    internal static IShellWorkerConnection Open(CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        var connection = new ShellPipeConnection(id);
        try
        {
            token.ThrowIfCancellationRequested();
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, Assembly.GetEntryAssembly()!.GetName().Name + ".dll"));
            foreach (string argument in new[] { Switch, id, parent.Id.ToString(CultureInfo.InvariantCulture),
                parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
            connection.helper = Process.Start(start) ?? throw new IOException("The Shell helper did not start.");
            connection.pipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
            if (!GetNamedPipeClientProcessId(connection.pipe.SafePipeHandle, out uint peer) ||
                peer != connection.helper.Id || connection.helper.HasExited || !ElevatedReportChannel.IsSameProcessUser((int)peer))
                throw new IOException("Untrusted Shell helper.");
            var reply = connection.Send(new() { Operation = "hello-v1" }, token);
            if (!reply.Ready) throw new IOException("Invalid Shell handshake.");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public DesktopShellWorker.ShellResponse Send(DesktopShellWorker.ShellRequest request, CancellationToken token)
    {
        ShellWorkerProtocol.Write(pipe, request, token);
        return ShellWorkerProtocol.Read<DesktopShellWorker.ShellResponse>(pipe, token);
    }

    public void Dispose()
    {
        pipe.Dispose();
        if (helper is null) return;
        try
        {
            if (!helper.HasExited)
            {
                try { helper.Kill(); }
                catch (Win32Exception) { /* The helper's parent/disconnect watchdog is a fallback. */ }
                catch (InvalidOperationException) { }
                // Do not return while a timed-out setter could still run after rollback.
                helper.WaitForExit();
            }
        }
        finally { helper.Dispose(); helper = null; }
    }

    internal static bool TryRun(string[] arguments, out int exitCode)
    {
        exitCode = 0;
        if (!arguments.Contains(Switch, StringComparer.Ordinal)) return false;
        exitCode = 1;
        if (arguments.Length != 4 || arguments[0] != Switch ||
            !Guid.TryParseExact(arguments[1], "N", out Guid id) || id == Guid.Empty ||
            !int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId) || parentId <= 0 ||
            !long.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out long created) ||
            created <= 0 || created > DateTime.MaxValue.Ticks) return true;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (!ElevatedSession.MatchesParent(parent, created)) return true;
            using var client = new NamedPipeClientStream(".", Prefix + arguments[1], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                System.Security.Principal.TokenImpersonationLevel.Anonymous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            client.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            if (!ElevatedReportChannel.GetNamedPipeServerProcessId(client.SafePipeHandle, out uint peer) ||
                peer != parentId || !ElevatedSession.MatchesParent(parent, created)) return true;
            var lifetime = new SessionLifetime(() => !parent.HasExited,
                () => ElevatedSession.IsPipeConnected(client.SafePipeHandle), () => Environment.Exit(1));
            using var watchdog = new Timer(_ => lifetime.Check(), null, 250, 250);
            try
            {
                ShellWorkerProtocol.Serve(client, DesktopShellWorker.Execute, timeout.Token,
                    lifetime.HandshakeComplete, lifetime.OperationState);
                exitCode = 0;
            }
            finally { watchdog.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        catch (Exception ex) when (ElevatedSession.IsTransportFailure(ex) || ex is ArgumentException or System.Security.SecurityException) { }
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
