using System.Buffers.Binary;
using System.Text.Json;

namespace MeetingDesktop.Core;

internal static class ShellWorkerProtocol
{
    internal const int MaximumFrameBytes = 4 * 1024 * 1024;

    internal static void Write<T>(Stream stream, T value, CancellationToken token = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaximumFrameBytes) throw new IOException("Shell message is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.WriteAsync(header, token).AsTask().GetAwaiter().GetResult();
        stream.WriteAsync(payload, token).AsTask().GetAwaiter().GetResult();
        stream.FlushAsync(token).GetAwaiter().GetResult();
    }

    internal static T Read<T>(Stream stream, CancellationToken token = default)
    {
        byte[] header = new byte[4];
        stream.ReadExactlyAsync(header, token).AsTask().GetAwaiter().GetResult();
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumFrameBytes) throw new IOException("Invalid Shell message size.");
        byte[] payload = new byte[length];
        stream.ReadExactlyAsync(payload, token).AsTask().GetAwaiter().GetResult();
        return JsonSerializer.Deserialize<T>(payload) ?? throw new IOException("Empty Shell message.");
    }

    // Synchronous by design: all Shell calls stay on the helper entry point's STA thread.
    internal static void Serve(Stream stream, Func<DesktopShellWorker.ShellRequest, DesktopShellWorker.ShellResponse> execute,
        CancellationToken handshakeToken = default, Action? ready = null, Action<bool>? operationState = null)
    {
        var hello = Read<DesktopShellWorker.ShellRequest>(stream, handshakeToken);
        if (hello.Operation != "hello-v1") throw new IOException("Shell handshake required.");
        Write(stream, new DesktopShellWorker.ShellResponse { Ready = true }, handshakeToken);
        ready?.Invoke();
        while (true)
        {
            var request = Read<DesktopShellWorker.ShellRequest>(stream);
            if (request.Operation == "ping")
            {
                Write(stream, new DesktopShellWorker.ShellResponse { Ready = true });
                continue;
            }
            if (request.Operation is not ("capture" or "place" or "switch" or "verify"))
                throw new IOException("Unknown Shell session operation.");
            operationState?.Invoke(true);
            try { Write(stream, execute(request)); }
            finally { operationState?.Invoke(false); }
        }
    }
}
