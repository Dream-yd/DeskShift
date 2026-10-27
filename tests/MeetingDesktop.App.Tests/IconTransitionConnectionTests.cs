using System.Diagnostics;
using System.Reflection;
using System.Text;
using MeetingDesktop.App.Services;

namespace MeetingDesktop.App.Tests;

public sealed class IconTransitionConnectionTests
{
    [Fact]
    public async Task SlowNativeAcquisitionDoesNotDiscardAHealthyHelper()
    {
        // A separate pipe peer reproduces a slow native acquisition without
        // touching Explorer. Subsequent replies have no artificial delay.
        const string script = "[Console]::WriteLine('ready'); " +
            "while (($line = [Console]::ReadLine()) -ne $null) { " +
            "if ($line.StartsWith('begin ')) { Start-Sleep -Milliseconds 150 }; " +
            "[Console]::WriteLine('ok') }";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("ready", await process.StandardOutput.ReadLineAsync(timeout.Token));
        using var connection = (IconTransitionConnection)Activator.CreateInstance(
            typeof(IconTransitionConnection), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [process], culture: null)!;
        string id = Guid.NewGuid().ToString("N");
        Assert.True(await connection.SendAsync("begin", id));
        Assert.True(await connection.SendAsync("hide", id));
        Assert.True(await connection.SendAsync("end", id));
    }
}
