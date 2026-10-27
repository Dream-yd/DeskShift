using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeetingDesktop.Core.Models;
using Microsoft.Win32;

namespace MeetingDesktop.Core;

public sealed record SafetyBackupDesktopPaths(
    string? UserShellFoldersRaw, string? ShellFoldersRaw, string? KnownFolderPath, string? PublicDesktopPath)
{
    public string? UserShellFoldersValueKind { get; init; }
    public string? ShellFoldersValueKind { get; init; }
}

/// <summary>Explicit capture boundary: tests supply synthetic data and never read the machine.</summary>
public sealed record SafetyBackupCapture
{
    public required LayoutSnapshot Layout { get; init; }
    public required SafetyBackupDesktopPaths DesktopPaths { get; init; }
    public required WallpaperState Wallpaper { get; init; }
    public IReadOnlyList<string> PublicShortcutPaths { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record SafetyBackupExportResult(
    string OutputPath, IReadOnlyList<string> Warnings, int ShortcutCount, bool WallpaperCopied);

/// <summary>
/// Copies a runtime capture into a ZIP for manual recovery. Never applies state,
/// resolves shortcut targets, or moves/writes any captured source file.
/// </summary>
public sealed class SafetyBackupExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Func<SafetyBackupCapture> _capture;
    private readonly Func<string, Stream> _openRead;

    // No default machine capture: callers must deliberately choose their data source.
    public SafetyBackupExporter(Func<SafetyBackupCapture> capture, Func<string, Stream>? openRead = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _openRead = openRead ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete));
    }

    public SafetyBackupExportResult Export(string outputPath, bool overwriteConfirmed = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!Path.IsPathFullyQualified(outputPath) || !Path.GetExtension(outputPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an absolute ZIP output path.", nameof(outputPath));
        outputPath = Path.GetFullPath(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        bool replacing = File.Exists(outputPath);
        if (replacing && !overwriteConfirmed)
            throw new IOException("The ZIP already exists. Confirm replacement in the Save dialog first.");
        if (replacing && (File.GetAttributes(outputPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The output must not be a symbolic link.");

        SafetyBackupCapture capture = _capture();
        // Even an explicit Save dialog replacement must never replace a captured asset.
        foreach (string? source in capture.PublicShortcutPaths.Cast<string?>().Append(capture.Wallpaper.Path))
            if (!string.IsNullOrWhiteSpace(source) && Path.IsPathFullyQualified(source) &&
                Path.GetFullPath(source).Equals(outputPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The output is a captured source file. Choose another ZIP path.");

        var warnings = capture.Warnings.ToList();
        var entries = new List<ManifestEntry>();
        string temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool ownsTemporary = false;
        bool wallpaperCopied = false;
        int shortcutCount = 0;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                ownsTemporary = true;
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    AddJson("layout.json", capture.Layout);
                    AddJson("desktop-paths.json", capture.DesktopPaths);
                    AddJson("wallpaper-state.json", capture.Wallpaper);
                    if (!string.IsNullOrWhiteSpace(capture.Wallpaper.Path))
                        wallpaperCopied = AddAsset(capture.Wallpaper.Path, "wallpaper/0001-" + SafeName(capture.Wallpaper.Path));
                    foreach (string shortcut in capture.PublicShortcutPaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!Path.GetExtension(shortcut).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                        string name = $"public-desktop/{shortcutCount + 1:D4}-{SafeName(shortcut)}";
                        if (AddAsset(shortcut, name)) shortcutCount++;
                    }
                    AddBytes("README.txt", Encoding.UTF8.GetBytes(RecoveryInstructions));
                    AddJson("metadata.json", new
                    {
                        SchemaVersion = 1,
                        CreatedUtc = DateTimeOffset.UtcNow,
                        Purpose = "backup for manual recovery, not an automatic full-machine backup",
                        PurposeZh = "用于手动恢复的备份，不是自动整机备份",
                        AutomaticRestoreSupported = false,
                        CaptureIsTransactional = false,
                        Scope = "Current icon layout and monitor topology; desktop path raw values; wallpaper state and readable image bytes; readable top-level public desktop .lnk bytes. No user desktop file contents or shortcut targets.",
                        WallpaperCopied = wallpaperCopied,
                        ShortcutCount = shortcutCount,
                        Warnings = warnings,
                    });
                    // manifest.json describes every other entry; it cannot hash itself.
                    byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new { Algorithm = "SHA256", Entries = entries }, JsonOptions);
                    using var manifestStream = zip.CreateEntry("manifest.json").Open();
                    manifestStream.Write(manifest);

                    void AddJson<T>(string name, T value) => AddBytes(name, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

                    void AddBytes(string name, byte[] bytes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                        entry.Write(bytes);
                        entries.Add(new(name, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)), null));
                    }

                    bool AddAsset(string source, string name)
                    {
                        // Stage each asset before adding its entry: a disappearing/denied source
                        // produces a warning, never a misleading partial wallpaper or shortcut.
                        // A spool bounds memory and is removed on close, including on cancellation.
                        string spoolPath = temporary + "." + Guid.NewGuid().ToString("N") + ".asset.tmp";
                        using var spool = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.ReadWrite,
                            FileShare.None, 81920, FileOptions.DeleteOnClose);
                        Stream input;
                        try { input = _openRead(source); }
                        catch (Exception ex) when (IsReadFailure(ex)) { Warn(ex); return false; }
                        using (input)
                        {
                            var buffer = new byte[81920];
                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                int count;
                                try { count = input.Read(buffer, 0, buffer.Length); }
                                catch (Exception ex) when (IsReadFailure(ex)) { Warn(ex); return false; }
                                if (count == 0) break;
                                // Output errors must fail the export, not masquerade as unreadable assets.
                                spool.Write(buffer, 0, count);
                            }
                        }
                        spool.Position = 0;
                        string hash = Convert.ToHexString(SHA256.HashData(spool));
                        spool.Position = 0;
                        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                        spool.CopyTo(entry);
                        entries.Add(new(name, spool.Length, hash, source));
                        return true;

                        void Warn(Exception ex) => warnings.Add($"Asset not copied: {source} ({ex.GetType().Name}: {ex.Message})");
                    }
                }
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Both operations publish the completed sibling file atomically. A new file
            // appearing during export must never be silently overwritten.
            if (replacing) File.Replace(temporary, outputPath, destinationBackupFileName: null);
            else File.Move(temporary, outputPath, overwrite: false);
            ownsTemporary = false;
            return new(outputPath, warnings.ToArray(), shortcutCount, wallpaperCopied);
        }
        finally
        {
            if (ownsTemporary)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Called only after the user explicitly chooses an output file at runtime.</summary>
    public static SafetyBackupCapture CaptureCurrent(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var icons = DesktopIconReader.Read(cancellationToken);
        var topology = MonitorTopologyReader.Capture();
        if (topology.Monitors.Count == 0) warnings.Add("Monitor topology unavailable.");
        const string explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";
        var user = ReadRaw(explorer + "User Shell Folders", "Desktop");
        var legacy = ReadRaw(explorer + "Shell Folders", "Desktop");
        string? known = null;
        string? common = null;
        var shortcuts = new List<string>();
        try
        {
            known = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);
            common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrWhiteSpace(common)) warnings.Add("Public desktop path unavailable.");
            else
                foreach (string path in Directory.EnumerateFiles(common, "*", SearchOption.TopDirectoryOnly))
                    if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) shortcuts.Add(path);
        }
        catch (Exception ex) when (IsReadFailure(ex)) { warnings.Add($"Public desktop inventory incomplete: {ex.Message}"); }
        var wallpaper = new WallpaperState(ReadRaw(@"Control Panel\Desktop", "WallPaper").Value,
            ReadRaw(@"Control Panel\Desktop", "WallpaperStyle").Value ?? "10",
            ReadRaw(@"Control Panel\Desktop", "TileWallpaper").Value ?? "0");
        return new()
        {
            Layout = new LayoutSnapshot { CreatedUtc = DateTimeOffset.UtcNow, Topology = topology, Icons = icons },
            DesktopPaths = new(user.Value, legacy.Value, known, common)
            { UserShellFoldersValueKind = user.Kind, ShellFoldersValueKind = legacy.Kind },
            Wallpaper = wallpaper,
            PublicShortcutPaths = shortcuts.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Warnings = warnings,
        };

        (string? Value, string? Kind) ReadRaw(string keyPath, string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
                object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value is null) { warnings.Add($"Registry value absent: HKCU\\{keyPath}\\{name}"); return (null, null); }
                if (value is not string text) { warnings.Add($"Registry value is not text: HKCU\\{keyPath}\\{name}"); return (null, key!.GetValueKind(name).ToString()); }
                return (text, key!.GetValueKind(name).ToString());
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                warnings.Add($"Registry value unreadable: HKCU\\{keyPath}\\{name} ({ex.Message})");
                return (null, null);
            }
        }
    }

    private static bool IsReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;

    private static string SafeName(string path)
    {
        string name = Path.GetFileName(path);
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) || safe is "." or ".." ? "asset" : safe;
    }

    private sealed record ManifestEntry(string EntryName, long Length, string Sha256, string? SourcePath);

    private const string RecoveryInstructions = """
        Safety backup / 安全备份包
        This is a backup for manual recovery, not an automatic full-machine backup.
        这是用于手动恢复的备份，不是自动整机备份。

        Check metadata.json Warnings for missing/unreadable assets before relying on this package.
        manifest.json lists the SHA256 and byte length of every other ZIP entry.
        layout.json includes icon coordinates, identities (when available), and monitor topology.
        desktop-paths.json preserves raw HKCU desktop values, including unexpanded environment variables.
        wallpaper-state.json preserves the original wallpaper path, style, and tile values.
        wallpaper/ contains copied image bytes when readable; the original temporary path may disappear.
        public-desktop/ contains copied top-level .lnk files, not their target files.

        Manual recovery: verify hashes, review the warnings and original source paths, then choose
        which assets/settings to recover. Compare monitor arrangements before using coordinates.
        A copied wallpaper can be saved to a stable folder and selected through Windows Settings.
        Recover public shortcuts individually to their recorded SourcePath only after checking conflicts;
        modifying the public desktop may require administrator permission. Do not blindly apply registry values.
        Capture is read-only but not a transactional snapshot; files may change during capture.
        ZIP import and automatic restoration are not supported. User desktop files, shortcut targets,
        Wallpaper Engine projects, running programs, and full system state are not included.

        手动恢复前，请检查 metadata.json 的警告，并用 manifest.json 校验 SHA256 和字节数。
        layout.json 包含图标布局及显示器拓扑；desktop-paths.json 保留未展开的桌面路径原始值。
        wallpaper-state.json 保留壁纸路径、样式和排列值；wallpaper/ 保存可读取的壁纸副本。
        临时壁纸路径可能消失，可将副本放到稳定目录，再通过 Windows 设置选择。
        public-desktop/ 仅保存公共桌面的 .lnk 字节，不包含快捷方式指向的文件。
        恢复快捷方式前逐项核对 SourcePath 和文件冲突，写入公共桌面可能需要管理员权限。
        使用布局坐标前核对显示器排列，不要直接套用注册表值。本次只读采集不是事务快照。
        尚不支持 ZIP 导入或自动恢复；不包含用户桌面文件、快捷方式目标、Wallpaper Engine 工程或整机状态。
        """;
}
