using System.Collections;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class ShellMatcherScalingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingReadsCollectionsLinearly(bool legacy)
    {
        var icons = Enumerable.Range(0, 500).Select(i => new IconPosition($"Item {i}", i, 0,
            legacy ? null : $@"C:\Desktop\{i}")).ToArray();
        var wanted = new CountingList(icons);
        var current = new CountingList(icons.Reverse().ToArray());
        var matches = ShellIconMatcher.Match(wanted, current);
        Assert.Equal(icons.Length, matches.Count);
        Assert.All(matches, match => Assert.Equal(match.Wanted, current[match.CurrentIndex]));
        Assert.True(wanted.Reads + current.Reads <= icons.Length * 10,
            $"Repeated scans: {wanted.Reads + current.Reads} reads for {icons.Length} icons.");
    }

    [Fact]
    public void AmbiguousLegacyCaptionDoesNotDisplaceAnExactIdentity()
    {
        var match = Assert.Single(ShellIconMatcher.Match([new("A", 1, 2, @"C:\Desk\A"), new("A", 3, 4)],
            [new("A", 0, 0, @"c:\desk\a")]));
        Assert.Equal(@"C:\Desk\A", match.Wanted.Identity);
    }

    private sealed class CountingList(IconPosition[] items) : IReadOnlyList<IconPosition>
    {
        internal int Reads { get; private set; }
        public int Count => items.Length;
        public IconPosition this[int index] { get { Reads++; return items[index]; } }
        public IEnumerator<IconPosition> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
