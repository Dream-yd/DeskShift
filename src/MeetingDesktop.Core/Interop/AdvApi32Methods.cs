using System.Runtime.InteropServices;

namespace MeetingDesktop.Core.Interop;

/// <summary>
/// The advapi32 entry points needed to read a process token's integrity level.
/// </summary>
/// <remarks>
/// These are kept out of <see cref="NativeMethods"/> deliberately. That file is
/// the audit surface for the shell interop - the exported methods there are
/// exactly the ones that touch another process's windows and memory - and the
/// integrity check is a read-only query that touches neither. Keeping it
/// separate also makes the dependency obvious: reading a token is the only
/// reason this assembly links advapi32 at all.
///
/// The sequence a caller needs is OpenProcessToken, GetTokenInformation with
/// <see cref="TokenIntegrityLevel"/> (which hands back a TOKEN_MANDATORY_LABEL
/// whose Sid points at the integrity SID), then the last sub-authority of that
/// SID. The value of that sub-authority is the integrity level.
/// </remarks>
internal static class AdvApi32Methods
{
    /// <summary>TOKEN_QUERY - read the token, not impersonate with it or change it.</summary>
    public const uint TokenQuery = 0x0008;

    /// <summary>
    /// <c>TOKEN_INFORMATION_CLASS.TokenIntegrityLevel</c>. Spelled as a number
    /// because the enum lives in namespaces this project does not reference,
    /// and adding a package for one constant would be worse than the comment.
    /// </summary>
    public const int TokenIntegrityLevel = 25;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    /// <summary>
    /// Writes a structure described by <paramref name="informationClass"/> into
    /// <paramref name="buffer"/>.
    /// </summary>
    /// <remarks>
    /// For <see cref="TokenIntegrityLevel"/> the first call is expected to fail
    /// with ERROR_INSUFFICIENT_BUFFER and is made only to learn the size - the
    /// return value carries no information the first time round, and
    /// <paramref name="returnLength"/> is the entire point of it.
    /// </remarks>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(
        IntPtr token,
        int informationClass,
        IntPtr buffer,
        int bufferLength,
        out int returnLength);

    /// <summary>
    /// Pointer to the SID's sub-authority count byte, or NULL if
    /// <paramref name="sid"/> is NULL.
    /// </summary>
    /// <remarks>
    /// The count is the number of trailing 32-bit sub-authorities, not the
    /// SID's length in bytes, which is why the caller has to index with
    /// <c>count - 1</c> to reach the integrity level rather than guessing an
    /// offset.
    /// </remarks>
    [DllImport("advapi32.dll")]
    public static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    /// <summary>
    /// Pointer to sub-authority <paramref name="index"/> of the SID, or NULL if
    /// the index is out of range.
    /// </summary>
    [DllImport("advapi32.dll")]
    public static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
