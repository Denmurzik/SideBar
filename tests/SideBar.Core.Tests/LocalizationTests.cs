using System.Reflection;
using System.Text.RegularExpressions;
using SideBar.Core.Localization;

namespace SideBar.Core.Tests;

public class LocalizationTests
{
    static readonly (string Name, Func<UiText, string> Get)[] All =
    [
        ("Autostart", t => t.Autostart),
        ("Exit", t => t.Exit),
        ("VisibilityHidden", t => t.VisibilityHidden),
        ("VisibilityOwnDevices", t => t.VisibilityOwnDevices),
        ("VisibilityEveryone", t => t.VisibilityEveryone),
        ("StackPaused", t => t.StackPaused),
        ("OwnMark", t => t.OwnMark),
        ("OwnDevice", t => t.OwnDevice),
        ("CertsUnavailable", t => t.CertsUnavailable),
        ("UnknownDevice", t => t.UnknownDevice),
        ("Accept", t => t.Accept),
        ("Decline", t => t.Decline),
        ("Open", t => t.Open),
        ("ShowInFolder", t => t.ShowInFolder),
        ("Copy", t => t.Copy),
        ("OpenLink", t => t.OpenLink),
        ("WifiUnsupported", t => t.WifiUnsupported),
        ("NotEnoughSpace", t => t.NotEnoughSpace),
        ("SenderCancelled", t => t.SenderCancelled),
        ("TimedOut", t => t.TimedOut),
        ("ReceivePublicNetwork", t => t.ReceivePublicNetwork),
        ("ReceiveNoLan", t => t.ReceiveNoLan),
        ("ReceiveFailed", t => t.ReceiveFailed),
        ("TransferInterrupted", t => t.TransferInterrupted),
        ("Searching", t => t.Searching),
        ("SendToDevice", t => t.SendToDevice),
        ("Sent", t => t.Sent),
        ("SendFailed", t => t.SendFailed),
        ("SendRejected", t => t.SendRejected),
        ("SendCancelled", t => t.SendCancelled),
        ("SendUnverified", t => t.SendUnverified),
        ("SendTimedOut", t => t.SendTimedOut),
        ("NothingToSend", t => t.NothingToSend),
        ("SettingsMenu", t => t.SettingsMenu),
        ("SettingsTitle", t => t.SettingsTitle),
        ("NavGeneral", t => t.NavGeneral),
        ("NavQuickShare", t => t.NavQuickShare),
        ("NavPanel", t => t.NavPanel),
        ("NavShelf", t => t.NavShelf),
        ("NavAbout", t => t.NavAbout),
        ("On", t => t.On),
        ("Off", t => t.Off),
        ("Change", t => t.Change),
        ("Reset", t => t.Reset),
        ("AutostartDesc", t => t.AutostartDesc),
        ("LanguageTitle", t => t.LanguageTitle),
        ("LanguageDesc", t => t.LanguageDesc),
        ("LanguageAuto", t => t.LanguageAuto),
        ("CheckUpdatesTitle", t => t.CheckUpdatesTitle),
        ("CheckNow", t => t.CheckNow),
        ("CheckNowSoon", t => t.CheckNowSoon),
        ("CheckingNow", t => t.CheckingNow),
        ("UpToDate", t => t.UpToDate),
        ("CheckFailed", t => t.CheckFailed),
        ("CheckFailedClosed", t => t.CheckFailedClosed),
        ("ReleasesTitle", t => t.ReleasesTitle),
        ("ReleasesLink", t => t.ReleasesLink),
        ("VisibilityTitle", t => t.VisibilityTitle),
        ("VisibilityDesc", t => t.VisibilityDesc),
        ("PcNameTitle", t => t.PcNameTitle),
        ("PcNameDesc", t => t.PcNameDesc),
        ("ReceiveFolderTitle", t => t.ReceiveFolderTitle),
        ("DownloadsName", t => t.DownloadsName),
        ("ReceiveFolderMissing", t => t.ReceiveFolderMissing),
        ("CertsTitle", t => t.CertsTitle),
        ("CertsLoading", t => t.CertsLoading),
        ("CertsMissing", t => t.CertsMissing),
        ("CertsUnreadable", t => t.CertsUnreadable),
        ("CertsFormat", t => t.CertsFormat),
        ("CertsNoValid", t => t.CertsNoValid),
        ("CertsRefreshTitle", t => t.CertsRefreshTitle),
        ("CertsRefreshDesc", t => t.CertsRefreshDesc),
        ("CertsRefreshSoon", t => t.CertsRefreshSoon),
        ("Refresh", t => t.Refresh),
        ("OfficialPathTitle", t => t.OfficialPathTitle),
        ("OfficialPathMissing", t => t.OfficialPathMissing),
        ("DelayStandard", t => t.DelayStandard),
        ("EdgeDelayTitle", t => t.EdgeDelayTitle),
        ("EdgeDelayDesc", t => t.EdgeDelayDesc),
        ("BounceTitle", t => t.BounceTitle),
        ("BounceDesc", t => t.BounceDesc),
        ("FullscreenTitle", t => t.FullscreenTitle),
        ("FullscreenDesc", t => t.FullscreenDesc),
        ("RetentionTitle", t => t.RetentionTitle),
        ("RetentionNever", t => t.RetentionNever),
        ("RetentionDay", t => t.RetentionDay),
        ("RetentionWeek", t => t.RetentionWeek),
        ("ClearShelfTitle", t => t.ClearShelfTitle),
        ("ClearShelfDesc", t => t.ClearShelfDesc),
        ("Clear", t => t.Clear),
        ("ClearSure", t => t.ClearSure),
        ("AboutLicense", t => t.AboutLicense),
        ("AboutLicenseValue", t => t.AboutLicenseValue),
        ("AboutRepo", t => t.AboutRepo),
        ("AboutRepoLink", t => t.AboutRepoLink),
        ("RefreshCertsNow", t => t.RefreshCertsNow),
        ("RefreshDone", t => t.RefreshDone),
        ("RefreshNotInstalled", t => t.RefreshNotInstalled),
        ("RefreshOfficialOpen", t => t.RefreshOfficialOpen),
        ("RefreshDeferred", t => t.RefreshDeferred),
        ("RefreshFailed", t => t.RefreshFailed),
        ("CertsStale", t => t.CertsStale),
        ("OfficialResident", t => t.OfficialResident),
        ("OfficialLeftover", t => t.OfficialLeftover),
        ("NoDevicesReason", t => t.NoDevicesReason),
        ("OfficialAppHint", t => t.OfficialAppHint),
        ("ImageFileStem", t => t.ImageFileStem),
        ("MoreConsentsFmt", t => t.MoreConsentsFmt),
        ("FilesOneFmt", t => t.FilesOneFmt),
        ("FilesFewFmt", t => t.FilesFewFmt),
        ("FilesManyFmt", t => t.FilesManyFmt),
        ("ReceivedFmt", t => t.ReceivedFmt),
        ("ReceivedFromFmt", t => t.ReceivedFromFmt),
        ("SharedLinkFmt", t => t.SharedLinkFmt),
        ("SharedTextFmt", t => t.SharedTextFmt),
        ("PinFmt", t => t.PinFmt),
        ("WantsFilesAndTextFmt", t => t.WantsFilesAndTextFmt),
        ("WantsFilesFmt", t => t.WantsFilesFmt),
        ("WantsLinksFmt", t => t.WantsLinksFmt),
        ("WantsLinkFmt", t => t.WantsLinkFmt),
        ("WantsTextFmt", t => t.WantsTextFmt),
        ("OnlyFirstFilesFmt", t => t.OnlyFirstFilesFmt),
        ("CheckUpdatesDescFmt", t => t.CheckUpdatesDescFmt),
        ("UpdateAvailableFmt", t => t.UpdateAvailableFmt),
        ("MsFmt", t => t.MsFmt),
        ("CertsLoadedFmt", t => t.CertsLoadedFmt),
        ("CertsFailedFmt", t => t.CertsFailedFmt),
    ];

    static readonly Regex Cyrillic = new("[А-Яа-яЁё]");
    static readonly Regex Placeholder = new(@"\{(\d+)\}");

    [Theory]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("de", false)]
    [InlineData("", false)]
    [InlineData("RU", true)]
    public void For_ru_is_russian_and_anything_else_is_english(string lang, bool russian) =>
        Assert.Same(russian ? UiTexts.Ru : UiTexts.En, UiTexts.For(lang));

    [Fact]
    public void For_null_is_english() => Assert.Same(UiTexts.En, UiTexts.For(null!));

    [Fact]
    public void List_covers_every_UiText_property()
    {
        var props = typeof(UiText).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).Order().ToArray();
        Assert.Equal(props, All.Select(a => a.Name).Order().ToArray());
    }

    [Fact]
    public void English_has_no_cyrillic()
    {
        foreach (var (name, get) in All)
            Assert.False(Cyrillic.IsMatch(get(UiTexts.En)), name);
    }

    [Fact]
    public void Russian_is_russian()
    {
        // проверка таблицы: хотя бы строки с текстом содержат кириллицу (не перепутаны таблицы)
        Assert.Matches(Cyrillic, UiTexts.Ru.Accept);
        Assert.Matches(Cyrillic, UiTexts.Ru.SettingsTitle);
    }

    [Fact]
    public void All_strings_non_empty_in_both()
    {
        foreach (var (name, get) in All)
        {
            Assert.False(string.IsNullOrWhiteSpace(get(UiTexts.Ru)), "ru " + name);
            Assert.False(string.IsNullOrWhiteSpace(get(UiTexts.En)), "en " + name);
        }
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        foreach (var (name, get) in All)
            Assert.Equal(Numbers(get(UiTexts.Ru)), Numbers(get(UiTexts.En)));
        static string Numbers(string s) => string.Join(",", Placeholder.Matches(s).Select(m => m.Groups[1].Value).Distinct().Order());
    }

    [Fact]
    public void Format_strings_format_without_throwing_in_both()
    {
        foreach (var (name, get) in All)
            foreach (var t in new[] { UiTexts.Ru, UiTexts.En })
                Assert.NotNull(string.Format(get(t), "a", "b"));
    }

    [Theory]
    [InlineData(1, "1 файл", "1 file")]
    [InlineData(2, "2 файла", "2 files")]
    [InlineData(5, "5 файлов", "5 files")]
    [InlineData(11, "11 файлов", "11 files")]
    [InlineData(21, "21 файл", "21 files")]
    [InlineData(0, "0 файлов", "0 files")]
    public void Files_plural_forms(int n, string ru, string en)
    {
        Assert.Equal(ru, UiTexts.Ru.Files(n));
        Assert.Equal(en, UiTexts.En.Files(n));
    }
}
