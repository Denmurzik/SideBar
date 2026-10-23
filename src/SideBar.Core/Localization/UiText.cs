namespace SideBar.Core.Localization;

/// <summary>Все строки UI одного языка. Параметры — {0}, {1}; набор номеров одинаков в RU и EN (тест).</summary>
public sealed record UiText
{
    public required string Autostart { get; init; }
    public required string Exit { get; init; }
    public required string VisibilityHidden { get; init; }
    public required string VisibilityOwnDevices { get; init; }
    public required string VisibilityEveryone { get; init; }
    public required string StackPaused { get; init; }
    public required string OwnMark { get; init; }
    public required string OwnDevice { get; init; }
    public required string CertsUnavailable { get; init; }
    public required string UnknownDevice { get; init; }
    public required string Accept { get; init; }
    public required string Decline { get; init; }
    public required string Open { get; init; }
    public required string ShowInFolder { get; init; }
    public required string Copy { get; init; }
    public required string OpenLink { get; init; }
    public required string WifiUnsupported { get; init; }
    public required string NotEnoughSpace { get; init; }
    public required string SenderCancelled { get; init; }
    public required string TimedOut { get; init; }
    public required string ReceivePublicNetwork { get; init; }
    public required string ReceiveNoLan { get; init; }
    public required string ReceiveFailed { get; init; }
    public required string TransferInterrupted { get; init; }
    public required string Searching { get; init; }
    public required string SendToDevice { get; init; }
    public required string Sent { get; init; }
    public required string SendFailed { get; init; }
    public required string SendRejected { get; init; }
    public required string SendCancelled { get; init; }
    public required string SendUnverified { get; init; }
    public required string SendTimedOut { get; init; }
    public required string NothingToSend { get; init; }
    public required string SettingsMenu { get; init; }
    public required string SettingsTitle { get; init; }
    public required string NavGeneral { get; init; }
    public required string NavQuickShare { get; init; }
    public required string NavPanel { get; init; }
    public required string NavShelf { get; init; }
    public required string NavAbout { get; init; }
    public required string On { get; init; }
    public required string Off { get; init; }
    public required string Change { get; init; }
    public required string Reset { get; init; }
    public required string AutostartDesc { get; init; }
    public required string LanguageTitle { get; init; }
    public required string LanguageDesc { get; init; }
    public required string LanguageAuto { get; init; }
    public required string CheckUpdatesTitle { get; init; }
    public required string CheckNow { get; init; }
    public required string CheckNowSoon { get; init; }
    public required string CheckingNow { get; init; }
    public required string UpToDate { get; init; }
    public required string CheckFailed { get; init; }
    public required string CheckFailedClosed { get; init; }
    public required string ReleasesTitle { get; init; }
    public required string ReleasesLink { get; init; }
    public required string VisibilityTitle { get; init; }
    public required string VisibilityDesc { get; init; }
    public required string PcNameTitle { get; init; }
    public required string PcNameDesc { get; init; }
    public required string ReceiveFolderTitle { get; init; }
    public required string DownloadsName { get; init; }
    public required string ReceiveFolderMissing { get; init; }
    public required string CertsTitle { get; init; }
    public required string CertsLoading { get; init; }
    public required string CertsMissing { get; init; }
    public required string CertsUnreadable { get; init; }
    public required string CertsFormat { get; init; }
    public required string CertsNoValid { get; init; }
    public required string CertsRefreshTitle { get; init; }
    public required string CertsRefreshDesc { get; init; }
    public required string CertsRefreshSoon { get; init; }
    public required string Refresh { get; init; }
    public required string OfficialPathTitle { get; init; }
    public required string OfficialPathMissing { get; init; }
    public required string DelayStandard { get; init; }
    public required string EdgeDelayTitle { get; init; }
    public required string EdgeDelayDesc { get; init; }
    public required string BounceTitle { get; init; }
    public required string BounceDesc { get; init; }
    public required string FullscreenTitle { get; init; }
    public required string FullscreenDesc { get; init; }
    public required string RetentionTitle { get; init; }
    public required string RetentionNever { get; init; }
    public required string RetentionDay { get; init; }
    public required string RetentionWeek { get; init; }
    public required string ClearShelfTitle { get; init; }
    public required string ClearShelfDesc { get; init; }
    public required string Clear { get; init; }
    public required string ClearSure { get; init; }
    public required string AboutLicense { get; init; }
    public required string AboutLicenseValue { get; init; }
    public required string AboutRepo { get; init; }
    public required string AboutRepoLink { get; init; }
    public required string RefreshCertsNow { get; init; }
    public required string RefreshDone { get; init; }
    public required string RefreshNotInstalled { get; init; }
    public required string RefreshOfficialOpen { get; init; }
    public required string RefreshDeferred { get; init; }
    public required string RefreshFailed { get; init; }
    public required string CertsStale { get; init; }
    public required string OfficialResident { get; init; }
    public required string OfficialLeftover { get; init; }
    public required string NoDevicesReason { get; init; }
    public required string OfficialAppHint { get; init; }
    public required string ImageFileStem { get; init; }
    public required string MoreConsentsFmt { get; init; }
    public required string FilesOneFmt { get; init; }
    public required string FilesFewFmt { get; init; }
    public required string FilesManyFmt { get; init; }
    public required string ReceivedFmt { get; init; }
    public required string ReceivedFromFmt { get; init; }
    public required string SharedLinkFmt { get; init; }
    public required string SharedTextFmt { get; init; }
    public required string PinFmt { get; init; }
    public required string WantsFilesAndTextFmt { get; init; }
    public required string WantsFilesFmt { get; init; }
    public required string WantsLinksFmt { get; init; }
    public required string WantsLinkFmt { get; init; }
    public required string WantsTextFmt { get; init; }
    public required string OnlyFirstFilesFmt { get; init; }
    public required string CheckUpdatesDescFmt { get; init; }
    public required string UpdateAvailableFmt { get; init; }
    public required string MsFmt { get; init; }
    public required string CertsLoadedFmt { get; init; }
    /// <summary>Формат даты в «действуют до»; применяется с InvariantCulture (в InvariantGlobalization месяцы всё равно английские).</summary>
    public required string CertDateFormat { get; init; }
    public required string CertsFailedFmt { get; init; }

    /// <summary>Правило множественного числа языка: живёт в таблице, чтобы язык и правило не расходились.</summary>
    public required PluralRule Plural { get; init; }

    /// <summary>Число с формой слова: русский — файл/файла/файлов, английский — file/files.</summary>
    public string Files(int n)
    {
        int m100 = n % 100, m10 = n % 10;
        string f = Plural == PluralRule.English ? (n == 1 ? FilesOneFmt : FilesManyFmt)
            : m100 is >= 11 and <= 14 ? FilesManyFmt : m10 == 1 ? FilesOneFmt : m10 is >= 2 and <= 4 ? FilesFewFmt : FilesManyFmt;
        return string.Format(f, n);
    }
}

public enum PluralRule { English, Russian }
