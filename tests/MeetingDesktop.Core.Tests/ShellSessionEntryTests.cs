namespace MeetingDesktop.Core.Tests;

public sealed class ShellSessionEntryTests
{
    [Theory]
    [InlineData("--desktop-shell-session")]
    [InlineData("--desktop-shell-session|not-a-guid|1|1")]
    [InlineData("--desktop-shell-session|00000000000000000000000000000000|1|1")]
    [InlineData("--desktop-shell-session|132bd8a194a24cb1805a06e8c0a9f93e|-1|1")]
    public void InvalidSessionInvocationFailsClosedWithoutStartingGui(string arguments)
    {
        Assert.True(DesktopShellWorker.TryRun(arguments.Split('|'), out int code));
        Assert.Equal(1, code);
    }
}
