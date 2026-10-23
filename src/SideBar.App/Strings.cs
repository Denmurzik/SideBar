namespace SideBar;

/// <summary>Строки UI (RU; EN — в M7).</summary>
static class Strings
{
    public const string AppName = "SideBar";
    public const string Autostart = "Запускать при входе в Windows";
    public const string Exit = "Выход";

    public const string VisibilityHidden = "Скрыто";
    public const string VisibilityOwnDevices = "Свои устройства";
    public const string VisibilityEveryone = "Все (10 мин)";
    public const string StackPaused = "Quick Share на паузе — открыт официальный Quick Share";
    public const string OwnMark = "своё";
    /// <summary>Имя своего устройства, если в сертификате его нет: имя из рекламы задаёт пир и сюда не попадает.</summary>
    public const string OwnDevice = "Ваше устройство";
    public const string CertsUnavailable = "Сертификаты Quick Share недоступны — работает только режим „Все“";
    public const string UnknownDevice = "Устройство";
    public const string Accept = "Принять";
    public const string Decline = "Отклонить";
    public const string Open = "Открыть";
    public const string ShowInFolder = "Показать в папке";
    public const string Copy = "Копировать";
    public const string OpenLink = "Открыть ссылку";
    public static string MoreConsents(int n) => $"ещё {n}";
    public const string WifiUnsupported = "Передача Wi-Fi-сетей не поддерживается";
    public const string NotEnoughSpace = "Недостаточно места на диске для приёма";
    public const string SenderCancelled = "Отправитель отменил передачу";
    public const string TimedOut = "Время ожидания истекло, передача отклонена";
    public const string ReceivePublicNetwork = "Сеть общедоступная — приём выключен";
    public const string ReceiveNoLan = "Нет подключения к локальной сети";
    public const string ReceiveFailed = "Не удалось получить данные";

    public static string Files(int n)
    {
        int m100 = n % 100, m10 = n % 10;
        string word = m100 is >= 11 and <= 14 ? "файлов" : m10 == 1 ? "файл" : m10 is >= 2 and <= 4 ? "файла" : "файлов";
        return $"{n} {word}";
    }

    public static string Received(int n) => $"Получено: {Files(n)}";
    public static string ReceivedFrom(string device, int n) => $"{device}: получено {Files(n)}";

    public static string SharedLink(string device, string domain) => $"{device} поделился ссылкой · {domain}";
    public static string SharedText(string device) => $"{device} поделился текстом";
    public const string TransferInterrupted = "Передача прервана";

    public static string Pin(string pin) => $"PIN {pin}";

    public static string WantsToSend(string device, int files, int texts, bool onlyLinks)
    {
        string what = files > 0
            ? texts > 0 ? $"отправить {Files(files)} и текст" : $"отправить {Files(files)}"
            : onlyLinks ? (texts > 1 ? "поделиться ссылками" : "поделиться ссылкой") : "поделиться текстом";
        return $"{device} хочет {what}";
    }
    public const string Searching = "Поиск устройств…";
    public const string SendToDevice = "Отправить на устройство";
    public const string Sent = "Отправлено";
    public const string SendFailed = "Не удалось отправить";
    public const string SendRejected = "Получатель отклонил";
    public const string SendCancelled = "Получатель отменил передачу";
    public static readonly string OnlyFirstFiles = $"Отправлены только первые {Nearby.NearbyService.MaxFiles} файлов";
    public const string SendUnverified = "Не удалось подтвердить устройство";
    public const string SendTimedOut = "Получатель не ответил";
    public const string CloseOfficialApp = SideBar.Core.Nearby.ShareTileModel.OfficialAppHint;
    public const string NothingToSend = "Нечего отправлять";
    // окно настроек
    public const string SettingsMenu = "Настройки…";
    public const string SettingsTitle = "Настройки SideBar";
    public const string NavGeneral = "Основное";
    public const string NavQuickShare = "Quick Share";
    public const string NavPanel = "Панель";
    public const string NavShelf = "Временный буфер";
    public const string NavAbout = "О программе";
    public const string On = "Вкл";
    public const string Off = "Выкл";
    public const string Change = "Изменить…";
    public const string Reset = "Сбросить";

    public const string AutostartDesc = "SideBar будет открываться вместе с системой";
    public const string LanguageTitle = "Язык";
    public const string LanguageDesc = "Применится после перезапуска";
    public const string LanguageAuto = "Авто";
    public const string LanguageRu = "Русский";
    public const string LanguageEn = "English";
    public const string CheckUpdatesTitle = "Проверять обновления";
    public static string CheckUpdatesDesc(string version) => $"Версия {version}";
    public const string CheckNow = "Проверить сейчас";
    public const string CheckNowSoon = "Появится после обновления";
    public const string CheckingNow = "Проверяем…";
    public const string UpToDate = "Установлена последняя версия";
    public const string CheckFailed = "Не удалось проверить";
    public const string CheckFailedClosed = "Не удалось проверить (репозиторий закрыт)";
    public static string UpdateAvailable(string version) => $"Доступна версия {version}";
    public const string ReleasesTitle = "Страница релизов";
    public const string ReleasesLink = "Открыть на GitHub";

    public const string VisibilityTitle = "Видимость";
    public const string VisibilityDesc = "Кому ваш компьютер показывается в Quick Share";
    public const string PcNameTitle = "Имя этого ПК";
    public const string PcNameDesc = "Так компьютер видят другие устройства (до 32 символов)";
    public const string ReceiveFolderTitle = "Папка для принятых файлов";
    public const string DownloadsName = "Загрузки";
    public const string ReceiveFolderMissing = "Папка недоступна — файлы принимаются в «Загрузки»";
    public const string CertsTitle = "Сертификаты Quick Share";
    public static string CertsLoaded(int peers, string until) => $"загружены · устройств {peers} · действуют до {until}";
    public static string CertsFailed(string reason) => $"недоступны: {reason}";
    public const string CertsLoading = "загружаются…";
    public const string CertsMissing = "Quick Share не найден или вы не входили в аккаунт";
    public const string CertsUnreadable = "файлы заняты или недоступны";
    public const string CertsFormat = "неизвестный формат файлов";
    public const string CertsNoValid = "нет действующего сертификата";
    public const string CertsRefreshTitle = "Обновить сертификаты сейчас";
    public const string CertsRefreshDesc = "Перечитать сертификаты официального Quick Share";
    public const string CertsRefreshSoon = "появится после обновления";
    public const string Refresh = "Обновить";
    public const string OfficialPathTitle = "Путь к официальному Quick Share";
    public const string OfficialPathMissing = "Файл не найден — официальное приложение не будет запускаться";
    public const string ExeFilter = "Приложение";

    public const string EdgeDelayTitle = "Задержка открытия у края";
    public const string EdgeDelayDesc = "Сколько курсор держится у края, прежде чем карточка откроется";
    public static string Ms(int n) => n == 0 ? "Сразу" : $"{n} мс";
    public const string BounceTitle = "Подпрыгивать при входящих";
    public const string BounceDesc = "Скрытая карточка привлекает внимание, когда приходит запрос или файл";
    public const string FullscreenTitle = "Показывать в полноэкранных приложениях";
    public const string FullscreenDesc = "Перетаскивание файла к краю работает всегда";

    public const string RetentionTitle = "Автоматически убирать файлы из буфера через";
    public const string RetentionNever = "Никогда";
    public const string RetentionDay = "1 день";
    public const string RetentionWeek = "7 дней";
    public const string ClearShelfTitle = "Очистить буфер";
    public const string ClearShelfDesc = "Убрать все файлы из временного буфера";
    public const string Clear = "Очистить";
    public const string ClearSure = "Точно очистить?";

    public const string AboutVersion = "Версия";
    public const string AboutLicense = "Лицензия";
    public const string AboutLicenseValue = "GNU GPLv3";
    public const string AboutRepo = "Репозиторий";
    public const string AboutRepoLink = "github.com/Denmurzik/SideBar";

    public const string RefreshCertsNow = "Обновить сертификаты сейчас";
    public const string RefreshDone = "Сертификаты обновлены";
    public const string RefreshNotInstalled = "Quick Share не установлен";
    public const string RefreshOfficialOpen = "Quick Share уже открыт";
    public const string RefreshDeferred = "Обновление отложено";
    public const string RefreshFailed = "Не удалось обновить";
    public const string CertsStale = "Не удаётся обновить сертификаты Quick Share — откройте официальное приложение Quick Share один раз";
    public const string OfficialResident = "Официальный Quick Share запущен — SideBar на паузе. Закройте его в трее, чтобы SideBar принимал файлы";
    public const string OfficialLeftover = "Обновление сертификатов не завершилось — официальный Quick Share остался открытым. Закройте его в трее, чтобы SideBar принимал файлы";
}
