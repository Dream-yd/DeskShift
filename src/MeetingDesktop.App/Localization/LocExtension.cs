using System.Windows.Data;
using System.Windows.Markup;

namespace MeetingDesktop.App.Localization;

/// <summary>
/// Resolves a resource key to a localized string, and keeps it up to date when
/// the language changes.
/// </summary>
/// <remarks>
/// <code>&lt;Button Content="{loc:Loc PrepareButton}" /&gt;</code>
///
/// <para>
/// This returns a <see cref="Binding"/> rather than a plain string, and that
/// is the entire point. The obvious approach -
/// <c>{x:Static loc:Strings.PrepareButton}</c> - compiles, is fast, and is
/// evaluated exactly once when the XAML is parsed. It therefore never updates
/// when the language changes, which is how bilingual WPF apps end up telling
/// users to restart. A binding against the manager's indexer re-evaluates
/// whenever <see cref="LocalizationManager"/> raises the indexer notification.
/// </para>
/// </remarks>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    /// <summary>The resource key, e.g. <c>PrepareButton</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
        {
            return string.Empty;
        }

        // The binding path is " [Key] ". A key containing ']' would terminate
        // the indexer early and bind to the wrong thing, so reject it loudly
        // at design time rather than shipping a label that reads '!key!'.
        if (Key.Contains(']') || Key.Contains('['))
        {
            throw new ArgumentException(
                $"Resource key '{Key}' contains a bracket, which a WPF indexer binding cannot express.",
                nameof(Key));
        }

        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}
