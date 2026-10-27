namespace MeetingDesktop.Core;

internal static class ShellIconPlacement
{
    internal static void Run<T>(IReadOnlyList<T> matches, Func<T, bool> isAt,
        Action<T[]> position, Action<int>? pause = null)
    {
        var pending = matches.Where(match => !isAt(match)).ToArray();
        if (pending.Length == 0) return;
        int stable = 0;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (pending.Length > 0) { position(pending); stable = 0; }
            // Explorer often places immediately. Confirm on two separate ticks
            // instead of always waiting 80 ms; a transient match resets the count.
            for (int poll = 0; poll < 4; poll++)
            {
                (pause ?? Thread.Sleep)(20);
                stable = matches.All(isAt) ? stable + 1 : 0;
                if (stable >= 2) return;
            }
            pending = matches.Where(match => !isAt(match)).ToArray();
        }
    }
}
