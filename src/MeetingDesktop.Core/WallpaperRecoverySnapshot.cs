namespace MeetingDesktop.Core;

/// <summary>Preserves disposable generated wallpaper images before a desktop change.</summary>
/// <remarks>Recovery restores a static image with its saved fit, not a dynamic wallpaper engine's project or scene.</remarks>
internal static class WallpaperRecoverySnapshot
{
    internal const long MaximumBytes = 64 * 1024 * 1024;
    private const string Prefix = "wallpaper-";
    private static string DirectoryPath(AppPaths paths) => Path.Combine(paths.RecoveryDirectory, "Wallpapers");

    /// <summary>Best effort, bounded to 64 MiB and three seconds. Caller cancellation still aborts prepare.</summary>
    internal static async Task<string?> CaptureAsync(AppPaths paths, WallpaperState original, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (original.IsEmpty) return null;
        string? temporary = null;
        bool created = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var source = new FileStream(original.Path!, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = source.Length;
            if (length <= 0 || length > MaximumBytes || !IsPrivateDirectory(paths.RecoveryDirectory)) return null;
            string directory = DirectoryPath(paths);
            Directory.CreateDirectory(directory);
            if (!IsPrivateDirectory(directory)) return null;
            string extension = Path.GetExtension(original.Path!);
            if (string.IsNullOrEmpty(extension)) extension = ".bmp";
            string destination = Path.Combine(directory, Prefix + Guid.NewGuid().ToString("N") + extension);
            temporary = destination + ".tmp";
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                created = true;
                byte[] buffer = new byte[65536];
                long remaining = length;
                while (remaining > 0)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), timeout.Token)
                        .ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("The original wallpaper changed during capture.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    remaining -= read;
                }
                if (source.Length != length) throw new IOException("The original wallpaper changed during capture.");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
        finally
        {
            if (created && temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Prefer the original. A missing/invalid backup leaves the existing original apply/failure behavior intact.</summary>
    internal static WallpaperState Resolve(AppPaths paths, WallpaperState original, string? backupPath)
    {
        if (original.IsEmpty || File.Exists(original.Path) || string.IsNullOrWhiteSpace(backupPath)) return original;
        try
        {
            string backup = Path.GetFullPath(backupPath);
            string directory = DirectoryPath(paths);
            string name = Path.GetFileName(backup);
            string stem = Path.GetFileNameWithoutExtension(name);
            if (!string.Equals(Path.GetDirectoryName(backup), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !stem.StartsWith(Prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(stem[Prefix.Length..], "N", out _) || !IsPrivateDirectory(directory)) return original;
            var file = new FileInfo(backup);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length <= 0 || file.Length > MaximumBytes)
                return original;
            // Confirm readability before selecting the fallback. Never copy it
            // back to the engine-owned filename or overwrite an original file.
            using var readable = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
            return original with { Path = backup };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return original;
        }
    }

    private static bool IsPrivateDirectory(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
