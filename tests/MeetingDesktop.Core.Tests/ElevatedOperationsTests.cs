using MeetingDesktop.Core;

namespace MeetingDesktop.Core.Tests;

/// <summary>
/// Tests for the elevated entry point's argument contract.
/// </summary>
/// <remarks>
/// <para>
/// Only the paths that return <c>false</c> are covered here, and that is not a
/// gap - it is the point. The two that return <c>true</c> perform the
/// privileged file move for real, so a test that reached them would park the
/// machine running the tests, and the public desktop's shortcuts would go with
/// it.
/// </para>
/// <para>
/// What <em>is</em> worth pinning is the shape of the request, because that
/// restriction is the whole security model. This entry point takes exactly one
/// token and matches it against a fixed list. If it ever accepted a path, any
/// unprivileged program on the machine could launch this executable to move
/// arbitrary files with administrator rights - a file-system back door with a
/// UAC prompt drawn over it.
/// </para>
/// </remarks>
public class ElevatedOperationsTests
{
    [Theory]
    [InlineData]
    [InlineData("--elevated-move-shortcuts-aside", "extra")]
    [InlineData("--elevated-move-shortcuts-aside", "--elevated-restore-shortcuts")]
    [InlineData("--data-dir", @"C:\somewhere")]
    [InlineData("--unknown-token")]
    [InlineData("")]
    public void TryRun_ReturnsFalse_ForAnythingButASingleKnownToken(params string[] args)
    {
        // False means "this is an ordinary launch, start the GUI" - and it must
        // be cheap, because it runs on every double-click.
        bool isElevatedRun = ElevatedOperations.TryRun(args, out int exitCode);

        Assert.False(isElevatedRun);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void TryRun_ReturnsFalse_ForANullArgumentArray()
    {
        // A single-file executable started with nothing at all. Main never
        // produces this, but the method is public and the null check is what
        // keeps a bad caller from reaching the switch.
        Assert.False(ElevatedOperations.TryRun(null!, out _));
    }

    [Fact]
    public void TheTwoTokensAreDistinct_AndCarryNoSpaces()
    {
        // The tokens travel as a single unquoted argument, so a space in one
        // would split into two arguments on the way in and the elevated process
        // would fall back to starting the GUI - a second window, no error, and
        // a broker that reports success because it saw exit code 0.
        Assert.NotEqual(ElevatedOperations.MoveShortcutsAside, ElevatedOperations.RestoreShortcuts);
        Assert.DoesNotContain(' ', ElevatedOperations.MoveShortcutsAside);
        Assert.DoesNotContain(' ', ElevatedOperations.RestoreShortcuts);
    }
}
