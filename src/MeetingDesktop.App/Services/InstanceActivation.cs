using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace MeetingDesktop.App.Services;

/// <summary>Wake an existing copy of the same executable without starting a second UI.</summary>
internal sealed class InstanceActivation : IDisposable
{
    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _registration;

    private InstanceActivation(EventWaitHandle signal, Action activate)
    {
        _event = signal;
        _registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => activate(),
            null, Timeout.Infinite, executeOnlyOnce: false);
    }

    internal static string Name(string user, string executable)
    {
        string path = Path.GetFullPath(executable).ToUpperInvariant();
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];
        return $@"Local\MeetingDesktop.Activate.{user}.{fingerprint}";
    }

    internal static InstanceActivation Listen(string name, Action activate) =>
        new(new EventWaitHandle(false, EventResetMode.AutoReset, name), activate);

    internal static bool TrySignal(string name)
    {
        try
        {
            using EventWaitHandle signal = EventWaitHandle.OpenExisting(name);
            return signal.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        { return false; }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _event.Dispose();
    }
}
