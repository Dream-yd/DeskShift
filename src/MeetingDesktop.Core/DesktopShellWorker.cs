using System.Text.Json;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

/// <summary>Runs Shell COM calls in an STA helper that can be stopped without ending Explorer.</summary>
public static class DesktopShellWorker
{
    private const string Switch = "--desktop-shell-worker";
    private static readonly ShellWorkerSession Session = new(ShellPipeConnection.Open);
    private static readonly ShellWorkerSession Verifier = new(ShellPipeConnection.Open);

    /// <summary>Starts normal-privilege helpers without reading or changing the desktop.</summary>
    public static Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.WhenAll(
        Session.WarmUpAsync(cancellationToken), Verifier.WarmUpAsync(cancellationToken));

    /// <summary>Stops owned helpers, including a launch still in progress.</summary>
    public static void Shutdown() { Session.Dispose(); Verifier.Dispose(); }

    public static bool TryRun(string[] arguments, out int exitCode)
    {
        exitCode = 0;
        if (ShellPipeConnection.TryRun(arguments, out exitCode)) return true;
        if (arguments.Length == 0 || arguments[0] != Switch) return false;
        if (arguments.Length != 3) { exitCode = 2; return true; }
        ShellResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<ShellRequest>(File.ReadAllText(arguments[1]))
                ?? throw new InvalidOperationException("Empty Shell request.");
            response = request.Operation switch
            {
                "capture" => new() { Icons = ShellDesktopService.Capture() },
                "switch" => ExecuteSwitch(request.DesktopPath),
                "place" => new() { Placement = ShellDesktopService.Place(request.Icons) },
                _ => throw new InvalidOperationException("Unknown Shell operation."),
            };
        }
        catch (Exception ex)
        {
            response = DescribeFailure(ex);
            exitCode = 1;
        }
        try
        {
            string temporary = arguments[2] + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(response));
            File.Move(temporary, arguments[2], overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exitCode = 3;
        }
        return true;
    }

    public static IReadOnlyList<IconPosition> Capture(CancellationToken cancellationToken = default) =>
        Invoke(new() { Operation = "capture" }, cancellationToken).Icons
        ?? throw new InvalidOperationException("The Shell helper returned no icon snapshot.");

    public static IReadOnlyList<IconPosition> SwitchTo(string path, CancellationToken cancellationToken = default)
        => SwitchTo(path, Invoke, (request, token) => Verifier.Invoke(request, token, retire: true),
            () => { _ = WarmVerifierAsync(); }, cancellationToken);

    internal static IReadOnlyList<IconPosition> SwitchTo(string path,
        Func<ShellRequest, CancellationToken, ShellResponse> switchView,
        Func<ShellRequest, CancellationToken, ShellResponse> verify,
        Action warmVerifier, CancellationToken cancellationToken = default, TimeSpan? operationTimeout = null,
        TimeProvider? timeProvider = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan limit = operationTimeout ?? TimeSpan.FromSeconds(20);
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        long started = clock.GetTimestamp();
        deadline.CancelAfter(limit);
        var token = deadline.Token;
        void CheckDeadline()
        {
            cancellationToken.ThrowIfCancellationRequested();
            // CancelAfter callbacks share the thread pool. A delayed callback
            // cannot make an already-late reply valid.
            if (clock.GetElapsedTime(started) >= limit) deadline.Cancel();
            token.ThrowIfCancellationRequested();
        }
        try
        {
            CheckDeadline();
            var verified = switchView(new() { Operation = "switch", DesktopPath = path }, token).Icons
                ?? throw new InvalidOperationException("The Shell helper returned no switch verification.");
            // The verifier has only completed IPC handshakes, never a Shell read.
            // Retiring it preserves a fresh Known Folder cache on every switch.
            try
            {
                CheckDeadline();
                var response = verify(new() { Operation = "verify", DesktopPath = path }, token);
                CheckDeadline();
                return VerifiedIcons(response, verified);
            }
            finally { warmVerifier(); }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new DesktopViewVerificationException(
                $"Switching and independent verification exceeded their shared {limit.TotalSeconds:g}-second deadline. Explorer was not restarted.", ex);
        }
    }

    internal static IReadOnlyList<IconPosition> VerifiedIcons(ShellResponse response, IReadOnlyList<IconPosition> expected)
    {
        ValidateResponse(response, 0);
        if (!response.TargetVerified || response.Icons is not { } current ||
            !expected.Select(i => i.Identity).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(current.Select(i => i.Identity).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new DesktopViewVerificationException("The desktop view did not remain on the target after switching.");
        return current;
    }

    public static PlaceResult Place(IReadOnlyList<IconPosition> icons, CancellationToken cancellationToken = default) =>
        Invoke(new() { Operation = "place", Icons = icons }, cancellationToken).Placement
        ?? throw new InvalidOperationException("The Shell helper returned no layout result.");

    private static ShellResponse ExecuteSwitch(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The target desktop path is empty.");
        return new() { Icons = ShellDesktopService.SwitchAndCapture(path) };
    }

    private static async Task WarmVerifierAsync()
    {
        try { await Verifier.WarmUpAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ElevatedSession.IsTransportFailure(ex) || ex is TimeoutException) { }
    }

    internal static ShellResponse Execute(ShellRequest request)
    {
        try
        {
            return request.Operation switch
            {
                "capture" => new() { Icons = ShellDesktopService.Capture() },
                "switch" => ExecuteSwitch(request.DesktopPath),
                "verify" => ExecuteVerification(request.DesktopPath),
                "place" => new() { Placement = ShellDesktopService.Place(request.Icons) },
                _ => throw new InvalidOperationException("Unknown Shell operation."),
            };
        }
        catch (Exception ex) { return DescribeFailure(ex); }
    }

    private static ShellResponse ExecuteVerification(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The target desktop path is empty.");
        var icons = ShellDesktopService.Capture();
        if (!ShellDesktopService.SnapshotMatchesTarget(path, icons))
            throw new DesktopViewVerificationException("The independent desktop view did not match the target folder.");
        return new() { Icons = icons, TargetVerified = true };
    }

    private static ShellResponse Invoke(ShellRequest request, CancellationToken cancellationToken)
    {
        ShellResponse response = Session.Invoke(request, cancellationToken);
        ValidateResponse(response, 0);
        return response;
    }

    internal static ShellResponse DescribeFailure(Exception error) => new()
    {
        Error = error.Message,
        HResult = error.HResult,
        ViewVerificationFailed = error is DesktopViewVerificationException,
    };

    internal static Exception HelperTimeout(string operation) => operation is "switch" or "verify"
        ? new DesktopViewVerificationException("The Shell helper exceeded 20 seconds before the target desktop could be verified.")
        : new TimeoutException("The Shell helper exceeded 20 seconds. Explorer was not restarted.");

    internal static void ValidateResponse(ShellResponse response, int exitCode)
    {
        if (response.Error is not null)
        {
            string message = $"Shell operation failed: {response.Error} (0x{response.HResult:X8}).";
            if (response.ViewVerificationFailed) throw new DesktopViewVerificationException(message);
            throw new InvalidOperationException(message);
        }
        if (response.ViewVerificationFailed || exitCode != 0)
            throw new InvalidOperationException("The Shell helper did not finish successfully.");
    }

    internal sealed record ShellRequest
    {
        public required string Operation { get; init; }
        public string? DesktopPath { get; init; }
        public IReadOnlyList<IconPosition> Icons { get; init; } = [];
    }

    internal sealed record ShellResponse
    {
        public bool Ready { get; init; }
        public bool TargetVerified { get; init; }
        public IReadOnlyList<IconPosition>? Icons { get; init; }
        public PlaceResult? Placement { get; init; }
        public string? Error { get; init; }
        public int HResult { get; init; }
        public bool ViewVerificationFailed { get; init; }
    }
}
