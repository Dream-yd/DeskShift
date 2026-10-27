using System.ComponentModel;
using System.Globalization;
using System.Resources;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Localization;

/// <summary>
/// Looks up UI strings for the current language, and notifies bindings when
/// the language changes.
/// </summary>
/// <remarks>
/// Strings are reached through an indexer rather than a generated class so
/// that a language switch can refresh every binding at once. See
/// <see cref="SetCulture"/>.
/// </remarks>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    /// <summary>
    /// Matches <c>Resources/Strings/Strings.resx</c> under the app's root
    /// namespace. The trailing <c>Strings</c> is the file's base name.
    /// </summary>
    private const string ResourceBaseName = "MeetingDesktop.App.Resources.Strings.Strings";

    private static readonly ResourceManager Resources = new(ResourceBaseName, typeof(LocalizationManager).Assembly);

    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");
    private readonly CultureInfo _systemUiCulture;

    private LocalizationManager() : this(CultureInfo.CurrentUICulture) { }
    internal LocalizationManager(CultureInfo systemUiCulture) => _systemUiCulture = systemUiCulture;

    public static LocalizationManager Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture => _culture;

    /// <summary>
    /// The string for <paramref name="key"/>, or <c>!key!</c> when it is missing.
    /// </summary>
    /// <remarks>
    /// A missing key returns a visible marker rather than an empty string.
    /// A blank label on a projector is much harder to diagnose than
    /// <c>!PrepareButton!</c>, and the unit test that asserts both resx files
    /// expose the same key set is what keeps it from ever being seen.
    /// </remarks>
    public string this[string key] =>
        string.IsNullOrEmpty(key)
            ? string.Empty
            : Resources.GetString(key, _culture) ?? $"!{key}!";

    /// <summary>Formats a resource string with arguments, using the current culture.</summary>
    public string Format(string key, params object?[] args) =>
        string.Format(_culture, this[key], args);

    /// <summary>
    /// Picks the culture to start in.
    /// </summary>
    /// <remarks>
    /// An explicit user choice wins. Otherwise the rule is: Chinese if either
    /// the UI culture or the region is Chinese, English otherwise.
    ///
    /// Checking only <c>CurrentUICulture</c> is a mistake worth spelling out,
    /// because it looks right. Windows lets the display language and the
    /// region be set independently, so a Chinese-region machine set to an
    /// English display language reports <c>CurrentUICulture = en-US</c> while
    /// <c>CurrentCulture = zh-CN</c>. Trusting the UI culture alone ships an
    /// English UI to a user reading Chinese documentation.
    /// </remarks>
    public static CultureInfo ResolveStartupCulture(LanguagePreference preference) => preference switch
    {
        LanguagePreference.English => CultureInfo.GetCultureInfo("en"),
        LanguagePreference.SimplifiedChinese => CultureInfo.GetCultureInfo("zh-Hans"),
        _ => ResolveAutomaticCulture(CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture),
    };

    private static CultureInfo ResolveAutomaticCulture(CultureInfo systemUiCulture, CultureInfo region)
    {
        if (IsChinese(systemUiCulture) || IsChinese(region))
        {
            // zh-Hans, not zh-CN: resource fallback is zh-CN -> zh-Hans -> zh ->
            // neutral, so a file named for zh-CN would miss zh-SG and zh-MO.
            return CultureInfo.GetCultureInfo("zh-Hans");
        }

        return CultureInfo.GetCultureInfo("en");

        static bool IsChinese(CultureInfo culture)
        {
            // Neutral cultures have an empty Name, so compare the parent too.
            string name = culture.Name.Length > 0 ? culture.Name : culture.TwoLetterISOLanguageName;
            return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Switches language and republishes every bound string.
    /// </summary>
    /// <remarks>
    /// Sets <c>CurrentUICulture</c> only - never <c>CurrentCulture</c>.
    /// <c>CurrentCulture</c> governs number, date and sort formatting, and
    /// overwriting it to make resources resolve would silently change how the
    /// whole app formats dates and orders icon names.
    ///
    /// The <c>"Item[]"</c> notification is <c>Binding.IndexerName</c>: raising
    /// it refreshes every indexer binding in one go, which is what makes the
    /// <c>{loc:Loc}</c> markup extension update live instead of requiring a
    /// restart.
    /// </remarks>
    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        if (Equals(_culture, culture))
        {
            return;
        }

        _culture = culture;

        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

        // Broad refresh as well, for anything bound to a plain property.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    /// <summary>Applies a stored preference.</summary>
    public void ApplyPreference(LanguagePreference preference) => SetCulture(preference == LanguagePreference.Auto
        ? ResolveAutomaticCulture(_systemUiCulture, CultureInfo.CurrentCulture)
        : ResolveStartupCulture(preference));

    internal static string? RawString(string key, CultureInfo culture) =>
        Resources.GetString(key, culture);

    /// <summary>Every key present in the neutral (English) resource.</summary>
    internal static IReadOnlyCollection<string> AllKeys()
    {
        ResourceSet? set = Resources.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, true);
        return set is null
            ? Array.Empty<string>()
            : set.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToList();
    }
}
