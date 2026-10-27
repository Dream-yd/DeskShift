using System.Text.Json;

namespace MeetingDesktop.Core.Tests;

public sealed class DesktopShellFailureTests
{
    [Fact]
    public void VerificationFailureSurvivesHelperJsonBoundary()
    {
        var failure = new DesktopViewVerificationException("Visible=4, required=1, matches=False.");
        var response = DesktopShellWorker.DescribeFailure(failure);
        var roundTrip = JsonSerializer.Deserialize<DesktopShellWorker.ShellResponse>(JsonSerializer.Serialize(response))!;

        var error = Assert.Throws<DesktopViewVerificationException>(() => DesktopShellWorker.ValidateResponse(roundTrip, 1));
        Assert.Contains(failure.Message, error.Message);
        Assert.IsAssignableFrom<TimeoutException>(error);
    }

    [Fact]
    public void OlderHelperFailureWithoutCategoryRemainsAGenericFailure()
    {
        var response = JsonSerializer.Deserialize<DesktopShellWorker.ShellResponse>(
            "{\"Error\":\"Failed\",\"HResult\":-1}")!;
        var error = Assert.Throws<InvalidOperationException>(() => DesktopShellWorker.ValidateResponse(response, 1));
        Assert.Contains("FFFFFFFF", error.Message);
    }

    [Theory]
    [InlineData("switch", true)]
    [InlineData("verify", true)]
    [InlineData("capture", false)]
    [InlineData("place", false)]
    public void OnlyVerificationHelperTimeoutIsAViewVerificationFailure(string operation, bool expected)
    {
        var failure = DesktopShellWorker.HelperTimeout(operation);
        Assert.IsAssignableFrom<TimeoutException>(failure);
        Assert.Equal(expected, failure is DesktopViewVerificationException);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void InconsistentHelperResponseCannotReportSuccess(bool verificationFailed, int exitCode) =>
        Assert.Throws<InvalidOperationException>(() => DesktopShellWorker.ValidateResponse(
            new() { ViewVerificationFailed = verificationFailed }, exitCode));

    [Fact]
    public void OtherHelperErrorsCannotMasqueradeAsViewVerificationFailure()
    {
        var response = DesktopShellWorker.DescribeFailure(new IOException("Cannot read request."));
        Assert.Throws<InvalidOperationException>(() => DesktopShellWorker.ValidateResponse(response, 1));
    }

    [Fact]
    public void IndependentVerificationPreservesTheActualRemoteFailure()
    {
        var response = DesktopShellWorker.DescribeFailure(new IOException("Cannot enumerate the disconnected folder."));
        var error = Assert.Throws<InvalidOperationException>(() => DesktopShellWorker.VerifiedIcons(response, []));
        Assert.Contains("disconnected folder", error.Message);
    }

    [Fact]
    public void APlainCaptureCannotMasqueradeAsIndependentTargetVerification() =>
        Assert.Throws<DesktopViewVerificationException>(() => DesktopShellWorker.VerifiedIcons(new() { Icons = [] }, []));

    [Fact]
    public void IndependentVerifierMustReturnTheSameIconIdentities()
    {
        var original = new Models.IconPosition("Item", 1, 2, @"E:\First\Item", true);
        var other = original with { Identity = @"E:\Other\Item" };
        Assert.Throws<DesktopViewVerificationException>(() => DesktopShellWorker.VerifiedIcons(
            new() { Icons = [other], TargetVerified = true }, [original]));
        Assert.Equal(original, Assert.Single(DesktopShellWorker.VerifiedIcons(
            new() { Icons = [original], TargetVerified = true }, [original])));
    }
}
