using System.Globalization;
using SideBar.Core.Localization;
using SideBar.Core.Settings;

namespace SideBar;

/// <summary>Фасад строк UI: язык выбирается один раз при старте (<see cref="UiLanguage.Resolve"/>), сами строки — в SideBar.Core.Localization.</summary>
static class Strings
{
    public const string AppName = "SideBar";
    // названия языков — на самом языке, не из таблиц (кириллица записана кодами: в App нет русского текста)
    public const string LanguageRu = "\u0420\u0443\u0441\u0441\u043A\u0438\u0439";   // Русский
    public const string LanguageEn = "English";

    public static UiText T { get; private set; } = UiTexts.Ru;

    /// <summary>Вызывается один раз в начале OnLaunched; смена языка в настройках действует после перезапуска.</summary>
    public static void Initialize(AppSettings settings)
    {
        var lang = UiLanguage.Resolve(settings, CultureInfo.CurrentUICulture);
#if DEBUG
        // скриншоты: SIDEBAR_UI_LANG=ru|en перекрывает и настройку, и язык Windows
        lang = Environment.GetEnvironmentVariable("SIDEBAR_UI_LANG") switch { "en" => Language.English, "ru" => Language.Russian, _ => lang };
#endif
        T = UiTexts.For(lang == Language.Russian ? "ru" : "en");
    }

    public static string Files(int n) => T.Files(n);
    public static string Autostart => T.Autostart;
    public static string Exit => T.Exit;
    public static string VisibilityHidden => T.VisibilityHidden;
    public static string VisibilityOwnDevices => T.VisibilityOwnDevices;
    public static string VisibilityEveryone => T.VisibilityEveryone;
    public static string StackPaused => T.StackPaused;
    public static string OwnMark => T.OwnMark;
    public static string OwnDevice => T.OwnDevice;
    public static string CertsUnavailable => T.CertsUnavailable;
    public static string UnknownDevice => T.UnknownDevice;
    public static string Accept => T.Accept;
    public static string Decline => T.Decline;
    public static string Open => T.Open;
    public static string ShowInFolder => T.ShowInFolder;
    public static string Copy => T.Copy;
    public static string OpenLink => T.OpenLink;
    public static string WifiUnsupported => T.WifiUnsupported;
    public static string NotEnoughSpace => T.NotEnoughSpace;
    public static string SenderCancelled => T.SenderCancelled;
    public static string TimedOut => T.TimedOut;
    public static string ReceivePublicNetwork => T.ReceivePublicNetwork;
    public static string ReceiveNoLan => T.ReceiveNoLan;
    public static string ReceiveFailed => T.ReceiveFailed;
    public static string TransferInterrupted => T.TransferInterrupted;
    public static string Searching => T.Searching;
    public static string SendToDevice => T.SendToDevice;
    public static string Sent => T.Sent;
    public static string SendFailed => T.SendFailed;
    public static string SendRejected => T.SendRejected;
    public static string SendCancelled => T.SendCancelled;
    public static string SendUnverified => T.SendUnverified;
    public static string SendTimedOut => T.SendTimedOut;
    public static string NothingToSend => T.NothingToSend;
    public static string SettingsMenu => T.SettingsMenu;
    public static string SettingsTitle => T.SettingsTitle;
    public static string NavGeneral => T.NavGeneral;
    public static string NavQuickShare => T.NavQuickShare;
    public static string NavPanel => T.NavPanel;
    public static string NavShelf => T.NavShelf;
    public static string NavAbout => T.NavAbout;
    public static string On => T.On;
    public static string Off => T.Off;
    public static string Change => T.Change;
    public static string Reset => T.Reset;
    public static string AutostartDesc => T.AutostartDesc;
    public static string LanguageTitle => T.LanguageTitle;
    public static string LanguageDesc => T.LanguageDesc;
    public static string LanguageAuto => T.LanguageAuto;
    public static string CheckUpdatesTitle => T.CheckUpdatesTitle;
    public static string CheckNow => T.CheckNow;
    public static string CheckNowSoon => T.CheckNowSoon;
    public static string CheckingNow => T.CheckingNow;
    public static string UpToDate => T.UpToDate;
    public static string CheckFailed => T.CheckFailed;
    public static string CheckFailedClosed => T.CheckFailedClosed;
    public static string ReleasesTitle => T.ReleasesTitle;
    public static string ReleasesLink => T.ReleasesLink;
    public static string VisibilityTitle => T.VisibilityTitle;
    public static string VisibilityDesc => T.VisibilityDesc;
    public static string PcNameTitle => T.PcNameTitle;
    public static string PcNameDesc => T.PcNameDesc;
    public static string ReceiveFolderTitle => T.ReceiveFolderTitle;
    public static string DownloadsName => T.DownloadsName;
    public static string ReceiveFolderMissing => T.ReceiveFolderMissing;
    public static string CertsTitle => T.CertsTitle;
    public static string CertsLoading => T.CertsLoading;
    public static string CertsMissing => T.CertsMissing;
    public static string CertsUnreadable => T.CertsUnreadable;
    public static string CertsFormat => T.CertsFormat;
    public static string CertsNoValid => T.CertsNoValid;
    public static string CertsRefreshTitle => T.CertsRefreshTitle;
    public static string CertsRefreshDesc => T.CertsRefreshDesc;
    public static string CertsRefreshSoon => T.CertsRefreshSoon;
    public static string Refresh => T.Refresh;
    public static string OfficialPathTitle => T.OfficialPathTitle;
    public static string OfficialPathMissing => T.OfficialPathMissing;
    public static string DelayStandard => T.DelayStandard;
    public static string EdgeDelayTitle => T.EdgeDelayTitle;
    public static string EdgeDelayDesc => T.EdgeDelayDesc;
    public static string BounceTitle => T.BounceTitle;
    public static string BounceDesc => T.BounceDesc;
    public static string FullscreenTitle => T.FullscreenTitle;
    public static string FullscreenDesc => T.FullscreenDesc;
    public static string RetentionTitle => T.RetentionTitle;
    public static string RetentionNever => T.RetentionNever;
    public static string RetentionDay => T.RetentionDay;
    public static string RetentionWeek => T.RetentionWeek;
    public static string ClearShelfTitle => T.ClearShelfTitle;
    public static string ClearShelfDesc => T.ClearShelfDesc;
    public static string Clear => T.Clear;
    public static string ClearSure => T.ClearSure;
    public static string AboutLicense => T.AboutLicense;
    public static string AboutLicenseValue => T.AboutLicenseValue;
    public static string AboutRepo => T.AboutRepo;
    public static string AboutRepoLink => T.AboutRepoLink;
    public static string RefreshCertsNow => T.RefreshCertsNow;
    public static string RefreshDone => T.RefreshDone;
    public static string RefreshNotInstalled => T.RefreshNotInstalled;
    public static string RefreshOfficialOpen => T.RefreshOfficialOpen;
    public static string RefreshDeferred => T.RefreshDeferred;
    public static string RefreshFailed => T.RefreshFailed;
    public static string CertsStale => T.CertsStale;
    public static string OfficialResident => T.OfficialResident;
    public static string OfficialLeftover => T.OfficialLeftover;
    public static string NoDevicesReason => T.NoDevicesReason;
    public static string OfficialAppHint => T.OfficialAppHint;
    public static string CloseOfficialApp => T.OfficialAppHint;

    public static string MoreConsents(int n) => string.Format(T.MoreConsentsFmt, n);
    public static string Received(int n) => string.Format(T.ReceivedFmt, Files(n));
    public static string ReceivedFrom(string device, int n) => string.Format(T.ReceivedFromFmt, device, Files(n));
    public static string SharedLink(string device, string domain) => string.Format(T.SharedLinkFmt, device, domain);
    public static string SharedText(string device) => string.Format(T.SharedTextFmt, device);
    public static string Pin(string pin) => string.Format(T.PinFmt, pin);

    public static string WantsToSend(string device, int files, int texts, bool onlyLinks) =>
        files > 0 ? string.Format(texts > 0 ? T.WantsFilesAndTextFmt : T.WantsFilesFmt, device, Files(files))
        : onlyLinks ? string.Format(texts > 1 ? T.WantsLinksFmt : T.WantsLinkFmt, device)
        : string.Format(T.WantsTextFmt, device);

    public static string OnlyFirstFiles => string.Format(T.OnlyFirstFilesFmt, Nearby.NearbyService.MaxFiles);
    public static string CheckUpdatesDesc(string version) => string.Format(T.CheckUpdatesDescFmt, version);
    public static string UpdateAvailable(string version) => string.Format(T.UpdateAvailableFmt, version);
    public static string Ms(int n) => string.Format(T.MsFmt, n);
    public static string CertsLoaded(int peers, string until) => string.Format(T.CertsLoadedFmt, peers, until);
    public static string CertsFailed(string reason) => string.Format(T.CertsFailedFmt, reason);
}
