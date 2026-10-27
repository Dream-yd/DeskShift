using System.Runtime.InteropServices;
using System.Text;
using MeetingDesktop.Core.Interop;
using MeetingDesktop.Core.Models;
using static MeetingDesktop.Core.Interop.NativeStructs;

namespace MeetingDesktop.Core;

/// <summary>
/// Reads the caption and position of every desktop icon.
/// </summary>
/// <remarks>
/// Getting text out of a listview in another process needs a buffer that
/// lives in <em>that</em> process's address space: the control writes the
/// string there, and we read it back. Everything else in this file is
/// bookkeeping around that one fact.
/// </remarks>
public static class DesktopIconReader
{
    /// <summary>Maximum caption length the control will return.</summary>
    private const int MaxCaptionChars = 260;

    /// <summary>
    /// Returns every desktop icon with its current position.
    /// </summary>
    /// <exception cref="DesktopInteropException">The desktop listview could not be reached.</exception>
    public static IReadOnlyList<IconPosition> Read(CancellationToken cancellationToken = default)
    {
        using DesktopListViewTarget target = DesktopGridView.Open(cancellationToken);
        return CompatibilityIconIdentity.TryEnrich(Read(target, cancellationToken), cancellationToken);
    }

    internal static IReadOnlyList<IconPosition> Read(
        DesktopListViewTarget target,
        CancellationToken cancellationToken = default)
    {
        int itemCount = GetItemCount(target);
        int itemSize = Marshal.SizeOf<LVITEM>();
        int textBytes = MaxCaptionChars * sizeof(char);
        int bufferSize = itemSize + textBytes;

        nint remote = NativeMethods.VirtualAllocEx(
            target.Process,
            nint.Zero,
            bufferSize,
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
            NativeMethods.PAGE_READWRITE);

        if (remote == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new DesktopInteropException(
                InteropFailureKind.RemoteAllocationFailed,
                $"VirtualAllocEx({bufferSize} bytes) in explorer pid {target.ProcessId} failed (Win32 error {error}).",
                win32ErrorCode: error,
                processId: target.ProcessId);
        }

        var results = new List<IconPosition>(itemCount);
        nint staging = Marshal.AllocHGlobal(itemSize);

        try
        {
            for (int index = 0; index < itemCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string name = ReadCaption(target, remote, itemSize, staging, textBytes, index);
                POINT position = ReadPosition(target, remote, index);

                results.Add(new IconPosition(name, position.X, position.Y));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(staging);
            NativeMethods.VirtualFreeEx(target.Process, remote, nint.Zero, NativeMethods.MEM_RELEASE);
        }

        return results;
    }

    private static int GetItemCount(DesktopListViewTarget target)
    {
        SendOrThrow(target, NativeMethods.LVM_GETITEMCOUNT, nint.Zero, nint.Zero, out nint result);
        return (int)result;
    }

    private static string ReadCaption(
        DesktopListViewTarget target,
        nint remote,
        int itemSize,
        nint staging,
        int textBytes,
        int index)
    {
        var item = new LVITEM
        {
            Mask = NativeMethods.LVIF_TEXT,
            ItemIndex = index,
            SubItemIndex = 0,
            Text = remote + itemSize,
            TextMaxLength = MaxCaptionChars,
        };

        Marshal.StructureToPtr(item, staging, fDeleteOld: false);

        var itemBytes = new byte[itemSize];
        Marshal.Copy(staging, itemBytes, 0, itemSize);
        WriteRemote(target, remote, itemBytes);

        // The timeout check must happen before the read. On timeout the buffer
        // still holds the previous item's text, which parses as a perfectly
        // plausible - and completely wrong - icon name.
        SendOrThrow(target, NativeMethods.LVM_GETITEMTEXTW, index, remote, out _);

        var textBuffer = new byte[textBytes];
        ReadRemote(target, remote + itemSize, textBuffer);

        string text = Encoding.Unicode.GetString(textBuffer);
        int terminator = text.IndexOf('\0');
        return terminator >= 0 ? text[..terminator] : text;
    }

    private static POINT ReadPosition(DesktopListViewTarget target, nint remote, int index)
    {
        var empty = new byte[8];
        WriteRemote(target, remote, empty);

        SendOrThrow(target, NativeMethods.LVM_GETITEMPOSITION, index, remote, out _);

        var positionBytes = new byte[8];
        ReadRemote(target, remote, positionBytes);

        return new POINT
        {
            X = BitConverter.ToInt32(positionBytes, 0),
            Y = BitConverter.ToInt32(positionBytes, 4),
        };
    }

    private static void SendOrThrow(
        DesktopListViewTarget target,
        uint message,
        nint wParam,
        nint lParam,
        out nint result)
    {
        nint sent = NativeMethods.SendMessageTimeout(
            target.View,
            message,
            wParam,
            lParam,
            NativeMethods.SMTO_ABORTIFHUNG,
            NativeMethods.ListViewMessageTimeoutMs,
            out result);

        if (sent == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new DesktopInteropException(
                InteropFailureKind.ListViewMessageTimeout,
                $"The desktop listview did not answer message 0x{message:X4} within " +
                $"{NativeMethods.ListViewMessageTimeoutMs} ms (Win32 error {error}). explorer's UI thread may be busy or hung.",
                win32ErrorCode: error,
                processId: target.ProcessId);
        }
    }

    private static void WriteRemote(DesktopListViewTarget target, nint address, byte[] bytes)
    {
        if (!NativeMethods.WriteProcessMemory(target.Process, address, bytes, bytes.Length, out _))
        {
            int error = Marshal.GetLastWin32Error();
            throw new DesktopInteropException(
                InteropFailureKind.RemoteMemoryAccessFailed,
                $"WriteProcessMemory({bytes.Length} bytes) into explorer pid {target.ProcessId} failed (Win32 error {error}).",
                win32ErrorCode: error,
                processId: target.ProcessId);
        }
    }

    private static void ReadRemote(DesktopListViewTarget target, nint address, byte[] buffer)
    {
        if (!NativeMethods.ReadProcessMemory(target.Process, address, buffer, buffer.Length, out _))
        {
            int error = Marshal.GetLastWin32Error();
            throw new DesktopInteropException(
                InteropFailureKind.RemoteMemoryAccessFailed,
                $"ReadProcessMemory({buffer.Length} bytes) from explorer pid {target.ProcessId} failed (Win32 error {error}).",
                win32ErrorCode: error,
                processId: target.ProcessId);
        }
    }
}
