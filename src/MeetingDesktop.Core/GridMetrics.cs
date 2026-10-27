using Microsoft.Win32;
using MeetingDesktop.Core.Interop;

namespace MeetingDesktop.Core;

/// <summary>
/// Works out the desktop icon grid pitch, in physical pixels.
/// </summary>
public static class GridMetrics
{
    /// <summary>
    /// Any observed gap larger than this is treated as a gap between columns
    /// rather than a pitch, and ignored when deriving one.
    /// </summary>
    private const int MaxPlausiblePitch = 512;

    /// <summary>The pitch Windows has used at 96 DPI for as long as anyone can remember.</summary>
    private const double DefaultPitchAt96Dpi = 75.0;

    /// <summary>
    /// Column pitch, resolved from the most trustworthy source available.
    /// </summary>
    /// <remarks>
    /// Four sources, in order. Each is a proxy for the others; the ordering
    /// is by how directly it reflects what explorer actually did.
    ///
    /// 1. <b>Observed deltas.</b> The actual pixels the running explorer build
    ///    accepted. The prototype used this too, but took the
    ///    <em>minimum</em> positive gap - which is wrong the moment a user
    ///    drags an icon off the grid, because "align to grid" can be off. A
    ///    single off-grid icon then reports a pitch of, say, 12 px, the
    ///    parking lattice built from it is far too fine, parked icons land on
    ///    top of each other, and the two-phase move cascades - the exact
    ///    failure the two-phase algorithm exists to prevent. The
    ///    <em>mode</em> is robust to that: one off-grid icon cannot outvote
    ///    fourteen on-grid ones.
    ///
    /// 2. <b>The control's own answer.</b> Authoritative, and the layer that
    ///    saves you when the desktop holds only one column and there are no
    ///    deltas to observe at all.
    ///
    /// 3. <b><c>WindowMetrics\IconSpacing</c>.</b> Stored as negated twips, so
    ///    pixels = abs(value) / 15. On the machine this was developed on it
    ///    reads -1128, which is 75.2 - matching the observed 75 px pitch. That
    ///    is why 75 is not a magic number.
    ///
    /// 4. <b>DPI-scaled default.</b> Last resort. Scaling matters because the
    ///    LVM_* positions are always physical pixels, so a bare 75 would be
    ///    wrong on any display that is not at 100%.
    /// </remarks>
    public static int ResolveColumnPitch(
        IReadOnlyList<int> observedX,
        nint listView = default,
        nint process = default)
    {
        int observed = DerivePitchFromObservation(observedX);
        int native = TryGetListViewSpacing(listView, process);

        // Prefer the control's own figure when the observation is a whole
        // multiple of it - that happens when icons are spaced two or more
        // cells apart and the mode lands on 150 instead of 75.
        if (native > 0 && observed > 0 && observed % native == 0)
        {
            return native;
        }

        if (observed > 0)
        {
            return observed;
        }

        if (native > 0)
        {
            return native;
        }

        int registry = TryGetRegistryIconSpacing();
        if (registry > 0)
        {
            return registry;
        }

        return ScaleForDpi((int)DefaultPitchAt96Dpi, listView);
    }

    /// <summary>
    /// The most common gap between adjacent, distinct X coordinates.
    /// </summary>
    /// <returns>The pitch, or 0 when there is not enough data to tell.</returns>
    internal static int DerivePitchFromObservation(IReadOnlyList<int> observedX)
    {
        if (observedX is null || observedX.Count < 2)
        {
            return 0;
        }

        var sorted = observedX.Distinct().OrderBy(x => x).ToList();
        var deltas = new List<int>(sorted.Count - 1);

        for (int i = 1; i < sorted.Count; i++)
        {
            int delta = sorted[i] - sorted[i - 1];
            if (delta > 1 && delta <= MaxPlausiblePitch)
            {
                deltas.Add(delta);
            }
        }

        if (deltas.Count == 0)
        {
            return 0;
        }

        // Mode, then smallest - so a tie resolves to the tighter pitch, which
        // is the safer lattice to build a parking area from.
        return deltas
            .GroupBy(d => d)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .First()
            .Key;
    }

    private static int TryGetListViewSpacing(nint listView, nint process)
    {
        if (listView == nint.Zero)
        {
            return 0;
        }

        try
        {
            // wParam is fSmall, not "is this the large view": TRUE selects the
            // SMALL icon view and FALSE the icon (large) one. Passing 1 here
            // asked for the small-icon spacing - 96 on the machine this was
            // written on, against 75 for the real desktop - and that wrong
            // pitch is what the placer builds its parking lattice from when
            // there is nothing to observe, or when the observed pitch is a
            // multiple of it.
            nint sent = NativeMethods.SendMessageTimeout(
                listView,
                NativeMethods.LVM_GETITEMSPACING,
                nint.Zero, // FALSE = icon (large) view
                nint.Zero,
                NativeMethods.SMTO_ABORTIFHUNG,
                NativeMethods.ListViewMessageTimeoutMs,
                out nint packed);

            if (sent == nint.Zero)
            {
                return 0;
            }

            int cx = (int)(packed.ToInt64() & 0xFFFF);
            return cx > 1 && cx <= MaxPlausiblePitch ? cx : 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return 0;
        }
    }

    private static int TryGetRegistryIconSpacing()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\Desktop\WindowMetrics");

            if (key?.GetValue("IconSpacing") is not string raw)
            {
                return 0;
            }

            if (!int.TryParse(raw, out int twips))
            {
                return 0;
            }

            int pixels = (int)Math.Round(Math.Abs(twips) / 15.0);
            return pixels > 1 && pixels <= MaxPlausiblePitch ? pixels : 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }

    private static int ScaleForDpi(int pitchAt96, nint window)
    {
        if (window == nint.Zero)
        {
            return pitchAt96;
        }

        try
        {
            uint dpi = NativeMethods.GetDpiForWindow(window);
            if (dpi == 0)
            {
                return pitchAt96;
            }

            return (int)Math.Round(pitchAt96 * dpi / 96.0);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return pitchAt96;
        }
    }
}
