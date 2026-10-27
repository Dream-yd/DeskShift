namespace MeetingDesktop.Core.Tests;

public sealed class ShellSettingsNotificationTests
{
    [Fact]
    public void OnlyTheShellWindowReceivesTheSettingsMessage()
    {
        List<nint> recipients = [];
        ShellSettingsNotification.Send(() => 1234, recipients.Add);
        Assert.Equal(new nint[] { 1234 }, recipients);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65535)]
    public void MissingOrInvalidShellNeverFallsBackToABroadcast(int window)
    {
        List<nint> recipients = [];
        ShellSettingsNotification.Send(() => window, recipients.Add);
        Assert.Empty(recipients);
    }
}
