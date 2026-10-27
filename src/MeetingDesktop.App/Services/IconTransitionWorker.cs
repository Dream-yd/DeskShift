using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace MeetingDesktop.App.Services;

/// <summary>The helper owns the temporary icon style and restores it if its parent exits or disconnects.</summary>
internal static class IconTransitionWorker
{
    internal const string Argument = "--icon-transition-worker";

    internal static bool TryRun(string[] args, out int code)
    {
        code = 0;
        if (args.Length == 0 || args[0] != Argument) return false;
        code = 1;
        if (args.Length != 3 || !int.TryParse(args[1], out int parentId) ||
            !long.TryParse(args[2], out long started)) return true;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToUniversalTime().Ticks != started || parent.HasExited) return true;
            using var input = new StreamReader(Console.OpenStandardInput());
            using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var clock = Stopwatch.StartNew();
            int abandoned = 0;
            long leaseStarted = -1;
            using var state = new IconTransitionState(NativeIconOpacity.TryAcquireDesktop,
                () => Volatile.Read(ref abandoned) != 0);
            Task<string?> command = input.ReadLineAsync();
            // Record cancellation independently of Explorer's native style callbacks.
            // Native calls cannot be interrupted safely; as soon as one returns, the
            // state sees abandonment and restores instead of applying a late hide.
            using var monitor = new Timer(_ =>
            {
                try
                {
                    var pending = Volatile.Read(ref command);
                    long began = Interlocked.Read(ref leaseStarted);
                    if (parent.HasExited || pending.IsFaulted ||
                        (pending.IsCompletedSuccessfully && pending.Result is null) ||
                        (began >= 0 && clock.ElapsedMilliseconds - began >= 30000))
                        Interlocked.Exchange(ref abandoned, 1);
                }
                catch (InvalidOperationException) { Interlocked.Exchange(ref abandoned, 1); }
            }, null, 50, 50);
            output.WriteLine("ready");
            try
            {
                while (Volatile.Read(ref abandoned) == 0)
                {
                    // Commands wake immediately. While hidden, check for Explorer
                    // resetting the container's opacity during a folder rebind.
                    if (command.Wait(state.Animating ? 8 : state.Active ? 16 : 100))
                    {
                        string? line = command.GetAwaiter().GetResult();
                        if (line is null) break;
                        // Keep reading during native work so the monitor can notice EOF.
                        Volatile.Write(ref command, input.ReadLineAsync());
                        string[] parts = line.Split(' ');
                        if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "N", out _)) break;
                        bool accepted = true;
                        switch (parts[0])
                        {
                            case "begin":
                                Interlocked.Exchange(ref leaseStarted, clock.ElapsedMilliseconds);
                                accepted = state.Begin(parts[1], clock.ElapsedMilliseconds);
                                break;
                            case "hide": accepted = state.Hide(parts[1]); break;
                            case "end": state.End(parts[1], clock.ElapsedMilliseconds); break;
                            default: accepted = false; break;
                        }
                        output.WriteLine(accepted ? "ok" : "skip");
                    }
                    state.Tick(clock.ElapsedMilliseconds);
                    if (!state.Active) Interlocked.Exchange(ref leaseStarted, -1);
                }
            }
            finally
            {
                // A transient native failure must not discard the only owner of the
                // restoration. Keep the small helper until the window recovers or dies.
                do
                {
                    state.Dispose();
                    if (state.Active) Thread.Sleep(100);
                } while (state.Active);
            }
            code = 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or
            System.ComponentModel.Win32Exception or AggregateException) { }
        return true;
    }

    internal static ProcessStartInfo StartInfo()
    {
        using var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        return start;
    }
}

/// <summary>One prewarmed presentation helper. A cold or busy helper is skipped rather than delaying a switch.</summary>
internal sealed class IconTransitionConnection : IDisposable
{
    private static readonly object Sync = new();
    private static Task<IconTransitionConnection?>? _warm;
    private readonly Process _process;
    private readonly SemaphoreSlim _gate = new(1);
    private bool _disposed;

    private IconTransitionConnection(Process process) => _process = process;

    internal static void WarmUp()
    {
        // Tests and other hosts must never launch a desktop presentation helper.
        if (!string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "MeetingDesktop", StringComparison.OrdinalIgnoreCase)) return;
        lock (Sync)
        {
            if (_warm is { IsCompleted: false } || _warm is { IsCompletedSuccessfully: true, Result: { _disposed: false } }) return;
            _warm = Task.Run(async () =>
            {
                IconTransitionConnection? connection = null;
                try
                {
                    var process = Process.Start(IconTransitionWorker.StartInfo());
                    if (process is null) return null;
                    connection = new(process);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    if (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) == "ready") return connection;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
                connection?.Dispose();
                return null;
            });
        }
    }

    internal static IconTransitionConnection? Ready()
    {
        lock (Sync) return _warm is { IsCompletedSuccessfully: true } && _warm.Result is { _disposed: false } ready ? ready : null;
    }

    internal async Task<bool> SendAsync(string command, string id)
    {
        if (_disposed) return false;
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
        bool entered = false;
        try
        {
            await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
            entered = true;
            if (_disposed) return false;
            await _process.StandardInput.WriteLineAsync((command + " " + id).AsMemory(), budget.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(budget.Token).ConfigureAwait(false);
            return await _process.StandardOutput.ReadLineAsync(budget.Token).ConfigureAwait(false) == "ok";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        { Dispose(); return false; }
        finally { if (entered) _gate.Release(); }
    }

    internal static void Shutdown()
    {
        lock (Sync)
        {
            if (_warm is { } warming)
                _ = warming.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); }, TaskScheduler.Default);
            _warm = null;
        }
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Do not Kill: EOF lets the independent helper restore the icon layer first.
        try { _process.StandardInput.BaseStream.Dispose(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        _ = Task.Run(async () =>
        {
            try { await _process.WaitForExitAsync().ConfigureAwait(false); }
            finally { _process.Dispose(); }
        });
    }
}

internal sealed class LiveIconTransition : IDisposable
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly Func<string, string, Task<bool>>? _send;
    private readonly Task<bool>? _begin;
    private bool _disposed;

    public LiveIconTransition()
    {
        IconTransitionConnection.WarmUp();
        if (IconTransitionConnection.Ready() is { } connection)
        {
            _send = connection.SendAsync;
            _begin = _send("begin", _id);
        }
    }

    internal LiveIconTransition(Func<string, string, Task<bool>> send)
    { _send = send; _begin = _send("begin", _id); }

    public async Task HideAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed) return;
        // Begin already has the connection's 75 ms budget. An in-flight reply
        // must not turn a fast preparation into an unprotected folder change.
        // This waits for acknowledgement, never for the outgoing animation.
        if (_begin is null || !await _begin.WaitAsync(token).ConfigureAwait(false) || _disposed)
        { Dispose(); return; }
        await _send!("hide", _id).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_begin is not null) _ = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        await _begin!.ConfigureAwait(false);
        await _send!("end", _id).ConfigureAwait(false);
    }
}
