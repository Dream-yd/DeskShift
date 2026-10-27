using System.Runtime.InteropServices;
using MeetingDesktop.Core.Interop;
using MeetingDesktop.Core.Models;
using static MeetingDesktop.Core.Interop.NativeStructs;

namespace MeetingDesktop.Core;

/// <summary>
/// Moves desktop icons to requested positions.
/// </summary>
/// <remarks>
/// <para>
/// Icons must be moved in two passes. Windows will not let two icons share a
/// cell: asking to place an icon where another one still sits makes Windows
/// shove it to the nearest free spot. Doing that for a whole layout produces a
/// cascade in which every icon ends up holding the position the <em>next</em>
/// icon asked for. The symptom is unmistakable once you have seen it - nine
/// of ten correct and one column shifted by exactly one row - and it is the
/// reason a naive implementation looks like it "almost works".
/// </para>
/// <para>
/// So: park everything on free cells first, which empties every target, then
/// place. The parking pass must cover <em>all</em> icons, not just the ones
/// being placed - an icon that is not being moved will happily sit on a target
/// cell and cause exactly the same displacement.
/// </para>
/// <para>
/// Interop stays synchronous. <c>SendMessageTimeout</c> is a blocking
/// cross-process call and cannot be made async; callers run this on a
/// background thread via <c>Task.Run</c>.
/// </para>
/// </remarks>
public static class DesktopIconPlacer
{
    /// <summary>
    /// Places the named icons at the requested coordinates.
    /// </summary>
    /// <param name="requested">Icon captions and their target positions.</param>
    /// <param name="cancellationToken">Checked between icons and during settle waits.</param>
    public static PlaceResult Place(
        IReadOnlyList<IconPosition> requested,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);

        if (requested.Count == 0)
        {
            return new PlaceResult(0, 0, Array.Empty<IconMismatch>(), Array.Empty<string>(), false, false);
        }

        using DesktopListViewTarget target = DesktopGridView.Open(cancellationToken);
        return Place(target, requested, cancellationToken);
    }

    internal static PlaceResult Place(
        DesktopListViewTarget target,
        IReadOnlyList<IconPosition> requested,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IconPosition> current = CompatibilityIconIdentity.TryEnrich(
            DesktopIconReader.Read(target, cancellationToken), cancellationToken);
        var matches = ShellIconMatcher.Match(requested, current);
        var indices = matches.Select(match => match.CurrentIndex).ToList();
        var targets = matches.Select(match => match.Wanted).ToList();
        var matched = matches.Select(match => match.Wanted).ToHashSet();
        var notFound = requested.Where(icon => !matched.Contains(icon)).Select(icon => icon.Name).ToList();

        if (indices.Count == 0)
        {
            return new PlaceResult(requested.Count, 0, Array.Empty<IconMismatch>(), notFound, false, false);
        }

        nint remote = NativeMethods.VirtualAllocEx(
            target.Process,
            nint.Zero,
            8,
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
            NativeMethods.PAGE_READWRITE);

        if (remote == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new DesktopInteropException(
                InteropFailureKind.RemoteAllocationFailed,
                $"VirtualAllocEx(8 bytes) in explorer pid {target.ProcessId} failed (Win32 error {error}).",
                win32ErrorCode: error,
                processId: target.ProcessId);
        }

        bool usedTwoPhase = false;
        bool parkingSucceeded = false;

        try
        {
            List<(int X, int Y)> freeSlots = BuildParkingSlots(target, current, targets);

            // Park every icon, not just the ones being placed. See the class
            // remarks - a stationary icon on a target cell displaces a placed
            // one just as effectively.
            bool canParkEverything = freeSlots.Count >= current.Count;
            bool canParkTargets = freeSlots.Count >= indices.Count;

            if (canParkEverything)
            {
                for (int i = 0; i < current.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SetPosition(target, remote, i, freeSlots[i].X, freeSlots[i].Y);
                }
            }
            else if (canParkTargets)
            {
                for (int i = 0; i < indices.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SetPosition(target, remote, indices[i], freeSlots[i].X, freeSlots[i].Y);
                }
            }

            usedTwoPhase = canParkTargets;

            if (usedTwoPhase)
            {
                Settle(cancellationToken);

                // Verify the park actually produced distinct cells before
                // trusting it. If the lattice was wrong (a bad pitch), parked
                // icons land on top of each other and the placement below
                // cascades - and a read-back at the END would only show the
                // aftermath. Checking here turns a silent cascade into a
                // reported degradation.
                parkingSucceeded = VerifyParking(target, indices, freeSlots, cancellationToken);
            }

            for (int i = 0; i < indices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetPosition(target, remote, indices[i], targets[i].X, targets[i].Y);
            }

            // Put the icons we were not asked to move back where they were,
            // unless a target has since claimed the spot.
            if (canParkEverything)
            {
                var claimed = new HashSet<long>(targets.Select(t => Key(t.X, t.Y)));
                var moved = new HashSet<int>(indices);

                for (int i = 0; i < current.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (moved.Contains(i) || claimed.Contains(Key(current[i].X, current[i].Y)))
                    {
                        continue;
                    }

                    SetPosition(target, remote, i, current[i].X, current[i].Y);
                }
            }

            RedrawItems(target, current.Count);
            Settle(cancellationToken);
        }
        finally
        {
            NativeMethods.VirtualFreeEx(target.Process, remote, nint.Zero, NativeMethods.MEM_RELEASE);
        }

        IReadOnlyList<IconMismatch> offTarget = Verify(target, targets, notFound, cancellationToken);

        return new PlaceResult(
            requested.Count,
            indices.Count,
            offTarget,
            notFound,
            usedTwoPhase,
            parkingSucceeded);
    }

    /// <summary>
    /// Builds a lattice of cells that are free in both the current and target
    /// layouts, for parking icons on.
    /// </summary>
    /// <remarks>
    /// Columns are generated all the way to the right edge of the listview
    /// rather than being limited to columns that already hold an icon. Using
    /// only observed columns collapses to zero free slots as soon as the
    /// desktop is packed: ten columns times fourteen rows is exactly the 140
    /// cells a fully populated block occupies, leaving nowhere to park and
    /// silently disabling the two-phase move.
    /// </remarks>
    private static List<(int X, int Y)> BuildParkingSlots(
        DesktopListViewTarget target,
        IReadOnlyList<IconPosition> current,
        IReadOnlyList<IconPosition> targets)
    {
        var rows = current.Select(p => p.Y)
            .Concat(targets.Select(p => p.Y))
            .Distinct()
            .OrderBy(y => y)
            .ToList();

        var observedX = current.Select(p => p.X)
            .Concat(targets.Select(p => p.X))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        if (rows.Count == 0 || observedX.Count == 0)
        {
            return new List<(int, int)>();
        }

        int pitch = GridMetrics.ResolveColumnPitch(observedX, target.View, target.Process);
        int rightEdge = GetRightEdge(target.View, observedX[^1]);

        var occupied = new HashSet<long>(
            current.Select(p => Key(p.X, p.Y))
                .Concat(targets.Select(p => Key(p.X, p.Y))));

        var slots = new List<(int X, int Y)>();

        for (int x = observedX[0]; x <= rightEdge; x += pitch)
        {
            foreach (int y in rows)
            {
                if (!occupied.Contains(Key(x, y)))
                {
                    slots.Add((x, y));
                }
            }
        }

        return slots;
    }

    private static int GetRightEdge(nint view, int fallback)
    {
        if (view == nint.Zero || !NativeMethods.GetClientRect(view, out RECT client))
        {
            return fallback;
        }

        return client.Right > fallback ? client.Right - 1 : fallback;
    }

    private static bool VerifyParking(
        DesktopListViewTarget target,
        IReadOnlyList<int> indices,
        IReadOnlyList<(int X, int Y)> slots,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IconPosition> after = DesktopIconReader.Read(target, cancellationToken);
        var landed = new HashSet<long>();

        for (int i = 0; i < indices.Count; i++)
        {
            int index = indices[i];
            if (index >= after.Count)
            {
                return false;
            }

            IconPosition actual = after[index];

            // Either it moved to its slot, or it is still where it started -
            // what must not happen is two icons sharing one cell.
            if (!landed.Add(Key(actual.X, actual.Y)))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<IconMismatch> Verify(
        DesktopListViewTarget target,
        IReadOnlyList<IconPosition> targets,
        List<string> notFound,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IconPosition> after = CompatibilityIconIdentity.TryEnrich(
            DesktopIconReader.Read(target, cancellationToken), cancellationToken);
        var matches = ShellIconMatcher.Match(targets, after);
        var mismatches = new List<IconMismatch>();
        foreach (var match in matches)
        {
            var want = match.Wanted;
            var actual = after[match.CurrentIndex];
            if (actual.X != want.X || actual.Y != want.Y)
                mismatches.Add(new(want.Name, want.X, want.Y, actual.X, actual.Y));
        }
        // Disappeared/ambiguous items during read-back must also prevent an exact result.
        foreach (var want in targets.Where(want => !matches.Any(match => match.Wanted == want)))
            notFound.Add(want.Name);

        return mismatches;
    }

    private static void SetPosition(DesktopListViewTarget target, nint remote, int index, int x, int y)
    {
        var point = new byte[8];
        BitConverter.GetBytes(x).CopyTo(point, 0);
        BitConverter.GetBytes(y).CopyTo(point, 4);

        if (!NativeMethods.WriteProcessMemory(target.Process, remote, point, point.Length, out _))
        {
            int error = Marshal.GetLastWin32Error();
            throw new DesktopInteropException(
                InteropFailureKind.RemoteMemoryAccessFailed,
                $"WriteProcessMemory into explorer pid {target.ProcessId} failed (Win32 error {error}).",
                win32ErrorCode: error,
                processId: target.ProcessId);
        }

        nint sent = NativeMethods.SendMessageTimeout(
            target.View,
            NativeMethods.LVM_SETITEMPOSITION32,
            index,
            remote,
            NativeMethods.SMTO_ABORTIFHUNG,
            NativeMethods.ListViewMessageTimeoutMs,
            out _);

        if (sent == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new DesktopInteropException(
                InteropFailureKind.ListViewMessageTimeout,
                $"LVM_SETITEMPOSITION32 for item {index} timed out (Win32 error {error}). " +
                "If the positions silently do nothing, explorer may be running at a higher integrity level " +
                "than this process - UIPI drops messages above WM_USER without reporting an error.",
                win32ErrorCode: error,
                processId: target.ProcessId);
        }
    }

    private static void RedrawItems(DesktopListViewTarget target, int itemCount)
    {
        if (itemCount <= 0)
        {
            return;
        }

        NativeMethods.SendMessageTimeout(
            target.View,
            NativeMethods.LVM_REDRAWITEMS,
            nint.Zero,
            itemCount - 1,
            NativeMethods.SMTO_ABORTIFHUNG,
            NativeMethods.ListViewMessageTimeoutMs,
            out _);
    }

    private static void Settle(CancellationToken cancellationToken) =>
        cancellationToken.WaitHandle.WaitOne(500);

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
}
