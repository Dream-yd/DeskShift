using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core;

/// <summary>
/// Reads and writes <see cref="AppSettings"/> as a single JSON file.
/// </summary>
/// <remarks>
/// The two halves of this class fail differently on purpose.
/// <see cref="Load"/> answers with defaults for anything it cannot trust, because
/// an app that refuses to start on a corrupt settings file gives the user no way
/// to repair it from inside the app. <see cref="Save"/> reports its failure,
/// because a save that silently did nothing would lose the user's choices at the
/// next sign-in with no explanation.
/// </remarks>
public sealed class SettingsStore
{
    /// <summary>
    /// One shared options instance. <see cref="JsonSerializerOptions"/> freezes
    /// and caches its metadata on first use, so building one per call would redo
    /// the same reflection work on every read and write for no benefit.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,

        // Names, not ordinals. Inserting a value into LanguagePreference would
        // otherwise silently repoint every stored "English" at whatever member
        // happened to land on index 1.
        Converters = { new JsonStringEnumConverter() },

        // The default encoder escapes every non-ASCII character to \uXXXX, which
        // turns a Chinese folder name in MeetingDesktopPath into unreadable noise
        // in a file users are expected to be able to inspect. The relaxed encoder
        // is unsafe only where the output is injected into HTML, and this file is
        // never rendered by anything; it is parsed by the same settings contract
        // that wrote it.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _settingsFilePath;
    private readonly Func<string, string> _readAllText;

    /// <summary>
    /// Creates a store bound to one file path. The file need not exist yet.
    /// </summary>
    /// <param name="settingsFilePath">
    /// Absolute path to the settings file. Its directory is created on the first
    /// successful save.
    /// </param>
    public SettingsStore(string settingsFilePath) : this(settingsFilePath, File.ReadAllText) { }

    internal SettingsStore(string settingsFilePath, Func<string, string> readAllText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);
        ArgumentNullException.ThrowIfNull(readAllText);
        _settingsFilePath = settingsFilePath;
        _readAllText = readAllText;
    }

    /// <summary>
    /// Loads the stored settings, or defaults when there is nothing trustworthy
    /// to load.
    /// </summary>
    /// <remarks>
    /// Never throws. A missing, empty, unreadable, or malformed file all yield
    /// <see cref="AppSettings"/> defaults, because the caller must still be able
    /// to start far enough to tell the user what went wrong.
    ///
    /// A file stamped with a newer <see cref="AppSettings.SchemaVersion"/> is
    /// refused rather than partially applied. The dangerous field is
    /// MeetingDesktopPath: a future build may store a path this build's swap and
    /// restore logic would not return to the user's real desktop, and half
    /// honouring a file is worse than ignoring it.
    /// </remarks>
    public AppSettings Load()
    {
        AppSettings? loaded;

        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new AppSettings();
            }

            string json = _readAllText(_settingsFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        }
        catch (Exception)
        {
            // Deliberately broad. Everything that can come out of this block -
            // JsonException, IOException and its subclasses,
            // UnauthorizedAccessException, SecurityException, a malformed path
            // argument - means the same thing to the caller: no settings are
            // available. Enumerating the types would only decide which of them
            // is allowed to break the "never throws" contract.
            return new AppSettings();
        }

        if (loaded is null || loaded.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            return new AppSettings();
        }

        return Normalize(loaded);
    }

    private static AppSettings Normalize(AppSettings loaded)
    {
        // JSON may explicitly contain null despite the model's non-null annotations.
        // Ignore incomplete profiles without losing unrelated preferences or rewriting the file.
        var profiles = (loaded.Profiles ?? [])
            .Where(profile => profile is not null &&
                !string.IsNullOrWhiteSpace(profile.Name) && !string.IsNullOrWhiteSpace(profile.Folder))
            .ToArray();
        return loaded with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            Profiles = profiles,
            AutomaticSnapshotRetentionDays = loaded.AutomaticSnapshotRetentionDays is 7 or 15 or 30
                ? loaded.AutomaticSnapshotRetentionDays : 0,
            ConfirmQuickSwitch = loaded.SchemaVersion < 2 ? false : loaded.ConfirmQuickSwitch,
        };
    }

    /// <summary>Checks an existing file before using its defaults to operate on the desktop.</summary>
    public AppSettings LoadForStartup()
    {
        AppSettings? existing = ReadExistingChecked();
        return existing is null ? new AppSettings() : Normalize(existing);
    }

    /// <summary>
    /// Writes <paramref name="settings"/>, replacing any existing file.
    /// </summary>
    /// <remarks>
    /// The write lands in a sibling temporary file that is then renamed over the
    /// target. A rename inside one volume is atomic, so a crash, a full disk, or
    /// a power cut mid-write leaves the previous settings intact instead of a
    /// truncated file that <see cref="Load"/> would have to discard.
    /// </remarks>
    /// <exception cref="IOException">The file could not be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">The destination is not writable.</exception>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Defaults returned for an unreadable or newer file are not permission
        // to overwrite the user's only copy when any preference is changed.
        _ = ReadExistingChecked();

        string? directory = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            // First run on a clean profile: the app data directory may not exist
            // yet, and a settings file is not worth failing over a missing parent.
            Directory.CreateDirectory(directory);
        }

        string tempPath = _settingsFilePath + ".tmp";

        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, SerializerOptions));
            File.Move(tempPath, _settingsFilePath, overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private AppSettings? ReadExistingChecked()
    {
        string json;
        try { json = _readAllText(_settingsFilePath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            AppSettings? existing = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            if (existing is null) throw new JsonException("Settings are empty.");
            if (existing.SchemaVersion > AppSettings.CurrentSchemaVersion)
                throw new IOException("Settings were created by a newer version. The original file was preserved.");
            return existing;
        }
        catch (JsonException ex)
        {
            throw new IOException("Settings are damaged or unsupported. The original file was preserved.", ex);
        }
    }

    /// <summary>
    /// Best-effort cleanup of the temporary file. A leftover is harmless - the
    /// next save overwrites it - so this must not mask the original failure.
    /// </summary>
    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
