using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.App.Services;

/// <summary>Runs only fixed public-desktop operations and retains their authenticated reports.</summary>
public sealed class ElevationBroker : IElevationBroker
{
    public ElevatedOperationReport? LastReport { get; private set; }

    public ElevationOutcome Run(string argument)
    {
        LastReport = null;
        // Validate before launching or allocating IPC resources.
        _ = ElevatedOperations.BuildRelaunchInfo(argument);
        Process? process = null;
        try
        {
            using var channel = new ElevatedReportChannel();
            process = Process.Start(channel.BuildRelaunchInfo(argument));
            if (process is null) return ElevationOutcome.Failed;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            LastReport = channel.ReceiveAsync(process.Id, timeout.Token).GetAwaiter().GetResult();
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            return process.ExitCode == 0 && LastReport.Succeeded
                ? ElevationOutcome.Succeeded : ElevationOutcome.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ElevationOutcome.Declined;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or OperationCanceledException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return ElevationOutcome.Failed;
        }
        finally
        {
            // An IPC timeout is not proof that the privileged file move stopped.
            // Never let the workflow roll back while that helper can still mutate files.
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(); }
                        catch (Win32Exception) { /* A medium token may not terminate an elevated helper. */ }
                        catch (InvalidOperationException) { }
                        process.WaitForExit();
                    }
                }
                finally { process.Dispose(); }
            }
        }
    }
}
