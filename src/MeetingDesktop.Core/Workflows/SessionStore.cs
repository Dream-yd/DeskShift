using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingDesktop.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace MeetingDesktop.Core.Workflows;

/// <summary>
/// Reads and writes the record of an in-progress desktop swap.
/// </summary>
/// <remarks>
/// This file is what makes a swap reversible. Without it the app knows the
/// desktop points somewhere unusual but not where it pointed before, and the
/// only safe thing it can do is refuse to touch anything.
///
/// It is written BEFORE the desktop is redirected and deleted only AFTER a
/// successful restore. A stale file is harmless - it is detected and reported.
/// A missing file during a swap is the state that loses data.
/// </remarks>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static readonly object SnapshotSync = new();
    private readonly string _path;

    public SessionStore(string sessionFilePath) => _path = sessionFilePath;

    public string FilePath => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>Returns the recorded session, or null when there is none or it is unreadable.</summary>
    public SessionInfo? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return ReadRecord(File.ReadAllBytes(_path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Parse the bytes being reviewed, rather than reopening a path which may
    // already refer to a different session. StreamReader retains BOM support.
    internal static SessionInfo? ReadRecord(byte[] record)
    {
        try
        {
            using var stream = new MemoryStream(record, writable: false);
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json)) return null;
            SessionInfo? info = JsonSerializer.Deserialize<SessionInfo>(json, Options);
            return info is null || info.SchemaVersion > SessionInfo.CurrentSchemaVersion ? null : info;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Verifies and renames the same open file, denying concurrent writes and replacement.</summary>
    internal void ArchiveVerified(byte[] expectedRecord, string archivePath, Action? beforeRename = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Recovery record archival requires Windows handle-based rename.");

        const uint genericRead = 0x80000000;
        const uint deleteAccess = 0x00010000;
        const uint openExisting = 3;
        const uint normalAttributes = 0x80;
        // Omit FILE_SHARE_WRITE and FILE_SHARE_DELETE. This also refuses an
        // already-open writer/deleter, even one allowing us to share its file.
        using SafeFileHandle handle = CreateFileW(Path.GetFullPath(_path), genericRead | deleteAccess,
            FileShare.Read, nint.Zero, openExisting, normalAttributes, nint.Zero);
        if (handle.IsInvalid)
            throw new IOException("The recovery record could not be locked for archival.", new Win32Exception(Marshal.GetLastPInvokeError()));

        using var stream = new FileStream(handle, FileAccess.Read);
        using var contents = new MemoryStream();
        stream.CopyTo(contents);
        if (!contents.ToArray().AsSpan().SequenceEqual(expectedRecord))
            throw new InvalidOperationException("The recovery record changed. Review it again.");

        beforeRename?.Invoke();

        // FILE_RENAME_INFO: DWORD flags, pointer-aligned HANDLE root (NULL),
        // DWORD UTF-16 byte length, then the destination name. The pointer's
        // alignment makes these offsets valid for both 32- and 64-bit Windows.
        byte[] name = Encoding.Unicode.GetBytes(Path.GetFullPath(archivePath));
        int lengthOffset = 2 * IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        byte[] renameInfo = new byte[checked(nameOffset + name.Length + sizeof(char))];
        BitConverter.TryWriteBytes(renameInfo.AsSpan(lengthOffset), name.Length);
        name.CopyTo(renameInfo, nameOffset);
        // Flags stay zero: an existing destination must never be overwritten.
        const int fileRenameInfo = 3;
        if (!SetFileInformationByHandle(handle, fileRenameInfo, renameInfo, (uint)renameInfo.Length))
            throw new IOException("The verified recovery record could not be archived.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        byte[] information, uint bufferSize);

    public void Save(SessionInfo info)
    {
        lock (SnapshotSync)
        {
            if (!string.IsNullOrWhiteSpace(info.AutoLayoutSnapshotPath) &&
                Path.IsPathFullyQualified(info.AutoLayoutSnapshotPath))
            {
                string snapshot = Path.GetFullPath(info.AutoLayoutSnapshotPath);
                if (!File.Exists(snapshot))
                {
                    // Preserve an existing recovery record even when its capture
                    // has disappeared, but never introduce a new missing reference.
                    string? previous = Load()?.AutoLayoutSnapshotPath;
                    if (string.IsNullOrWhiteSpace(previous) || !Path.IsPathFullyQualified(previous) ||
                        !string.Equals(Path.GetFullPath(previous), snapshot, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The recovery snapshot is missing before the session could be saved.");
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            WriteAtomic(_path, JsonSerializer.Serialize(info, Options));
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover session file is detected on the next run and reported.
            // Failing the restore because the bookkeeping could not be tidied
            // would be the wrong trade.
        }
    }

    /// <summary>
    /// Writes via a temporary file and then replaces the target, so an
    /// interrupted write cannot leave a half-written session behind.
    /// </summary>
    private static void WriteAtomic(string path, string contents)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
