using MeetingDesktop.App.Services;

namespace MeetingDesktop.App.Tests;

public sealed class InstanceActivationTests
{
    [Fact]
    public void ExistingSameVersionCanBeWokenAndDisposalReleasesTheEvent()
    {
        string name = @"Local\MeetingDesktop.Test." + Guid.NewGuid().ToString("N");
        using var awakened = new ManualResetEventSlim();
        using (InstanceActivation.Listen(name, awakened.Set))
        {
            Assert.True(InstanceActivation.TrySignal(name));
            Assert.True(awakened.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(InstanceActivation.TrySignal(name + ".missing"));
        }
        Assert.False(InstanceActivation.TrySignal(name));
    }

    [Fact]
    public void EventNameDistinguishesUsersAndVersionDirectories()
    {
        string first = InstanceActivation.Name("user-a", @"C:\Old\MeetingDesktop.exe");
        Assert.Equal(first, InstanceActivation.Name("user-a", @"c:\old\MeetingDesktop.exe"));
        Assert.NotEqual(first, InstanceActivation.Name("user-a", @"C:\New\MeetingDesktop.exe"));
        Assert.NotEqual(first, InstanceActivation.Name("user-b", @"C:\Old\MeetingDesktop.exe"));
    }
}
