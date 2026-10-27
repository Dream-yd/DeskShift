namespace MeetingDesktop.Core;

/// <summary>Copies meeting materials into a new archive. Originals are never moved or removed.</summary>
public static class MeetingFolderArchive
{
    public static string Copy(string source, string destinationRoot)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException();
        if (destinationRoot.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            destinationRoot.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Archive must be outside the meeting folder.");
        var entries = new List<(string Path, bool Directory)>();
        void Inspect(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked folders cannot be archived automatically.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked files cannot be archived automatically.");
                bool folder = (attributes & FileAttributes.Directory) != 0;
                entries.Add((entry, folder));
                if (folder) Inspect(entry);
            }
        }
        Inspect(source);
        string target = Path.Combine(destinationRoot, "Desktop-archive-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(target);
        foreach (var entry in entries)
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, entry.Path));
            if (entry.Directory) Directory.CreateDirectory(destination);
            else File.Copy(entry.Path, destination, overwrite: false);
        }
        return target;
    }
}
