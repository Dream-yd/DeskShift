using System.Globalization;
using MeetingDesktop.App.Localization;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Tests;

public sealed class LanguageFollowSystemTests
{
    [Fact]
    public void ChineseOverrideDoesNotBecomeTheSystemLanguage() => StaTest.Run(() =>
    {
        CultureInfo originalRegion = CultureInfo.CurrentCulture;
        CultureInfo originalUi = CultureInfo.CurrentUICulture;
        CultureInfo? originalDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var language = new LocalizationManager(CultureInfo.GetCultureInfo("en-US"));
            language.ApplyPreference(LanguagePreference.SimplifiedChinese);
            Assert.Equal("zh-Hans", language.Culture.Name);
            language.ApplyPreference(LanguagePreference.Auto);
            Assert.Equal("en", language.Culture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalRegion;
            CultureInfo.CurrentUICulture = originalUi;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefault;
        }
    });
}
